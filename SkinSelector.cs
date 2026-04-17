using System.Diagnostics;
using System.Drawing.Imaging;
using Microsoft.Win32;

namespace OsuCursorOverlay;

public sealed record SkinFiles(
    string? CursorPath,
    string? TrailPath,
    string? MiddlePath
);

public static class SkinDiscovery
{
    private static readonly string[] CandidatePaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!", "Skins"),
        @"C:\Program Files\osu!\Skins",
        @"C:\Program Files (x86)\osu!\Skins",
    };

    public static string? FindSkinsDirectory()
    {
        foreach (var path in CandidatePaths)
            if (Directory.Exists(path)) return path;
        return null;
    }

    public static IReadOnlyList<string> ListSkins(string skinsDir)
    {
        var skins = Directory.GetDirectories(skinsDir)
            .Where(d => File.Exists(Path.Combine(d, "cursor.png")))
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n)
            .ToList();
        return skins;
    }

    public static SkinFiles GetSkinFiles(string skinPath)
    {
        string cursor = Path.Combine(skinPath, "cursor.png");
        string trail  = Path.Combine(skinPath, "cursortrail.png");
        string middle = Path.Combine(skinPath, "cursormiddle.png");
        return new SkinFiles(
            File.Exists(cursor) ? cursor : null,
            File.Exists(trail)  ? trail  : null,
            File.Exists(middle) ? middle : null);
    }
}

public sealed class SkinSelectorForm : Form
{
    private readonly string _skinsDir;
    private readonly IReadOnlyList<string> _skins;
    private AppSettings _settings;
    private readonly string _configPath;

    private FlowLayoutPanel _skinGrid = null!;
    private PictureBox _previewBox = null!;
    private Label _skinNameLabel = null!;
    private TextBox _searchBox = null!;
    private Panel _configPanel = null!;
    private Button _btnTheme = null!;
    private bool _darkMode;

    private readonly List<(Panel card, string name)> _skinCards = new();
    private string _selectedSkin = "";

    private Thread? _previewThread;
    private volatile bool _stopPreview = false;
    private SkinAssets? _previewAssets;

    public string? SelectedSkinName { get; private set; }
    public string? SelectedSkinPath { get; private set; }
    public AppSettings FinalSettings => _settings;

    private static readonly Color DarkBg      = Color.FromArgb(22, 22, 40);
    private static readonly Color DarkSurface = Color.FromArgb(38, 38, 62);
    private static readonly Color DarkAccent  = Color.FromArgb(255, 100, 160);
    private static readonly Color DarkText    = Color.FromArgb(235, 235, 235);

    public SkinSelectorForm(string skinsDir, IReadOnlyList<string> skins, AppSettings settings, string configPath)
    {
        _skinsDir   = skinsDir;
        _skins      = skins;
        _settings   = settings;
        _configPath = configPath;
        _darkMode   = DetectDarkMode();

        Text            = "osu! Cursor Overlay";
        Width           = 800;
        Height          = 520;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterScreen;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = true;
        Font            = new Font("Segoe UI", 9.5f);

        BuildUI();
        ApplyTheme();
        LoadSkinCards();
    }

    // ── Theme ────────────────────────────────────────────────────────────────

