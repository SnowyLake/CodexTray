using CodexUsage.Core;

namespace CodexUsageTray;

internal sealed class SettingsSavedEventArgs : EventArgs
{
    public int PreviousPort { get; }

    /// <summary>
    /// Creates event data for saved settings.
    /// </summary>
    public SettingsSavedEventArgs(int previousPort)
    {
        PreviousPort = previousPort;
    }
}

internal sealed class SettingsForm : Form
{
    private readonly AppSettings m_Settings;
    private readonly Label m_ServiceStatusLabel = new();
    private readonly Label m_UpdatedAtLabel = new();
    private readonly Label m_SourceFileLabel = new();
    private readonly Label m_DisplayLabel = new();
    private readonly TextBox m_LiteMonitorPathTextBox = new();
    private readonly NumericUpDown m_PortInput = new();
    private readonly CheckBox m_StartWithWindowsCheckBox = new();

    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

    public event EventHandler? InstallPluginRequested;

    /// <summary>
    /// Creates a settings window for the tray application.
    /// </summary>
    public SettingsForm(AppSettings settings)
    {
        m_Settings = settings;
        Text = "CodexUsage Settings";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(620, 430);
        Size = new Size(720, 480);
        BuildLayout();
        LoadSettingsToControls();
    }

    /// <summary>
    /// Updates the visible service status labels.
    /// </summary>
    public void UpdateStatus(bool isRunning, int port, UsageResponse? response, string? error)
    {
        m_ServiceStatusLabel.Text = error == null
            ? $"Service: {(isRunning ? "Running" : "Stopped")} on 127.0.0.1:{port}"
            : $"Service: Error on 127.0.0.1:{port} - {error}";
        m_UpdatedAtLabel.Text = $"Updated: {response?.UpdatedAt ?? "unavailable"}";
        m_SourceFileLabel.Text = $"Source: {TrimMiddle(response?.SourceFile ?? "unavailable", 82)}";
        m_DisplayLabel.Text = response?.Available == true
            ? $"{response.Display.Codex5H}    |    {response.Display.CodexWeekly}"
            : "Codex usage unavailable";
    }

