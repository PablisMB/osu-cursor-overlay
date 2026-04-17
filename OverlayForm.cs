using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OsuCursorOverlay;

public sealed class OverlayForm : Form
{
    private readonly AppSettings _settings;
    private readonly SkinAssets _assets;
    private readonly CursorManager _cursorManager;
    private readonly string _skinName;

    private TrailRenderer? _trailRenderer;
    private Bitmap? _backBuffer;
    private readonly object _renderLock = new();
    private Thread? _renderThread;
    private volatile bool _stopRender = false;
    private volatile bool _paused = false;

    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _trayMenu;

    private const int HOTKEY_ID = 1;
    private bool _hotkeyRegistered = false;

    public OverlayForm(AppSettings settings, SkinAssets assets, string skinName)
    {
        _settings = settings;
        _assets = assets;
        _skinName = skinName;
        _cursorManager = new CursorManager();

        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        TopMost = true;
        ShowInTaskbar = false;
        // BackColor = Color.Black;
        // TransparencyKey = Color.Black;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(0, 0);

        var screen = Screen.PrimaryScreen;
        if (screen != null)
        {
            Size = screen.Bounds.Size;
        }

        Shown += OverlayForm_Shown;
        FormClosing += OverlayForm_FormClosing;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        ConfigureOverlayWindow();
        RegisterExitHotkey();
        BuildTrayIcon();

        // Spawn watchdog here so we can pass the overlay HWND.
        // The watchdog will restore the system cursor if we hang or are force-killed.
        Program.SpawnWatchdog(Handle);

        StartRenderThread();
    }

    private void OverlayForm_Shown(object? sender, EventArgs e)
    {
        if (_settings.HideSystemCursor)
        {
            _cursorManager.HideSystemCursors();
        }
    }