    private static bool DetectDarkMode()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return v is int i && i == 0;
        }
        catch { return false; }
    }

    private void ToggleDarkMode()
    {
        _darkMode = !_darkMode;
        _btnTheme.Text = _darkMode ? "\u2600" : "\u263D";
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        var bg      = _darkMode ? DarkBg      : SystemColors.Control;
        var surface = _darkMode ? DarkSurface : SystemColors.ControlLightLight;
        var fg      = _darkMode ? DarkText    : SystemColors.ControlText;

        SetThemeRecursive(this, bg, fg);

        _skinGrid.BackColor = bg;
        _configPanel.BackColor = surface;
        SetThemeRecursive(_configPanel, surface, fg);

        RefreshCardSelection();
    }

    private static void SetThemeRecursive(Control ctrl, Color bg, Color fg)
    {
        if (ctrl is TrackBar || ctrl is ComboBox) return;
        ctrl.BackColor = bg;
        ctrl.ForeColor = fg;
        foreach (Control c in ctrl.Controls)
            SetThemeRecursive(c, bg, fg);
    }

    // ── Build UI ─────────────────────────────────────────────────────────────

    private void BuildUI()
    {
        var root = new TableLayoutPanel
        {
            Dock      = DockStyle.Fill,
            RowCount  = 3,
            ColumnCount = 1,
            Padding   = Padding.Empty,
            Margin    = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(),  0, 0);
        root.Controls.Add(BuildContent(), 0, 1);
        root.Controls.Add(BuildFooter(),  0, 2);

        BuildConfigPanel();
    }

    private Panel BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 4) };

        _searchBox = new TextBox
        {
            PlaceholderText = "Buscar skins...",
            Location        = new Point(10, 8),
            Width           = 690,
            Height          = 26,
            Font            = new Font("Segoe UI", 10f),
            BorderStyle     = BorderStyle.FixedSingle
        };
        _searchBox.TextChanged += (_, _) => FilterCards();

        var btnCfg = MakeButton("\u2699", 28, 28, new Font("Segoe UI", 13f));
        btnCfg.Location = new Point(710, 6);
        btnCfg.Click   += ToggleConfigPanel;

        panel.Controls.AddRange(new Control[] { _searchBox, btnCfg });
        return panel;
    }

    private Control BuildContent()
    {
        var split = new SplitContainer
        {
            Dock             = DockStyle.Fill,
            SplitterDistance = 440,
            IsSplitterFixed  = true,
            Panel1MinSize    = 300,
            Panel2MinSize    = 200,
            SplitterWidth    = 4
        };

        // Left: skin grid
        _skinGrid = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            AutoScroll    = true,
            Padding       = new Padding(6),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = true
        };
        split.Panel1.Controls.Add(_skinGrid);

        // Right: preview
        var right = new Panel { Dock = DockStyle.Fill };

        _previewBox = new PictureBox
        {
            SizeMode  = PictureBoxSizeMode.Zoom,
            Width     = 260,
            Height    = 260,
            BackColor = Color.Transparent
        };

        _skinNameLabel = new Label
        {
            Text      = "",
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock      = DockStyle.Bottom,
            Height    = 28
        };

        right.Controls.Add(_previewBox);
        right.Controls.Add(_skinNameLabel);
        right.Resize += (_, _) => CenterPreviewBox();
        split.Panel2.Controls.Add(right);

        return split;
    }

    private Panel BuildFooter()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 8) };

        _btnTheme = MakeButton(_darkMode ? "\u2600" : "\u263D", 36, 34, new Font("Segoe UI", 13f));
        _btnTheme.Location = new Point(10, 8);
        _btnTheme.Click   += (_, _) => ToggleDarkMode();

        var btnLaunch = MakeButton("Lanzar", 120, 34, new Font("Segoe UI", 11f, FontStyle.Bold));
        btnLaunch.Location = new Point(646, 8);
        btnLaunch.Click   += BtnLaunch_Click;

        panel.Controls.AddRange(new Control[] { _btnTheme, btnLaunch });
        return panel;
    }

    private void CenterPreviewBox()
    {
        if (_previewBox.Parent == null) return;
        int cx = (_previewBox.Parent.ClientSize.Width - _previewBox.Width) / 2;
        int cy = (_previewBox.Parent.ClientSize.Height - _previewBox.Height - _skinNameLabel.Height) / 2;
        _previewBox.Location = new Point(Math.Max(0, cx), Math.Max(0, cy));
    }

    // ── Config panel ─────────────────────────────────────────────────────────

    private void BuildConfigPanel()
    {
        _configPanel = new Panel
        {
            Width       = 305,
            Height      = 315,
            Visible     = false,
            BorderStyle = BorderStyle.FixedSingle
        };

        // Title bar
        var titleBar = new Panel { Dock = DockStyle.Top, Height = 34 };
        var title    = new Label { Text = "Configuracion", Font = new Font("Segoe UI", 10f, FontStyle.Bold), Location = new Point(10, 8), AutoSize = true };
        var btnX     = MakeButton("x", 24, 22, new Font("Segoe UI", 9f));
        btnX.Location = new Point(273, 6);
        btnX.Click   += (_, _) => _configPanel.Visible = false;
        titleBar.Controls.AddRange(new Control[] { title, btnX });

        // Content
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 2, 10, 6) };

        int y = 4;
        y = AddSlider(content, "Escala",    y, 1, 40,  (int)Math.Round(_settings.Scale * 10),   v => { _settings.Scale = v / 10f; RestartPreview(); });
        y = AddSlider(content, "Trail",     y, 1, 50,  _settings.TrailLength,                   v => { _settings.TrailLength = v; RestartPreview(); });
        y = AddSlider(content, "Alpha %",   y, 0, 100, _settings.MaxTrailAlpha * 100 / 255,     v => { _settings.MaxTrailAlpha = v * 255 / 100; RestartPreview(); });
        y = AddSlider(content, "Espaciado", y, 1, 50,  (int)Math.Round(_settings.TrailSpacing * 10), v => { _settings.TrailSpacing = v / 10f; RestartPreview(); });

        // FPS
        var fpsLabel = new Label { Text = "FPS", Location = new Point(0, y + 3), AutoSize = true };
        var fpsBox   = new ComboBox
        {
            Location      = new Point(100, y),
            Width         = 150,
            DropDownStyle = ComboBoxStyle.DropDown
        };
        fpsBox.Items.AddRange(new object[] { "60", "120", "144", "240" });
        fpsBox.Text        = _settings.TargetFps.ToString();
        fpsBox.TextChanged += (_, _) => { if (int.TryParse(fpsBox.Text, out int fps) && fps >= 30 && fps <= 300) _settings.TargetFps = fps; };
        y += 30;

        // Checkboxes
        var chkHide = new CheckBox { Text = "Ocultar cursor del sistema", Location = new Point(0, y), AutoSize = true, Checked = _settings.HideSystemCursor };
        chkHide.CheckedChanged += (_, _) => _settings.HideSystemCursor = chkHide.Checked;
        y += 26;

        var chkStart = new CheckBox { Text = "Iniciar con Windows", Location = new Point(0, y), AutoSize = true, Checked = _settings.AutoStartWithWindows };
        chkStart.CheckedChanged += (_, _) => _settings.AutoStartWithWindows = chkStart.Checked;
        y += 30;

        // Save button
        var btnSave = MakeButton("Guardar", 110, 30, new Font("Segoe UI", 10f, FontStyle.Bold));
        btnSave.Location = new Point(90, y);
        btnSave.Click   += (_, _) => { ConfigManager.Save(_configPath, _settings); _configPanel.Visible = false; };

        content.Controls.AddRange(new Control[] { fpsLabel, fpsBox, chkHide, chkStart, btnSave });

        _configPanel.Controls.Add(content);
        _configPanel.Controls.Add(titleBar);
        Controls.Add(_configPanel);
        _configPanel.BringToFront();
    }

    private int AddSlider(Panel parent, string label, int y, int min, int max, int value, Action<int> onChange)
    {
        value = Math.Clamp(value, min, max);

        var lbl = new Label { Text = label, Location = new Point(0, y + 4), AutoSize = true };
        var track = new TrackBar
        {
            Location       = new Point(80, y - 3),
            Width          = 158,
            Minimum        = min,
            Maximum        = max,
            Value          = value,
            TickFrequency  = Math.Max(1, (max - min) / 5),
            SmallChange    = 1,
            TickStyle      = TickStyle.None,
            Height         = 28
        };
        var txt = new TextBox
        {
            Location    = new Point(242, y + 1),
            Width       = 44,
            Text        = value.ToString(),
            BorderStyle = BorderStyle.FixedSingle
        };

        bool syncing = false;
        track.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            txt.Text = track.Value.ToString();
            onChange(track.Value);
            syncing = false;
        };
        txt.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (int.TryParse(txt.Text, out int v) && v >= min && v <= max)
            {
                syncing = true;
                track.Value = v;
                onChange(v);
                syncing = false;
            }
        };

        parent.Controls.AddRange(new Control[] { lbl, track, txt });
        return y + 30;
    }

    private void ToggleConfigPanel(object? sender, EventArgs e)
    {
        _configPanel.Visible = !_configPanel.Visible;
        if (!_configPanel.Visible) return;
        _configPanel.Location = new Point(ClientSize.Width - _configPanel.Width - 10, 44);
        _configPanel.BringToFront();
    }

    // ── Skin cards ───────────────────────────────────────────────────────────

    private void LoadSkinCards()
    {
        _skinGrid.SuspendLayout();
        foreach (var skinName in _skins)
        {
            var card = CreateCard(skinName);
            _skinCards.Add((card, skinName));
            _skinGrid.Controls.Add(card);
        }
        _skinGrid.ResumeLayout();

        if (_skinCards.Count > 0)
            SelectSkin(_skinCards[0].name);
    }

    private Panel CreateCard(string skinName)
    {
        var card = new Panel
        {
            Width  = 104,
            Height = 114,
            Margin = new Padding(4),
            Cursor = Cursors.Hand,
            Tag    = skinName
        };

        Bitmap? thumb = null;
        try
        {
            var path = Path.Combine(_skinsDir, skinName, "cursor.png");
            if (File.Exists(path))
            {
                using var raw = new Bitmap(path);
                thumb = new Bitmap(68, 68, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(thumb);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);
                float scale = Math.Min(68f / raw.Width, 68f / raw.Height);
                int dw = (int)(raw.Width * scale), dh = (int)(raw.Height * scale);
                g.DrawImage(raw, new Rectangle((68 - dw) / 2, (68 - dh) / 2, dw, dh), 0, 0, raw.Width, raw.Height, GraphicsUnit.Pixel);
            }
        }
        catch { }

        var pic = new PictureBox
        {
            Image     = thumb,
            SizeMode  = PictureBoxSizeMode.CenterImage,
            Width     = 96,
            Height    = 74,
            Location  = new Point(4, 4),
            BackColor = Color.Transparent
        };

        var lbl = new Label
        {
            Text      = skinName,
            Width     = 96,
            Height    = 32,
            Location  = new Point(4, 78),
            TextAlign = ContentAlignment.TopCenter,
            Font      = new Font("Segoe UI", 7.5f),
            AutoEllipsis = true,
            BackColor = Color.Transparent
        };

        card.Controls.AddRange(new Control[] { pic, lbl });

        EventHandler onClick = (_, _) => SelectSkin(skinName);
        card.Click += onClick;
        pic.Click  += onClick;
        lbl.Click  += onClick;

        return card;
    }

    private void SelectSkin(string skinName)
    {
        _selectedSkin         = skinName;
        _skinNameLabel.Text   = skinName;
        RefreshCardSelection();
        RestartPreview();
    }

    private void RefreshCardSelection()
    {
        var sel   = _darkMode ? Color.FromArgb(58, 58, 92) : Color.FromArgb(198, 218, 255);
        var unsel = Color.Transparent;

        foreach (var (card, name) in _skinCards)
        {
            card.BackColor = name == _selectedSkin ? sel : unsel;
            foreach (Control c in card.Controls)
                c.BackColor = Color.Transparent;
        }
    }

    private void FilterCards()
    {
        var filter = _searchBox.Text.Trim().ToLowerInvariant();
        _skinGrid.SuspendLayout();
        foreach (var (card, name) in _skinCards)
            card.Visible = string.IsNullOrEmpty(filter) || name.ToLowerInvariant().Contains(filter);
        _skinGrid.ResumeLayout();
    }

    // ── Animated preview ─────────────────────────────────────────────────────

    private void RestartPreview()
    {
        _stopPreview = true;
        _previewThread?.Join(300);
        _previewAssets?.Dispose();
        _previewAssets = null;

        if (string.IsNullOrEmpty(_selectedSkin)) return;

        var skinFiles = SkinDiscovery.GetSkinFiles(Path.Combine(_skinsDir, _selectedSkin));
        try { _previewAssets = SkinAssets.Load(skinFiles, 1.0f); }
        catch { return; }

        var assets   = _previewAssets;
        var settings = _settings;

        _stopPreview   = false;
        _previewThread = new Thread(() => RunPreview(assets, settings)) { IsBackground = true };
        _previewThread.Start();
    }

    private void RunPreview(SkinAssets assets, AppSettings settings)
    {
        const int W = 260, H = 260, FPS = 60;
        long frameTicks = Stopwatch.Frequency / FPS;

        using var renderer = new TrailRenderer(assets, settings.TrailLength, settings.TrailSpacing, settings.MaxTrailAlpha, settings.MinTrailScale, 1.0f);
        var sw     = Stopwatch.StartNew();
        double angle = 0;
        int cx = W / 2, cy = H / 2, r = 80;

        while (!_stopPreview)
        {
            long t0 = sw.ElapsedTicks;

            angle += 0.045;
            var pos = new Point(cx + (int)(r * Math.Cos(angle)), cy + (int)(r * Math.Sin(angle)));

            var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode    = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                renderer.UpdateTrail(pos);
                renderer.DrawFrame(g, pos, W, H);
            }

            try
            {
                _previewBox.BeginInvoke(() =>
                {
                    var old = _previewBox.Image;
                    _previewBox.Image = bmp;
                    old?.Dispose();
                });
            }
            catch { bmp.Dispose(); break; }

            long remaining = frameTicks - (sw.ElapsedTicks - t0);
            if (remaining > 0)
                Thread.Sleep((int)(remaining * 1000L / Stopwatch.Frequency));
        }
    }

    // ── Launch ───────────────────────────────────────────────────────────────

    private void BtnLaunch_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedSkin)) return;
        SelectedSkinName = _selectedSkin;
        SelectedSkinPath = Path.Combine(_skinsDir, _selectedSkin);
        DialogResult     = DialogResult.OK;
        Close();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Button MakeButton(string text, int w, int h, Font font)
    {
        var btn = new Button
        {
            Text      = text,
            Width     = w,
            Height    = h,
            Font      = font,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _stopPreview = true;
        _previewThread?.Join(300);
        _previewAssets?.Dispose();
        base.OnFormClosed(e);
    }
}
