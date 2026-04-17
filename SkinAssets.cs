using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace OsuCursorOverlay;

public sealed class SkinAssets : IDisposable
{
    public Bitmap Cursor { get; private set; }
    public Bitmap? Trail { get; private set; }
    public Bitmap? Middle { get; private set; }

    private SkinAssets()
    {
        Cursor = null!;
    }

    public static SkinAssets Load(SkinFiles files, float cursorScale)
    {
        var assets = new SkinAssets
        {
            Cursor = LoadRaw(files.CursorPath) ?? GenerateFallbackCursor(),
            // osu! behavior: if no cursortrail.png, the trail uses the cursor.png itself.
            // null means use cursor bitmap as fallback (resolved in TrailRenderer).
            Trail = files.TrailPath != null ? LoadRaw(files.TrailPath) : null,
            Middle = files.MiddlePath != null ? LoadRaw(files.MiddlePath) : null
        };

        if (cursorScale != 1.0f)
        {
            var oldCursor = assets.Cursor;
            assets.Cursor = ScaleBitmap(assets.Cursor, cursorScale);
            oldCursor.Dispose();

            // Also scale the trail if it exists
            if (assets.Trail != null)
            {
                var oldTrail = assets.Trail;
                assets.Trail = ScaleBitmap(assets.Trail, cursorScale);
                oldTrail.Dispose();
            }
        }

        return assets;
    }

    public void Dispose()
    {
        Cursor?.Dispose();
        Trail?.Dispose();
        Middle?.Dispose();
    }

    private static Bitmap? LoadRaw(string? path)
    {
        if (path == null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var src = new Bitmap(path);
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(
                    src, 
                    new Rectangle(0, 0, src.Width, src.Height),
                    0, 0, src.Width, src.Height, 
                    GraphicsUnit.Pixel);
            }
            src.Dispose();

            if (Path.GetFileNameWithoutExtension(path).EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
            {
                var halved = ScaleBitmap(dst, 0.5f);
                dst.Dispose();
                return halved;
            }

            return dst;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap GenerateFallbackCursor()
    {
        var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // White ring
            using (var pen = new Pen(Color.White, 2))
            {
                g.DrawEllipse(pen, 2, 2, 12, 12);
            }

            // Pink dot
            using (var brush = new SolidBrush(Color.FromArgb(255, 100, 160)))
            {
                g.FillEllipse(brush, 6, 6, 4, 4);
            }
        }

        return bmp;
    }

    private static Bitmap GenerateFallbackTrail()
    {
        var bmp = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // White circle
            using (var brush = new SolidBrush(Color.FromArgb(255, 255, 255)))
            {
                g.FillEllipse(brush, 1, 1, 6, 6);
            }
        }

        return bmp;
    }

    private static Bitmap ScaleBitmap(Bitmap src, float scale)
    {
        int newWidth = Math.Max(1, (int)(src.Width * scale));
        int newHeight = Math.Max(1, (int)(src.Height * scale));

        var scaled = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(
                src,
                new Rectangle(0, 0, newWidth, newHeight),
                0, 0, src.Width, src.Height,
                GraphicsUnit.Pixel);
        }

        return scaled;
    }
}