    /// <summary>
    /// Builds the settings window controls.
    /// </summary>
    private void BuildLayout()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        Label titleLabel = new()
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "CodexUsage LiteMonitor Tray",
            Margin = new Padding(0, 0, 0, 12),
        };
        root.Controls.Add(titleLabel, 0, 0);

        root.Controls.Add(BuildStatusGroup(), 0, 1);
        root.Controls.Add(BuildSettingsGroup(), 0, 2);
        root.Controls.Add(BuildButtonRow(), 0, 3);
    }

    /// <summary>
    /// Builds the service status section.
    /// </summary>
    private Control BuildStatusGroup()
    {
        GroupBox group = new()
        {
            Dock = DockStyle.Top,
            Text = "Service",
            Padding = new Padding(12),
            AutoSize = true,
        };
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            AutoSize = true,
        };
        group.Controls.Add(layout);

        foreach (Label label in new[] { m_ServiceStatusLabel, m_DisplayLabel, m_UpdatedAtLabel, m_SourceFileLabel })
        {
            label.AutoSize = true;
            label.Margin = new Padding(0, 2, 0, 2);
            layout.Controls.Add(label);
        }

        return group;
    }

    /// <summary>
    /// Builds the editable settings section.
    /// </summary>
    private Control BuildSettingsGroup()
    {
        GroupBox group = new()
        {
            Dock = DockStyle.Top,
            Text = "Settings",
            Padding = new Padding(12),
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 12),
        };

        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
            AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        group.Controls.Add(layout);

        Label pathLabel = CreateFieldLabel("LiteMonitor folder");
        layout.Controls.Add(pathLabel, 0, 0);
        m_LiteMonitorPathTextBox.Dock = DockStyle.Fill;
        m_LiteMonitorPathTextBox.Margin = new Padding(8, 4, 8, 4);
        layout.Controls.Add(m_LiteMonitorPathTextBox, 1, 0);
        layout.Controls.Add(CreateButton("Browse", BrowseLiteMonitorFolder), 2, 0);
        layout.Controls.Add(CreateButton("Auto Detect", AutoDetectLiteMonitorFolder), 3, 0);

        Label portLabel = CreateFieldLabel("Port");
        layout.Controls.Add(portLabel, 0, 1);
        m_PortInput.Minimum = 1;
        m_PortInput.Maximum = 65535;
        m_PortInput.Width = 120;
        m_PortInput.Margin = new Padding(8, 4, 8, 4);
        layout.Controls.Add(m_PortInput, 1, 1);

        m_StartWithWindowsCheckBox.AutoSize = true;
        m_StartWithWindowsCheckBox.Text = "Start with Windows";
        m_StartWithWindowsCheckBox.Margin = new Padding(8, 8, 8, 4);
        layout.Controls.Add(m_StartWithWindowsCheckBox, 1, 2);

        return group;
    }

    /// <summary>
    /// Builds the bottom button row.
    /// </summary>
    private Control BuildButtonRow()
    {
        FlowLayoutPanel row = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        row.Controls.Add(CreateButton("Save", SaveSettings));
        row.Controls.Add(CreateButton("Install Plugin Config", RequestInstallPlugin));
        row.Controls.Add(CreateButton("Close", (_, _) => Hide()));
        return row;
    }

    /// <summary>
    /// Loads settings values into controls.
    /// </summary>
    private void LoadSettingsToControls()
    {
        m_LiteMonitorPathTextBox.Text = m_Settings.LiteMonitorDir;
        m_PortInput.Value = Math.Max(m_PortInput.Minimum, Math.Min(m_PortInput.Maximum, m_Settings.Port));
        m_StartWithWindowsCheckBox.Checked = m_Settings.StartWithWindows;
    }

    /// <summary>
    /// Opens a folder browser for the LiteMonitor directory.
    /// </summary>
    private void BrowseLiteMonitorFolder(object? sender, EventArgs args)
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Select LiteMonitor folder",
            SelectedPath = Directory.Exists(m_LiteMonitorPathTextBox.Text) ? m_LiteMonitorPathTextBox.Text : string.Empty,
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            m_LiteMonitorPathTextBox.Text = dialog.SelectedPath;
        }
    }

    /// <summary>
    /// Auto detects the LiteMonitor directory.
    /// </summary>
    private void AutoDetectLiteMonitorFolder(object? sender, EventArgs args)
    {
        string detected = LiteMonitorLocator.AutoDetect(m_LiteMonitorPathTextBox.Text);
        if (string.IsNullOrWhiteSpace(detected))
        {
            MessageBox.Show(this, "LiteMonitor was not found.", "CodexUsage", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        m_LiteMonitorPathTextBox.Text = detected;
    }

    /// <summary>
    /// Saves control values back to settings.
    /// </summary>
    private void SaveSettings(object? sender, EventArgs args)
    {
        int previousPort = m_Settings.Port;
        m_Settings.LiteMonitorDir = m_LiteMonitorPathTextBox.Text.Trim();
        m_Settings.Port = (int)m_PortInput.Value;
        m_Settings.StartWithWindows = m_StartWithWindowsCheckBox.Checked;
        SettingsSaved?.Invoke(this, new SettingsSavedEventArgs(previousPort));
        MessageBox.Show(this, "Settings saved.", "CodexUsage", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Raises a request to install the LiteMonitor plugin.
    /// </summary>
    private void RequestInstallPlugin(object? sender, EventArgs args)
    {
        m_Settings.LiteMonitorDir = m_LiteMonitorPathTextBox.Text.Trim();
        InstallPluginRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Creates a field label with shared layout settings.
    /// </summary>
    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            AutoSize = true,
            Text = text,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 0, 4),
        };
    }

    /// <summary>
    /// Creates a button with shared layout settings.
    /// </summary>
    private static Button CreateButton(string text, EventHandler handler)
    {
        Button button = new()
        {
            AutoSize = true,
            Text = text,
            Margin = new Padding(4),
            MinimumSize = new Size(92, 30),
        };
        button.Click += handler;
        return button;
    }

    /// <summary>
    /// Trims a long path in the middle for display.
    /// </summary>
    private static string TrimMiddle(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        int keep = (maxLength - 3) / 2;
        return value[..keep] + "..." + value[^keep..];
    }
}
