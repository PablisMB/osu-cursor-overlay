using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace OsuCursorOverlay;

/// <summary>
/// Reimplements osu!lazer CursorTrail.cs logic using GDI+ for Win32 overlay rendering.
/// 
/// Key design decisions matching the original:
/// - Interval between trail parts = Texture.DisplayWidth * CursorScale / 2.5f (NOT velocity-based)
/// - Each part stores its own Scale at spawn time (set by NewPartScale, analogous to cursor expand)
/// - Fade is time-based: parts fade over FadeDuration (300ms) using a FadeExponent of 1.7f
/// - The "time" system uses a normalized float: part.Time = time + 1, expired when (time - part.Time) >= 1
/// </summary>
public sealed class TrailRenderer : IDisposable
{
    // Matches max_sprites in CursorTrail.cs
    private const int MaxSprites = 2048;

    private struct TrailPart
    {
        public PointF Position;
        /// <summary>Normalized spawn time. The part is alive while (currentTime - Time) < 1.0</summary>
        public float Time;
        /// <summary>Scale of this part at spawn (corresponds to NewPartScale in osu!)</summary>
        public float Scale;
        /// <summary>-1 means this slot is unused.</summary>
        public int InvalidationID;
    }

    // Circular buffer of trail parts, exactly like osu!'s TrailPart[]
    private readonly TrailPart[] _parts = new TrailPart[MaxSprites];
    private int _currentIndex;

    // Time system: normalized float advancing at rate 1.0 per FadeDuration ms
    private double _timeOffset;  // Stopwatch ticks at which time=0
    private float _time;

    // Match osu! original: FadeDuration = 300ms, FadeExponent = 1.7
    private const double FadeDurationMs = 300.0;
    private const float FadeExponent   = 1.7f;

    // Interval = cursor diameter × trailSpacing multiplier
    private readonly float _interval;

    // Maximum number of visible trail parts (= trail_length config)
    private readonly int _maxActiveParts;

    // The scale factor applied globally (user setting, = CursorScale in osu!)
    private readonly float _globalScale;

    // The scale applied to each NEW part at spawn (= NewPartScale in osu!, driven by expand state)
    // We keep this at 1.0 since we don't simulate key-press cursor expansion
    private float _newPartScale = 1.0f;

    // Max alpha for trail parts
    private readonly int _maxTrailAlpha;

    private readonly Bitmap _trailBase;
    private readonly Bitmap _cursor;

    // Pre-built alpha ImageAttributes for all 256 values
    private readonly ImageAttributes[] _alphaAttribs;

    // Cached scaled version of the trail texture at globalScale
    private Bitmap? _cachedScaledTrail;

    // Track last position for interpolation
    private PointF? _lastPosition;

    public TrailRenderer(
        SkinAssets assets,
        int trailLength,
        float trailSpacing,
        int maxTrailAlpha,
        float minTrailScale,   // kept for config compat
        float globalScale = 1.0f)
    {
        _globalScale = globalScale;
        // max_trail_alpha is 0-100% → map to 0-255
        _maxTrailAlpha = Math.Clamp((int)(maxTrailAlpha / 100f * 255f), 0, 255);
        _maxActiveParts = Math.Clamp(trailLength, 1, MaxSprites);

        // osu! behavior: if no cursortrail.png, use cursor.png as trail texture
        _trailBase = assets.Trail ?? assets.Cursor;
        _cursor = assets.Cursor;

        // osu! real interval = cursortrail.DisplayWidth / 2.5
        // cursortrail.png is typically ~half the cursor width (32px vs 64px cursor)
        // So: interval ≈ cursor.Width / 2 / 2.5 = cursor.Width / 5
        // At 64px cursor: interval=12.8px → heavy overlap when moving slow → thick blob
        // trail_spacing multiplies: 1.0=osu default, 2.0=more spaced dots, 0.5=denser
        float baseInterval = _cursor.Width / 5.0f;
        _interval = Math.Max(2f, baseInterval * Math.Max(0.1f, trailSpacing));

        // Mark all slots as unused
        for (int i = 0; i < MaxSprites; i++)
            _parts[i].InvalidationID = -1;

        // Reset the time origin
        _timeOffset = Stopwatch.GetTimestamp();
        _time = 0f;

        // Pre-build ImageAttributes for alpha blending
        _alphaAttribs = new ImageAttributes[256];
        for (int i = 0; i < 256; i++)
        {
            var cm = new ColorMatrix { Matrix33 = i / 255f };
            var ia = new ImageAttributes();
            ia.SetColorMatrix(cm);
            _alphaAttribs[i] = ia;
        }
    }

    /// <summary>
    /// Call once per frame to update the internal time counter.
    /// Equivalent to CursorTrail.Update() in osu!
    /// </summary>
    private void UpdateTime()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedMs = (now - _timeOffset) / (double)Stopwatch.Frequency * 1000.0;
        _time = (float)(elapsedMs / FadeDurationMs);

