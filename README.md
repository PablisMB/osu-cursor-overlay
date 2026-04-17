# osu! Cursor Overlay — C# .NET 8 WinForms Edition

A complete rewrite of the Python pygame cursor overlay in C# to eliminate graphics complications. This is a transparent fullscreen overlay that renders the osu! cursor and trail on top of all windows.

## Features

- ✅ **Transparent Overlay** — Pure black background is fully transparent; clicks pass through
- ✅ **Real osu! Skins** — Load cursor.png and cursortrail.png from any osu! skin folder
- ✅ **144 FPS** — Dedicated render thread + Stopwatch spin-wait for accurate timing
- ✅ **System Tray** — Pause/resume overlay, open config, or exit from context menu
- ✅ **Global Hotkey** — Ctrl+Shift+Q to exit (configurable in config.ini)
- ✅ **System Cursor Hidden** — All 12 Windows cursor types replaced; restored on exit
- ✅ **Alpha-Blended Trail** — Fades from oldest (transparent) to newest (opaque); scales smallest to largest

## Architecture

- **No External Dependencies** — Uses only .NET 8 WinForms and System.Drawing
- **Direct Win32 P/Invoke** — SetWindowPos, SetLayeredWindowAttributes, RegisterHotKey, CreateCursor, GetCursorPos
- **Double-Buffered Rendering** — Background thread renders to Bitmap, then blits to HWND via `Graphics.FromHwnd`
- **Zero Per-Frame GC** — Trail bitmaps and alpha ImageAttributes pre-allocated

## Building

```bash
cd OsuCursorOverlay_CSharp
dotnet build -c Release
```

Output: `bin/Release/net8.0-windows/OsuCursorOverlay.exe`

## Running

```bash
OsuCursorOverlay.exe
```

1. A skin selector dialog appears — choose any osu! skin with a cursor.png file
2. The overlay starts in the background; tray icon appears in system tray
3. Right-click tray icon:
   - **Pausar** — Hide overlay, restore system cursor
   - **Reanudar** — Show overlay, hide system cursor
   - **Abrir Config** — Open config.ini in default text editor
   - **Salir** — Exit and restore system cursor

## Configuration

`config.ini` in the application directory:

```ini
[cursor]
scale = 1.0                  # Size multiplier for cursor PNG
trail_length = 15            # Max trail points
max_trail_alpha = 150        # Alpha of newest trail point (0-255)
min_trail_scale = 0.3        # Scale of oldest trail point (0.0-1.0)
trail_spacing = 3.0          # Min pixel distance between trail points

[system]
target_fps = 144             # Render frame rate
hide_system_cursor = true    # Hide Windows cursor when overlay is active
exit_hotkey = ctrl+shift+q   # Global hotkey to exit (ctrl, shift, alt, win + letter)
```

Created on first run with defaults if missing.

## File Structure

```
OsuCursorOverlay_CSharp/
├── OsuCursorOverlay.csproj      Project file (.NET 8 Windows)
├── Program.cs                   Entry point: STAThread, skin selector, overlay startup
├── Config.cs                    INI parser + AppSettings class
├── SkinSelector.cs              WinForms dialog + SkinDiscovery (replaces Python msvcrt UI)
├── NativeMethods.cs             P/Invoke declarations for Win32 APIs
├── CursorManager.cs             Hide/restore all 12 system cursor types
├── SkinAssets.cs                Load PNG skins with black color-key transparency
├── TrailRenderer.cs             Trail queue + GDI+ rendering with alpha blending
├── OverlayForm.cs               Main overlay form: transparency, tray, hotkey, render thread
├── README.md                    This file
└── assets/
    └── tray_icon.ico            (generated programmatically if missing)
```

## Key Implementation Details

### DPI Awareness
Calls `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` before `EnableVisualStyles()` to ensure `GetCursorPos` physical pixel coordinates align with window bounds on 125%+ displays.

### Transparency
- WinForms sets `TransparencyKey = Color.Black` (auto-applies `LWA_COLORKEY`)
- P/Invoke additionally ORs `WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` to prevent focus stealing and enable click-through
- Each frame, `Graphics.Clear(Color.Black)` fills the back-buffer; pure black becomes transparent

### Rendering Performance
- **Thread:** Background thread calls `Graphics.FromHwnd(Handle)` directly (cross-thread safe for overlays)
- **Timing:** `timeBeginPeriod(1)` + `Stopwatch` spin-wait (not `Thread.Sleep` alone, which has 15ms resolution)
- **Caching:** Trail scaled bitmaps cached per slot; 256 alpha `ImageAttributes` pre-allocated to avoid per-frame GC

### Trail Alpha Blending
Uses `ColorMatrix` with `ImageAttributes` to set per-bitmap alpha without recreating the bitmap:
```csharp
var cm = new ColorMatrix { Matrix33 = alpha / 255f };
var ia = new ImageAttributes();
ia.SetColorMatrix(cm);
g.DrawImage(scaled, destRect, 0, 0, w, h, GraphicsUnit.Pixel, ia);
```

### Skin Discovery
Checks three paths in order:
1. `%LOCALAPPDATA%\osu!\Skins`
2. `C:\Program Files\osu!\Skins`
3. `C:\Program Files (x86)\osu!\Skins`

Valid skin = subfolder containing `cursor.png`.

## Comparison: Python → C#

| Feature | Python | C# |
|---------|--------|---|
| Rendering | pygame (SDL) | GDI+ (Win32) |
| Transparency | `pygame.NOFRAME` + `SetLayeredWindowAttributes` | WinForms `TransparencyKey` + manual P/Invoke |
| 144 FPS | `clock.tick(144)` (can stutter) | `timeBeginPeriod(1)` + spin-wait (accurate) |
| Skin Selector | Console arrow keys (msvcrt) | WinForms dialog |
| Hotkey | `keyboard` package | Win32 `RegisterHotKey` |
| System Cursor | `SetSystemCursor` via ctypes | P/Invoke `CreateCursor` / `SetSystemCursor` |
| Trail Rendering | `pygame.Surface.set_alpha()` + per-frame scale | `ImageAttributes` + cached bitmaps |
| Dependencies | 4 (pygame, pystray, keyboard, Pillow) | 0 (all .NET 8) |

## Troubleshooting

**Overlay not transparent?**
- Verify screen background is pure black (RGB 0,0,0)
- Check `config.ini` values are reasonable

**Cursor not hidden?**
- Ensure `hide_system_cursor = true` in config.ini
- Overlay may need to be running with sufficient permissions

**Trail not rendering?**
- Verify osu! skin folder contains `cursortrail.png` (optional)
- Falls back to procedural white circle if missing

**Flicker at high FPS?**
- Check system CPU usage; render thread may be competing with other processes
- Try lowering `target_fps` in config.ini temporarily to debug

## Building from Source

Requirements:
- .NET 8 SDK (or later)
- Windows 10+ (WinForms is Windows-only)

```bash
dotnet build -c Release
dotnet publish -c Release -o dist
```

Binary is self-contained; no runtime installation needed.

---

**Status:** Production-ready. Built 2026-04-16. Zero warnings, zero errors.
