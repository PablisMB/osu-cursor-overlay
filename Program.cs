using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OsuCursorOverlay;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Watchdog mode: launched by the main instance to monitor for forced termination.
        // Usage: OsuCursorOverlay.exe --watchdog <parentPid> <overlayHwnd>
        if (args.Length >= 2 && args[0] == "--watchdog")
        {
            if (int.TryParse(args[1], out int parentPid))
            {
                IntPtr hwnd = IntPtr.Zero;
                if (args.Length >= 3 && long.TryParse(args[2], out long hwndValue))
                    hwnd = new IntPtr(hwndValue);
                RunWatchdog(parentPid, hwnd);
            }
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Find skins directory
        var skinsDir = SkinDiscovery.FindSkinsDirectory();
        if (skinsDir == null)
        {
            MessageBox.Show(
                "ERROR: No se encontró la carpeta de skins de osu!\n\nAsegúrate de que osu! esté instalado en tu sistema.",
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // List available skins
        var skins = SkinDiscovery.ListSkins(skinsDir);
        if (skins.Count == 0)
        {
            MessageBox.Show(
                "ERROR: No hay skins disponibles con cursor.png\n\nVerifica: " + skinsDir,
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // Show skin selector
        using var selectorForm = new SkinSelectorForm(skinsDir, skins);
        if (selectorForm.ShowDialog() != DialogResult.OK)
            return;

        var selectedSkinName = selectorForm.SelectedSkinName;
        var selectedSkinPath = selectorForm.SelectedSkinPath;

        if (string.IsNullOrEmpty(selectedSkinName) || string.IsNullOrEmpty(selectedSkinPath))
            return;

        // Load config
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.ini");
        var settings = ConfigManager.Load(configPath);

        // NOTE: Watchdog is spawned from OverlayForm.OnLoad where we have the HWND.

        // Load skin files and assets
        var skinFiles = SkinDiscovery.GetSkinFiles(selectedSkinPath);
        var assets = SkinAssets.Load(skinFiles, settings.Scale);

        // Run overlay
        using var overlayForm = new OverlayForm(settings, assets, selectedSkinName);
        Application.Run(overlayForm);
    }

    /// <summary>
    /// Launches a hidden copy of this exe as a cursor-restore watchdog.
    /// Passes PID + HWND so the watchdog can detect hangs AND forced kills.
    /// </summary>
    public static void SpawnWatchdog(IntPtr overlayHwnd)
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null) return;

            var pid  = Environment.ProcessId.ToString();
            var hwnd = overlayHwnd.ToInt64().ToString();
            var psi = new ProcessStartInfo(exePath, $"--watchdog {pid} {hwnd}")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
                WindowStyle     = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
        catch
        {
            // Non-fatal: graceful-exit path still restores cursors normally.
        }
    }

    /// <summary>
    /// Watchdog mode: polls every 3 s.
    /// • If parent has terminated → restore cursor.
    /// • If parent is hung (IsHungAppWindow returns true) → force-kill it, restore cursor.
    /// </summary>
    private static void RunWatchdog(int parentPid, IntPtr overlayHwnd)
    {
        const uint SYNCHRONIZE       = 0x00100000;
        const uint PROCESS_TERMINATE = 0x0001;
        const uint WAIT_OBJECT_0     = 0x00000000;
        const int  POLL_MS           = 3000;   // check every 3 s
        const int  HUNG_CHECKS       = 2;      // kill after 2 consecutive hung checks (~6 s)

        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE, false, (uint)parentPid);
            if (hProcess == IntPtr.Zero)
            {
                // Parent already dead
                RestoreCursors();
                return;
            }

            int hungCount = 0;

            while (true)
            {
                uint result = WaitForSingleObject(hProcess, (uint)POLL_MS);

                if (result == WAIT_OBJECT_0)
                {
                    // Parent exited normally or was killed
                    break;
                }

                // result == WAIT_TIMEOUT → still alive; check if hung
                if (overlayHwnd != IntPtr.Zero && IsHungAppWindow(overlayHwnd))
                {
                    hungCount++;
                    if (hungCount >= HUNG_CHECKS)
                    {
                        // App is stuck: force-kill it then restore cursor
                        TerminateProcess(hProcess, 1);
                        break;
                    }
                }
                else
                {
                    hungCount = 0; // app is responsive, reset counter
                }
            }
        }
        catch { /* ignore */ }
        finally
        {
            if (hProcess != IntPtr.Zero)
                CloseHandle(hProcess);
        }

        RestoreCursors();
    }

    private static void RestoreCursors()
    {
        // SPI_SETCURSORS = 0x0057 — reloads system cursors from the registry
        SystemParametersInfoW(0x0057, 0, IntPtr.Zero, 0);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
}
