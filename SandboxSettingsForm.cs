namespace ZomboidManager;

public class SandboxSettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly TextBox _txtZomboidPath;
    private readonly ListBox _lstFiles;
    private readonly TextBox _txtEditor;
    private readonly Label _lblStatus;
    private string? _currentFilePath;
    private bool _dirty;

    public SandboxSettingsForm(AppConfig config)
    {
        _config = config;

        Text = "Sandbox Settings";
        BackColor = AppTheme.BackgroundDark;
        ForeColor = AppTheme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(980, 680);
        MinimumSize = new Size(800, 560);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
            BackColor = AppTheme.BackgroundDark
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var pathGroup = new GroupBox
        {
            Text = "Zomboid user data folder",
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 10),
            BackColor = AppTheme.BackgroundPanel
        };
        AppTheme.StyleGroupBox(pathGroup);

        var pathLayout = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 2,
            AutoSize = true,
            Dock = DockStyle.Fill,
            BackColor = AppTheme.BackgroundPanel
        };
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var lblHint = new Label
        {
            Text = "Select the Zomboid folder under your Windows user profile (e.g. C:\\Users\\Administrator\\Zomboid). Server INI and sandbox configs are usually in the Server subfolder.",
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            Margin = new Padding(0, 0, 0, 8)
        };
        AppTheme.StyleLabel(lblHint, secondary: true);
        pathLayout.SetColumnSpan(lblHint, 3);
        pathLayout.Controls.Add(lblHint, 0, 0);

        _txtZomboidPath = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = _config.ZomboidUserPath
        };
        AppTheme.StyleTextBox(_txtZomboidPath);
        pathLayout.Controls.Add(_txtZomboidPath, 0, 1);

        var btnBrowse = new Button
        {
            Text = "Browse...",
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0)
        };
        AppTheme.StyleButton(btnBrowse);
        btnBrowse.Click += (_, _) => BrowseZomboidFolder();
        pathLayout.Controls.Add(btnBrowse, 1, 1);

        var btnRefresh = new Button
        {
            Text = "Refresh files",
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0)
        };
        AppTheme.StyleButton(btnRefresh);
        btnRefresh.Click += (_, _) =>
        {
            SavePathToConfig();
            RefreshFileList();
        };
        pathLayout.Controls.Add(btnRefresh, 2, 1);

        pathGroup.Controls.Add(pathLayout);
        root.Controls.Add(pathGroup, 0, 0);

        var filesLabel = new Label
        {
            Text = "Configuration files",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        AppTheme.StyleLabel(filesLabel);
        root.Controls.Add(filesLabel, 0, 1);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = AppTheme.BackgroundDark
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _lstFiles = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.BackgroundInput,
            ForeColor = AppTheme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
            DisplayMember = nameof(ConfigFileEntry.DisplayName)
        };
        _lstFiles.SelectedIndexChanged += (_, _) => LoadSelectedFile();
        content.Controls.Add(_lstFiles, 0, 0);

        _txtEditor = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            AcceptsTab = true,
            Font = new Font("Consolas", 10f),
            BackColor = AppTheme.BackgroundInput,
            ForeColor = AppTheme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle
        };
        AppTheme.StyleTextBox(_txtEditor);
        _txtEditor.Font = new Font("Consolas", 10f);
        _txtEditor.TextChanged += (_, _) => _dirty = true;
        content.Controls.Add(_txtEditor, 1, 0);

        root.Controls.Add(content, 0, 2);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0),
            BackColor = AppTheme.BackgroundDark
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _lblStatus = new Label
        {
            Text = "No file loaded.",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        AppTheme.StyleLabel(_lblStatus, secondary: true);
        bottom.Controls.Add(_lblStatus, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = AppTheme.BackgroundDark
        };

        var btnClose = new Button { Text = "Close", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        AppTheme.StyleButton(btnClose);
        btnClose.Click += (_, _) => Close();

        var btnReload = new Button { Text = "Reload", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        AppTheme.StyleButton(btnReload);
        btnReload.Click += (_, _) => LoadSelectedFile(force: true);

        var btnSave = new Button { Text = "Save file", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        AppTheme.StyleButton(btnSave);
        btnSave.Click += (_, _) => SaveCurrentFile();

        buttons.Controls.Add(btnClose);
        buttons.Controls.Add(btnReload);
        buttons.Controls.Add(btnSave);
        bottom.Controls.Add(buttons, 1, 0);

        root.Controls.Add(bottom, 0, 3);
        Controls.Add(root);

        FormClosing += OnFormClosing;

        if (!string.IsNullOrWhiteSpace(_config.ZomboidUserPath))
            RefreshFileList();
    }

    private void BrowseZomboidFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the Zomboid folder under your user profile",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        string current = _txtZomboidPath.Text.Trim();
        if (Directory.Exists(current))
            dialog.SelectedPath = current;
        else
        {
            string defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Zomboid");
            if (Directory.Exists(defaultPath))
                dialog.SelectedPath = defaultPath;
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _txtZomboidPath.Text = dialog.SelectedPath;
        SavePathToConfig();
        RefreshFileList();
    }

    private void SavePathToConfig()
    {
        _config.ZomboidUserPath = _txtZomboidPath.Text.Trim().Trim('"');
        ConfigManager.Save(_config);
    }

    private void RefreshFileList()
    {
        if (_dirty && !ConfirmDiscardChanges())
            return;

        _lstFiles.Items.Clear();
        _txtEditor.Clear();
        _currentFilePath = null;
        _dirty = false;

        string rootPath = _txtZomboidPath.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            SetStatus("Zomboid folder not found. Please browse to C:\\Users\\<user>\\Zomboid.", AppTheme.WarningYellow);
            return;
        }

        var entries = new List<ConfigFileEntry>();
        string serverDir = Path.Combine(rootPath, "Server");
        if (Directory.Exists(serverDir))
        {
            foreach (string pattern in new[] { "*.ini", "*.lua", "*.cfg", "*.txt" })
            {
                foreach (string file in Directory.GetFiles(serverDir, pattern, SearchOption.TopDirectoryOnly))
                    entries.Add(CreateEntry(rootPath, file));
            }
        }

        foreach (string pattern in new[] { "options.ini", "latestSave.ini" })
        {
            string file = Path.Combine(rootPath, pattern);
            if (File.Exists(file))
                entries.Add(CreateEntry(rootPath, file));
        }

        entries = entries
            .GroupBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (ConfigFileEntry entry in entries)
            _lstFiles.Items.Add(entry);

        SetStatus(entries.Count == 0
            ? "No config files found. Check that the Server subfolder exists."
            : $"{entries.Count} file(s) found.", AppTheme.TextSecondary);

        if (_lstFiles.Items.Count > 0)
            _lstFiles.SelectedIndex = 0;
    }

    private static ConfigFileEntry CreateEntry(string rootPath, string fullPath)
    {
        string relative = Path.GetRelativePath(rootPath, fullPath);
        return new ConfigFileEntry(fullPath, relative);
    }

    private void LoadSelectedFile(bool force = false)
    {
        if (_lstFiles.SelectedItem is not ConfigFileEntry entry)
            return;

        if (!force && _dirty && !string.Equals(_currentFilePath, entry.FullPath, StringComparison.OrdinalIgnoreCase)
            && !ConfirmDiscardChanges())
        {
            SelectFileInList(_currentFilePath);
            return;
        }

        try
        {
            string content = File.ReadAllText(entry.FullPath);
            _currentFilePath = entry.FullPath;
            _txtEditor.Text = content;
            _dirty = false;
            SetStatus($"Loaded: {entry.DisplayName}", AppTheme.SuccessGreen);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load file: {ex.Message}", AppTheme.ErrorRed);
        }
    }

    private void SaveCurrentFile()
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath))
        {
            MessageBox.Show(this, "No file selected.", "Sandbox Settings",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            File.WriteAllText(_currentFilePath, _txtEditor.Text);
            _dirty = false;
            SetStatus($"Saved: {Path.GetFileName(_currentFilePath)}", AppTheme.SuccessGreen);
            MessageBox.Show(this, "File saved successfully.", "Sandbox Settings",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to save file:\n{ex.Message}", "Sandbox Settings",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SelectFileInList(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return;

        for (int i = 0; i < _lstFiles.Items.Count; i++)
        {
            if (_lstFiles.Items[i] is ConfigFileEntry entry
                && string.Equals(entry.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                _lstFiles.SelectedIndex = i;
                return;
            }
        }
    }

    private bool ConfirmDiscardChanges()
    {
        DialogResult result = MessageBox.Show(
            this,
            "You have unsaved changes. Discard them?",
            "Sandbox Settings",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        return result == DialogResult.Yes;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_dirty)
            return;

        DialogResult result = MessageBox.Show(
            this,
            "You have unsaved changes. Close anyway?",
            "Sandbox Settings",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
            e.Cancel = true;
    }

    private void SetStatus(string text, Color color)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = color;
    }

    private sealed class ConfigFileEntry(string fullPath, string displayName)
    {
        public string FullPath { get; } = fullPath;
        public string DisplayName { get; } = displayName;

        public override string ToString() => DisplayName;
    }
}
