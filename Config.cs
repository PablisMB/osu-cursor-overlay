using System.Globalization;

namespace OsuCursorOverlay;

public sealed class AppSettings
{
    // Cursor section
    public float Scale { get; set; } = 1.0f;
    public int TrailLength { get; set; } = 15;
    public int MaxTrailAlpha { get; set; } = 150;
    public float MinTrailScale { get; set; } = 0.3f;
    public float TrailSpacing { get; set; } = 3.0f;

    // System section
    public int TargetFps { get; set; } = 144;
    public bool HideSystemCursor { get; set; } = true;
    public string ExitHotkey { get; set; } = "ctrl+shift+q";
    public bool AutoStartWithWindows { get; set; } = false;
    public string LastSkinName { get; set; } = "";

    // Parsed from ExitHotkey
    public Keys HotkeyVKey { get; set; }
    public uint HotkeyModifiers { get; set; }
}

public static class ConfigManager
{
    private const string DefaultIniContent = """
        [cursor]
        scale = 1.0
        trail_length = 15
        max_trail_alpha = 150
        min_trail_scale = 0.3
        trail_spacing = 1.0

        [system]
        target_fps = 144
        hide_system_cursor = true
        exit_hotkey = ctrl+shift+q
        auto_start_with_windows = false
        last_skin_name =
        """;

    public static AppSettings Load(string path)
    {
        string content;

        if (!File.Exists(path))
        {
            File.WriteAllText(path, DefaultIniContent);
            content = DefaultIniContent;
        }
        else
        {
            content = File.ReadAllText(path);
        }

        var settings = new AppSettings
        {
            Scale = float.Parse(ReadValue(content, "cursor", "scale") ?? "1.0", CultureInfo.InvariantCulture),
            TrailLength = int.Parse(ReadValue(content, "cursor", "trail_length") ?? "15"),
            MaxTrailAlpha = int.Parse(ReadValue(content, "cursor", "max_trail_alpha") ?? "150"),
            MinTrailScale = float.Parse(ReadValue(content, "cursor", "min_trail_scale") ?? "0.3", CultureInfo.InvariantCulture),
            TrailSpacing = float.Parse(ReadValue(content, "cursor", "trail_spacing") ?? "1.0", CultureInfo.InvariantCulture),
            TargetFps = int.Parse(ReadValue(content, "system", "target_fps") ?? "144"),
            HideSystemCursor = bool.Parse(ReadValue(content, "system", "hide_system_cursor") ?? "true"),
            ExitHotkey = ReadValue(content, "system", "exit_hotkey") ?? "ctrl+shift+q",
            AutoStartWithWindows = bool.Parse(ReadValue(content, "system", "auto_start_with_windows") ?? "false"),
            LastSkinName = ReadValue(content, "system", "last_skin_name") ?? ""
        };

        ParseHotkey(settings);
        return settings;
    }

    private static string? ReadValue(string iniText, string section, string key)
    {
        var lines = iniText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        bool inSection = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed == $"[{section}]")
            {
                inSection = true;
                continue;
            }

            if (inSection && trimmed.StartsWith('['))
            {
                break;
            }

            if (inSection && trimmed.Contains('='))
            {
                var parts = trimmed.Split('=', 2);
                if (parts[0].Trim() == key)
                {
                    return parts[1].Trim();
                }
            }
        }

        return null;
    }

    private static void ParseHotkey(AppSettings settings)
    {
        var tokens = settings.ExitHotkey.Split('+');
        uint modifiers = 0;
        Keys? vkey = null;

        foreach (var token in tokens)
        {
            var lower = token.Trim().ToLowerInvariant();

            switch (lower)
            {
                case "ctrl":
                    modifiers |= NativeMethods.MOD_CTRL;
                    break;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;
                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;
                case "win":
                    modifiers |= NativeMethods.MOD_WIN;
                    break;
                default:
                    if (lower.Length == 1 && Enum.TryParse<Keys>(lower.ToUpperInvariant(), out var k))
                    {
                        vkey = k;
                    }
                    break;
            }
        }

        settings.HotkeyModifiers = modifiers;
        settings.HotkeyVKey = vkey ?? Keys.Q;
    }

    public static void Save(string path, AppSettings s)
    {
        var content = string.Join("\n", new[]
        {
            "[cursor]",
            $"scale = {s.Scale.ToString("F2", CultureInfo.InvariantCulture)}",
            $"trail_length = {s.TrailLength}",
            $"max_trail_alpha = {s.MaxTrailAlpha}",
            $"min_trail_scale = {s.MinTrailScale.ToString("F2", CultureInfo.InvariantCulture)}",
            $"trail_spacing = {s.TrailSpacing.ToString("F2", CultureInfo.InvariantCulture)}",
            "",
            "[system]",
            $"target_fps = {s.TargetFps}",
            $"hide_system_cursor = {s.HideSystemCursor.ToString().ToLower()}",
            $"exit_hotkey = {s.ExitHotkey}",
            $"auto_start_with_windows = {s.AutoStartWithWindows.ToString().ToLower()}",
            $"last_skin_name = {s.LastSkinName}"
        });
        File.WriteAllText(path, content);
    }
}
