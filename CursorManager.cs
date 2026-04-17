namespace OsuCursorOverlay;

public sealed class CursorManager : IDisposable
{
    private bool _disposed = false;

    public CursorManager()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreSystemCursors();
    }

    public void HideSystemCursors()
    {
        foreach (var cursorId in NativeMethods.CursorTypeIds)
        {
            var blank = CreateBlankCursor();
            if (blank != IntPtr.Zero)
            {
                NativeMethods.SetSystemCursor(blank, cursorId);
            }
        }
    }

    public void RestoreSystemCursors()
    {
        NativeMethods.SystemParametersInfoW(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            RestoreSystemCursors();
            _disposed = true;
        }
    }

    private static IntPtr CreateBlankCursor()
    {
        byte[] andMask = new byte[128];
        byte[] xorMask = new byte[128];

        // All bits set to 1 (transparent)
        for (int i = 0; i < 128; i++)
        {
            andMask[i] = 0xFF;
            xorMask[i] = 0x00;
        }

        return NativeMethods.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
    }
}
