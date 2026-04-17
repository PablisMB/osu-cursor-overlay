namespace OsuCursorOverlay;

public sealed record SkinFiles(
    string? CursorPath,
    string? TrailPath,
    string? MiddlePath
);

public static class SkinDiscovery
{
    private static readonly string[] CandidatePaths = new[]
    {
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "osu!", "Skins"),
        @"C:\Program Files\osu!\Skins",
        @"C:\Program Files (x86)\osu!\Skins",
    };

    public static string? FindSkinsDirectory()
    {
        foreach (var path in CandidatePaths)
        {
            if (Directory.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    public static IReadOnlyList<string> ListSkins(string skinsDir)
    {
        var dirs = Directory.GetDirectories(skinsDir);
        var skins = new List<string>();

        foreach (var dir in dirs)
        {
            var cursorPath = Path.Combine(dir, "cursor.png");
            if (File.Exists(cursorPath))
            {
                skins.Add(Path.GetFileName(dir));
            }
        }

        skins.Sort();
        return skins;
    }

    public static SkinFiles GetSkinFiles(string skinPath)
    {
        var cursorPath = Path.Combine(skinPath, "cursor.png");
        var trailPath = Path.Combine(skinPath, "cursortrail.png");
        var middlePath = Path.Combine(skinPath, "cursormiddle.png");

        return new SkinFiles(
            File.Exists(cursorPath) ? cursorPath : null,
            File.Exists(trailPath) ? trailPath : null,
            File.Exists(middlePath) ? middlePath : null
        );
    }
}

public sealed class SkinSelectorForm : Form
{
    private ListBox? _listBox;
    private Button? _btnOk;
    private Button? _btnCancel;
    private Label? _lblInstructions;

    public string? SelectedSkinName { get; private set; }
    public string? SelectedSkinPath { get; private set; }

    public SkinSelectorForm(string skinsDir, IReadOnlyList<string> skins)
    {
        Text = "Select osu! Skin";
        Width = 480;
        Height = 400;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;

        // Instructions
        _lblInstructions = new Label
        {
            Text = "Choose a skin with a cursor.png file:",
            Dock = DockStyle.Top,
            Padding = new Padding(10, 10, 10, 5),
            AutoSize = false,
            Height = 40
        };
        Controls.Add(_lblInstructions);

        // ListBox
        _listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            SelectionMode = SelectionMode.One
        };

        foreach (var skin in skins)
        {
            _listBox.Items.Add(skin);
        }

        if (_listBox.Items.Count > 0)
        {
            _listBox.SelectedIndex = 0;
        }

        _listBox.DoubleClick += ListBox_DoubleClick;
        Controls.Add(_listBox);

        // Buttons panel
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            Padding = new Padding(10),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };

        _btnCancel = new Button
        {
            Text = "Cancel",
            Width = 100,
            Height = 30,
            DialogResult = DialogResult.Cancel
        };
        _btnCancel.Click += BtnCancel_Click;

        _btnOk = new Button
        {
            Text = "OK",
            Width = 100,
            Height = 30,
            DialogResult = DialogResult.OK
        };
        _btnOk.Click += BtnOk_Click;

        buttonPanel.Controls.Add(_btnCancel);
        buttonPanel.Controls.Add(_btnOk);

        Controls.Add(buttonPanel);

        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        // Store skin directory for path resolution
        this.Tag = (skinsDir, skins);
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        if (_listBox?.SelectedItem is string selected)
        {
            SelectedSkinName = selected;
            var (skinsDir, _) = ((string, IReadOnlyList<string>))this.Tag!;
            SelectedSkinPath = Path.Combine(skinsDir, selected);
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private void ListBox_DoubleClick(object? sender, EventArgs e)
    {
        BtnOk_Click(null, EventArgs.Empty);
    }

    private void BtnCancel_Click(object? sender, EventArgs e)
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }
}