    private void OverlayForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        Shutdown();
    }

    private void ConfigureOverlayWindow()
    {
        var exStyle = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE);

        exStyle = new IntPtr(
            exStyle.ToInt64() |
            NativeMethods.WS_EX_LAYERED |
            NativeMethods.WS_EX_TRANSPARENT |
            NativeMethods.WS_EX_TOOLWINDOW |
            NativeMethods.WS_EX_NOACTIVATE);

        NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, exStyle);

        var screen = Screen.PrimaryScreen;
        if (screen != null)
        {
            var bounds = screen.Bounds;
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                0, 0,
                bounds.Width, bounds.Height,
                NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void RegisterExitHotkey()
    {
        if (NativeMethods.RegisterHotKey(Handle, HOTKEY_ID, _settings.HotkeyModifiers, (uint)_settings.HotkeyVKey))
        {
            _hotkeyRegistered = true;
        }
    }

    private void UnregisterExitHotkey()
    {
        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID);
            _hotkeyRegistered = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
        {
            Shutdown();
            return;
        }

        base.WndProc(ref m);
    }

    private void StartRenderThread()
    {
        _renderThread = new Thread(RenderLoop)
        {
            Name = "pygame-overlay",
            IsBackground = true
        };
        _renderThread.Start();
    }

    private void RenderLoop()
    {
        IntPtr hdcMemory = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOldBitmap = IntPtr.Zero;

        try
        {
            NativeMethods.timeBeginPeriod(1);

            long frameTicks = Stopwatch.Frequency / _settings.TargetFps;
            var sw = Stopwatch.StartNew();
            int topmostCounter = 0;  // re-assert topmost every N frames

            int w = Width;
            int h = Height;

            IntPtr hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
            hdcMemory = NativeMethods.CreateCompatibleDC(hdcScreen);

            var bmi = new NativeMethods.BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h; // Top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = NativeMethods.BI_RGB;

            hBitmap = NativeMethods.CreateDIBSection(hdcScreen, ref bmi, 0, out IntPtr pBits, IntPtr.Zero, 0);
            hOldBitmap = NativeMethods.SelectObject(hdcMemory, hBitmap);
            NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);

            _backBuffer = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, pBits);

            _trailRenderer = new TrailRenderer(
                _assets,
                _settings.TrailLength,
                _settings.TrailSpacing,
                _settings.MaxTrailAlpha,
                _settings.MinTrailScale,
                1.0f);  // bitmaps are already pre-scaled by SkinAssets

            while (!_stopRender)
            {
                long frameStart = sw.ElapsedTicks;

                if (!_paused)
                {
                    NativeMethods.GetCursorPos(out var pt);
                    var pos = new Point(pt.X, pt.Y);

                    lock (_renderLock)
                    {
                        if (_backBuffer != null && _trailRenderer != null)
                        {
                            using var g = Graphics.FromImage(_backBuffer);
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;

                            _trailRenderer.UpdateTrail(pos);
                            _trailRenderer.DrawFrame(g, pos, w, h);
                        }
                    }

                    try
                    {
                        IntPtr hdcDst = NativeMethods.GetDC(IntPtr.Zero);

                        var ptSrc = new NativeMethods.POINT { X = 0, Y = 0 };
                        // Ensure window location coordinates match desktop bounds for multiple monitors
                        var loc = Location; 
                        var ptLocation = new NativeMethods.POINT { X = loc.X, Y = loc.Y };
                        var size = new NativeMethods.SIZE { cx = w, cy = h };

                        var blend = new NativeMethods.BLENDFUNCTION
                        {
                            BlendOp = NativeMethods.AC_SRC_OVER,
                            BlendFlags = 0,
                            SourceConstantAlpha = 255,
                            AlphaFormat = NativeMethods.AC_SRC_ALPHA
                        };

                        bool success = NativeMethods.UpdateLayeredWindow(
                            Handle,
                            hdcDst,
                            ref ptLocation,
                            ref size,
                            hdcMemory,
                            ref ptSrc,
                            0,
                            ref blend,
                            NativeMethods.ULW_ALPHA);

                        if (!success)
                        {
                            int error = Marshal.GetLastWin32Error();
                            File.AppendAllText("error_log.txt", $"UpdateLayeredWindow failed: {error}\n");
                        }

                        NativeMethods.ReleaseDC(IntPtr.Zero, hdcDst);

                        // Re-assert HWND_TOPMOST every frame to stay above context menus
                        // and other topmost windows with minimal latency.
                        NativeMethods.SetWindowPos(
                            Handle,
                            NativeMethods.HWND_TOPMOST,
                            0, 0, 0, 0,
                            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

                        // Re-hide system cursors every ~1s (less critical, keep rate-limited)
                        topmostCounter++;
                        if (topmostCounter >= _settings.TargetFps)
                        {
                            topmostCounter = 0;
                            if (_settings.HideSystemCursor)
                                _cursorManager.HideSystemCursors();
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
                else
                {
                    Thread.Sleep(50);
                    continue;
                }

                long elapsed = sw.ElapsedTicks - frameStart;
                long remaining = frameTicks - elapsed;

                if (remaining > 0)
                {
                    long sleepMs = (remaining * 1000L / Stopwatch.Frequency) - 1;
                    if (sleepMs > 0)
                    {
                        Thread.Sleep((int)sleepMs);
                    }

                    while (sw.ElapsedTicks - frameStart < frameTicks)
                    {
                        // Spin wait
                    }
                }
            }

            NativeMethods.timeEndPeriod(1);
        }
        finally
        {
            _trailRenderer?.Dispose();
            _backBuffer?.Dispose();

            if (hOldBitmap != IntPtr.Zero && hdcMemory != IntPtr.Zero)
                NativeMethods.SelectObject(hdcMemory, hOldBitmap);
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);
            if (hdcMemory != IntPtr.Zero)
                NativeMethods.DeleteDC(hdcMemory);
        }
    }

    private void BuildTrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = GenerateTrayIcon(),
            Text = $"osu! Cursor Overlay — {_skinName}",
            Visible = true
        };

        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add(new ToolStripMenuItem("Pausar", null, OnTrayPauseResume));
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(new ToolStripMenuItem("Abrir Config", null, OnTrayOpenConfig));
        _trayMenu.Items.Add(new ToolStripMenuItem("Salir", null, OnTrayExit));

        _notifyIcon.ContextMenuStrip = _trayMenu;
    }

    private Icon GenerateTrayIcon()
    {
        var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // White ring
            using (var pen = new Pen(Color.White, 4))
            {
                g.DrawEllipse(pen, 8, 8, 48, 48);
            }

            // Pink dot
            using (var brush = new SolidBrush(Color.FromArgb(255, 100, 160)))
            {
                g.FillEllipse(brush, 24, 24, 16, 16);
            }
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    private void OnTrayPauseResume(object? sender, EventArgs e)
    {
        if (_paused)
        {
            _paused = false;
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOW);
            ConfigureOverlayWindow();
            if (_settings.HideSystemCursor)
            {
                _cursorManager.HideSystemCursors();
            }

            if (_trayMenu?.Items[0] is ToolStripMenuItem item)
            {
                item.Text = "Pausar";
            }
        }
        else
        {
            _paused = true;
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
            _cursorManager.RestoreSystemCursors();

            if (_trayMenu?.Items[0] is ToolStripMenuItem item)
            {
                item.Text = "Reanudar";
            }
        }
    }

    private void OnTrayOpenConfig(object? sender, EventArgs e)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.ini");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = configPath,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore if can't open
        }
    }

    private void OnTrayExit(object? sender, EventArgs e)
    {
        Shutdown();
    }

    private void Shutdown()
    {
        _stopRender = true;

        if (_renderThread != null && _renderThread.IsAlive)
        {
            _renderThread.Join(2000);
        }

        _cursorManager.RestoreSystemCursors();
        UnregisterExitHotkey();

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _trayMenu?.Dispose();

        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cursorManager?.Dispose();
            _assets?.Dispose();
        }

        base.Dispose(disposing);
    }
}