        // Reset time origin periodically to avoid float precision issues (osu! threshold: 1000000)
        if (_time > 1_000_000)
        {
            for (int i = 0; i < MaxSprites; i++)
            {
                _parts[i].Time -= _time;
                if (_parts[i].InvalidationID != -1)
                    _parts[i].InvalidationID++;
            }
            _time = 0f;
            _timeOffset = now;
        }
    }

    /// <summary>
    /// Add trail parts for a new cursor position.
    /// Mirrors CursorTrail.AddTrail() + addPart() from osu!
    /// </summary>
    public void UpdateTrail(Point pos)
    {
        UpdateTime();

        var position = new PointF(pos.X, pos.Y);

        if (!_lastPosition.HasValue)
        {
            _lastPosition = position;
            return;
        }

        PointF pos1 = _lastPosition.Value;
        float dx = position.X - pos1.X;
        float dy = position.Y - pos1.Y;
        float distance = (float)Math.Sqrt(dx * dx + dy * dy);

        if (distance < 0.001f) return;

        float dirX = dx / distance;
        float dirY = dy / distance;

        // osu! loop: emit a part every _interval pixels along the path
        for (float d = _interval; d < distance; d += _interval)
        {
            var partPos = new PointF(pos1.X + dirX * d, pos1.Y + dirY * d);
            AddPart(partPos);
            _lastPosition = partPos;
        }
    }

    public bool HasActiveTrail()
    {
        for (int i = 0; i < MaxSprites; i++)
        {
            var p = _parts[i];
            if (p.InvalidationID != -1 && (_time - p.Time) < 1f)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Writes a new part into the circular buffer.
    /// Mirrors addPart() from osu!
    /// </summary>
    private void AddPart(PointF position)
    {
        _parts[_currentIndex].Position = position;
        _parts[_currentIndex].Time = _time + 1f;  // exact: parts[currentIndex].Time = time + 1
        _parts[_currentIndex].Scale = _newPartScale;
        _parts[_currentIndex].InvalidationID++;

        _currentIndex = (_currentIndex + 1) % MaxSprites;
    }

    /// <summary>
    /// Draw cursor trail then cursor on top.
    /// </summary>
    public void DrawFrame(Graphics g, Point cursorPos, int screenW, int screenH)
    {
        UpdateTime();
        g.Clear(Color.Transparent);
        DrawTrail(g);
        DrawCursor(g, cursorPos);
    }

    private void DrawCursor(Graphics g, Point cursorPos)
    {
        // Bitmap is already pre-scaled by SkinAssets; draw at native size
        int cursorW = _cursor.Width;
        int cursorH = _cursor.Height;
        int cursorX = cursorPos.X - cursorW / 2;
        int cursorY = cursorPos.Y - cursorH / 2;

        g.DrawImage(
            _cursor,
            new Rectangle(cursorX, cursorY, cursorW, cursorH),
            0, 0, _cursor.Width, _cursor.Height,
            GraphicsUnit.Pixel);
    }

    private void DrawTrail(Graphics g)
    {
        if (_trailBase == null) return;

        // Draw trail at full cursor size (no shrinking)
        int maxW = _cursor.Width;
        int maxH = _cursor.Height;
        // Pre-scale the trail texture to max size (cached)
        var scaledBmp = GetTrailBitmap(maxW, maxH);

        int drawn = 0;
        for (int i = 0; i < MaxSprites && drawn < _maxActiveParts; i++)
        {
            int idx = ((_currentIndex - 1 - i) % MaxSprites + MaxSprites) % MaxSprites;
            var part = _parts[idx];

            if (part.InvalidationID == -1) continue;

            float dt = _time - part.Time;
            if (dt >= 1f) continue;

            // timeAlive: 1.0 just spawned → 0.0 about to expire
            float timeAlive = part.Time - _time;

            // Alpha fade
            float fadeProgress = (float)Math.Pow(Math.Max(0f, timeAlive), FadeExponent);
            int alpha = Math.Clamp((int)(_maxTrailAlpha * fadeProgress), 0, 255);
            if (alpha < 4) continue;

            drawn++;

            int partW = _cursor.Width;
            int partH = _cursor.Height;

            int tx = (int)(part.Position.X - partW / 2f);
            int ty = (int)(part.Position.Y - partH / 2f);

            g.DrawImage(
                scaledBmp,
                new Rectangle(tx, ty, partW, partH),
                0, 0, scaledBmp.Width, scaledBmp.Height,
                GraphicsUnit.Pixel,
                _alphaAttribs[alpha]);
        }
    }

    private Bitmap GetTrailBitmap(int targetW, int targetH)
    {
        if (_cachedScaledTrail != null &&
            _cachedScaledTrail.Width == targetW &&
            _cachedScaledTrail.Height == targetH)
        {
            return _cachedScaledTrail;
        }

        _cachedScaledTrail?.Dispose();

        var scaled = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(scaled);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(
            _trailBase,
            new Rectangle(0, 0, targetW, targetH),
            0, 0, _trailBase.Width, _trailBase.Height,
            GraphicsUnit.Pixel);

        _cachedScaledTrail = scaled;
        return scaled;
    }

    public void Dispose()
    {
        _trailBase.Dispose();
        _cachedScaledTrail?.Dispose();
        foreach (var ia in _alphaAttribs)
            ia.Dispose();
    }

    private static Bitmap CreateFallbackTrail()
    {
        var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(180, 255, 255, 255));
        g.FillEllipse(brush, 2, 2, 12, 12);
        return bmp;
    }
}
