using CodexMonitor.Core;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace CodexMonitor.App;

/// <summary>
/// Save state of the settings page.
/// </summary>
internal enum SettingsStatus
{
    /// <summary>
    /// No message shown.
    /// </summary>
    Clean,

    /// <summary>
    /// Settings match the last saved snapshot after an explicit save.
    /// </summary>
    Saved,

    /// <summary>
    /// Settings differ from the last saved snapshot.
    /// </summary>
    Unsaved,
}

internal sealed class TrayPopupViewModel : INotifyPropertyChanged
{
    private const string k_HomePageName = "Home";
    private const string k_SettingsPageName = "Settings";

    private static readonly Media.Brush s_GreenBrush = new Media.SolidColorBrush(Media.Color.FromRgb(26, 188, 137));
    private static readonly Media.Brush s_YellowBrush = new Media.SolidColorBrush(Media.Color.FromRgb(226, 176, 54));
    private static readonly Media.Brush s_RedBrush = new Media.SolidColorBrush(Media.Color.FromRgb(224, 91, 77));
    private static readonly Media.Brush s_PlanBadgeActiveBrush = new Media.SolidColorBrush(Media.Color.FromRgb(26, 188, 137));
    private static readonly Media.Brush s_PlanBadgeInactiveBrush = new Media.SolidColorBrush(Media.Color.FromRgb(107, 122, 117));

    private readonly AppSettings m_Settings;
    private string m_CurrentPage = k_HomePageName;
    private string m_PlanDisplay = "None";
    private Media.Brush m_PlanBadgeBrush = s_PlanBadgeInactiveBrush;
    private string m_UpdatedAtDisplay = "Waiting for first refresh";
    private string m_ServiceStatus = "Service: starting";
    private string m_SourceDisplay = "Source: unavailable";
    private string m_LiteMonitorDir = string.Empty;
    private string m_TrafficMonitorDir = string.Empty;
    private string m_PortText = CodexMonitorDefaults.Port.ToString(CultureInfo.InvariantCulture);
    private string m_RefreshIntervalText = CodexMonitorDefaults.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture);
    private string m_ThemeMode = AppSettings.ThemeModeSystem;
    private bool m_StartWithWindows;
    private bool m_AcrylicEnabled = CodexMonitorDefaults.AcrylicEnabled;
    private int m_AcrylicOpacityPercent = CodexMonitorDefaults.AcrylicOpacityPercent;
    private bool m_ShowResetTimeInPlugins = CodexMonitorDefaults.ShowResetTimeInPlugins;
    private bool m_IsRefreshing;
    private bool m_IsModalOpen;
    private bool m_IsDetectingLiteMonitor;
    private bool m_IsDetectingTrafficMonitor;

    private SettingsStatus m_SettingsStatus = SettingsStatus.Clean;
    private SettingsStatus m_SettingsBaseline = SettingsStatus.Clean;
    private bool m_SuppressDirtyTracking;
    private string m_SnapshotLiteMonitorDir = string.Empty;
    private string m_SnapshotTrafficMonitorDir = string.Empty;
    private string m_SnapshotPortText = string.Empty;
    private string m_SnapshotRefreshIntervalText = string.Empty;
    private string m_SnapshotThemeMode = AppSettings.ThemeModeSystem;
    private bool m_SnapshotStartWithWindows;
    private bool m_SnapshotAcrylicEnabled = CodexMonitorDefaults.AcrylicEnabled;
    private int m_SnapshotAcrylicOpacityPercent = CodexMonitorDefaults.AcrylicOpacityPercent;
    private bool m_SnapshotShowResetTimeInPlugins = CodexMonitorDefaults.ShowResetTimeInPlugins;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? RefreshRequested;

    public event EventHandler? SaveSettingsRequested;

    public event EventHandler? InstallLiteMonitorPluginRequested;

    public event EventHandler? InstallTrafficMonitorPluginRequested;

    public event EventHandler? ExitRequested;

    public QuotaViewModel FiveHourQuota { get; } = new("5-Hour");

    public QuotaViewModel SevenDayQuota { get; } = new("7-Day");

    public ICommand ShowHomeCommand { get; }

    public ICommand ShowSettingsCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand SaveSettingsCommand { get; }

    public ICommand InstallLiteMonitorPluginCommand { get; }

    public ICommand InstallTrafficMonitorPluginCommand { get; }

    public ICommand BrowseLiteMonitorCommand { get; }

    public ICommand BrowseTrafficMonitorCommand { get; }

    public ICommand AutoDetectLiteMonitorCommand { get; }

    public ICommand AutoDetectTrafficMonitorCommand { get; }

    public ICommand ExitCommand { get; }

    public string PlanDisplay
    {
        get => m_PlanDisplay;
        private set => SetField(ref m_PlanDisplay, value);
    }

    public Media.Brush PlanBadgeBrush
    {
        get => m_PlanBadgeBrush;
        private set => SetField(ref m_PlanBadgeBrush, value);
    }

    public string UpdatedAtDisplay
    {
        get => m_UpdatedAtDisplay;
        private set => SetField(ref m_UpdatedAtDisplay, value);
    }

    public SettingsStatus SettingsStatus
    {
        get => m_SettingsStatus;
        private set
        {
            if (SetField(ref m_SettingsStatus, value))
            {
                OnPropertyChanged(nameof(SettingsStatusText));
                OnPropertyChanged(nameof(SettingsStatusBrush));
            }
        }
    }

    public string SettingsStatusText => m_SettingsStatus switch
    {
        SettingsStatus.Saved => "Changes saved",
        SettingsStatus.Unsaved => "Unsaved changes",
        _ => string.Empty,
    };

    public Media.Brush SettingsStatusBrush => m_SettingsStatus == SettingsStatus.Unsaved ? s_YellowBrush : s_GreenBrush;

    public string ServiceStatus
    {
        get => m_ServiceStatus;
        private set => SetField(ref m_ServiceStatus, value);
    }

    public string SourceDisplay
    {
        get => m_SourceDisplay;
        private set => SetField(ref m_SourceDisplay, value);
    }

    public string LiteMonitorDir
    {
        get => m_LiteMonitorDir;
        set
        {
            if (SetField(ref m_LiteMonitorDir, value))
            {
                OnPropertyChanged(nameof(LiteMonitorDirDisplay));
                EvaluateDirtyState();
            }
        }
    }

    public string LiteMonitorDirDisplay => FormatPluginPath(m_LiteMonitorDir);

    public string TrafficMonitorDir
    {
        get => m_TrafficMonitorDir;
        set
        {
            if (SetField(ref m_TrafficMonitorDir, value))
            {
                OnPropertyChanged(nameof(TrafficMonitorDirDisplay));
                EvaluateDirtyState();
            }
        }
    }

    public string TrafficMonitorDirDisplay => FormatPluginPath(m_TrafficMonitorDir);

    public bool IsDetectingLiteMonitor
    {
        get => m_IsDetectingLiteMonitor;
        set
        {
            if (SetField(ref m_IsDetectingLiteMonitor, value))
            {
                OnPropertyChanged(nameof(IsLiteMonitorActionsEnabled));
            }
        }
    }

    public bool IsDetectingTrafficMonitor
    {
        get => m_IsDetectingTrafficMonitor;
        set
        {
            if (SetField(ref m_IsDetectingTrafficMonitor, value))
            {
                OnPropertyChanged(nameof(IsTrafficMonitorActionsEnabled));
            }
        }
    }

    // Plugin row actions are disabled while an auto detect is running to avoid racing the detected path.
    public bool IsLiteMonitorActionsEnabled => !m_IsDetectingLiteMonitor;

    public bool IsTrafficMonitorActionsEnabled => !m_IsDetectingTrafficMonitor;

    public string PortText
    {
        get => m_PortText;
        set
        {
            if (SetField(ref m_PortText, value))
            {
                EvaluateDirtyState();
            }
        }
    }

    public string RefreshIntervalText
    {
        get => m_RefreshIntervalText;
        set
        {
            if (SetField(ref m_RefreshIntervalText, value))
            {
                EvaluateDirtyState();
            }
        }
    }

    public string ThemeMode
    {
        get => m_ThemeMode;
        set
        {
            if (SetField(ref m_ThemeMode, NormalizeThemeMode(value)))
            {
                EvaluateDirtyState();
            }
        }
    }

    public string[] ThemeModeOptions { get; } =
    [
        AppSettings.ThemeModeSystem,
        AppSettings.ThemeModeLight,
        AppSettings.ThemeModeDark,
    ];

    public bool StartWithWindows
    {
        get => m_StartWithWindows;
        set
        {
            if (SetField(ref m_StartWithWindows, value))
            {
                EvaluateDirtyState();
            }
        }
    }

    public bool AcrylicEnabled
    {
        get => m_AcrylicEnabled;
        set
        {
            if (SetField(ref m_AcrylicEnabled, value))
            {
                EvaluateDirtyState();
            }
        }
    }

    public int AcrylicOpacityPercent
    {
        get => m_AcrylicOpacityPercent;
        set
        {
            int clamped = Math.Clamp(value, CodexMonitorDefaults.MinimumAcrylicOpacityPercent, CodexMonitorDefaults.MaximumAcrylicOpacityPercent);
            if (SetField(ref m_AcrylicOpacityPercent, clamped))
            {
                OnPropertyChanged(nameof(AcrylicOpacityDisplay));
                EvaluateDirtyState();
            }
        }
    }

    public bool ShowResetTimeInPlugins
    {
        get => m_ShowResetTimeInPlugins;
        set
        {
            if (SetField(ref m_ShowResetTimeInPlugins, value))
            {
                EvaluateDirtyState();
            }
        }
    }

    public string AcrylicOpacityDisplay => $"{m_AcrylicOpacityPercent}%";

    public int AcrylicOpacityMinimum => CodexMonitorDefaults.MinimumAcrylicOpacityPercent;

    public int AcrylicOpacityMaximum => CodexMonitorDefaults.MaximumAcrylicOpacityPercent;

    public bool IsHomeVisible => m_CurrentPage == k_HomePageName;

    public bool IsSettingsVisible => m_CurrentPage == k_SettingsPageName;

    public bool IsHomeSelected => m_CurrentPage == k_HomePageName;

    public bool IsSettingsSelected => m_CurrentPage == k_SettingsPageName;

    public bool IsRefreshing
    {
        get => m_IsRefreshing;
        set => SetField(ref m_IsRefreshing, value);
    }

    public bool IsModalOpen
    {
        get => m_IsModalOpen;
        private set => SetField(ref m_IsModalOpen, value);
    }

    /// <summary>
    /// Creates a view model for the WPF tray popup.
    /// </summary>
    public TrayPopupViewModel(AppSettings settings)
    {
        m_Settings = settings;
        LoadSettings(settings);
        ShowHomeCommand = new RelayCommand(_ => ShowHome());
        ShowSettingsCommand = new RelayCommand(_ => ShowSettings());
        RefreshCommand = new RelayCommand(_ => RefreshRequested?.Invoke(this, EventArgs.Empty));
        SaveSettingsCommand = new RelayCommand(_ => SaveSettingsRequested?.Invoke(this, EventArgs.Empty));
        InstallLiteMonitorPluginCommand = new RelayCommand(_ => InstallLiteMonitorPluginRequested?.Invoke(this, EventArgs.Empty));
        InstallTrafficMonitorPluginCommand = new RelayCommand(_ => InstallTrafficMonitorPluginRequested?.Invoke(this, EventArgs.Empty));
        BrowseLiteMonitorCommand = new RelayCommand(_ => BrowseMonitorFolder("Select LiteMonitor folder", LiteMonitorDir, value => LiteMonitorDir = value));
        BrowseTrafficMonitorCommand = new RelayCommand(_ => BrowseMonitorFolder("Select TrafficMonitor folder", TrafficMonitorDir, value => TrafficMonitorDir = value));
        AutoDetectLiteMonitorCommand = new RelayCommand(async _ => await DetectLiteMonitorAsync(showNotFound: true));
        AutoDetectTrafficMonitorCommand = new RelayCommand(async _ => await DetectTrafficMonitorAsync(showNotFound: true));
        ExitCommand = new RelayCommand(_ => ExitRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Loads settings values into editable properties.
    /// </summary>
    public void LoadSettings(AppSettings settings)
    {
        m_SuppressDirtyTracking = true;
        try
        {
            LiteMonitorDir = settings.LiteMonitorDir;
            TrafficMonitorDir = settings.TrafficMonitorDir;
            PortText = settings.Port.ToString(CultureInfo.InvariantCulture);
            RefreshIntervalText = settings.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture);
            ThemeMode = settings.ThemeMode;
            StartWithWindows = settings.StartWithWindows;
            AcrylicEnabled = settings.AcrylicEnabled;
            AcrylicOpacityPercent = settings.AcrylicOpacityPercent;
            ShowResetTimeInPlugins = settings.ShowResetTimeInPlugins;
        }
        finally
        {
            m_SuppressDirtyTracking = false;
        }

        CaptureSnapshot(SettingsStatus.Clean);
    }

    /// <summary>
    /// Captures the current editable values as the saved snapshot baseline.
    /// </summary>
    private void CaptureSnapshot(SettingsStatus baseline)
    {
        m_SnapshotLiteMonitorDir = m_LiteMonitorDir;
        m_SnapshotTrafficMonitorDir = m_TrafficMonitorDir;
        m_SnapshotPortText = m_PortText;
        m_SnapshotRefreshIntervalText = m_RefreshIntervalText;
        m_SnapshotThemeMode = m_ThemeMode;
        m_SnapshotStartWithWindows = m_StartWithWindows;
        m_SnapshotAcrylicEnabled = m_AcrylicEnabled;
        m_SnapshotAcrylicOpacityPercent = m_AcrylicOpacityPercent;
        m_SnapshotShowResetTimeInPlugins = m_ShowResetTimeInPlugins;
        m_SettingsBaseline = baseline;
        SettingsStatus = baseline;
    }

    /// <summary>
    /// Recomputes the settings save status against the saved snapshot.
    /// </summary>
    private void EvaluateDirtyState()
    {
        if (m_SuppressDirtyTracking)
        {
            return;
        }

        bool matchesSnapshot =
            m_LiteMonitorDir == m_SnapshotLiteMonitorDir &&
            m_TrafficMonitorDir == m_SnapshotTrafficMonitorDir &&
            m_PortText == m_SnapshotPortText &&
            m_RefreshIntervalText == m_SnapshotRefreshIntervalText &&
            m_ThemeMode == m_SnapshotThemeMode &&
            m_StartWithWindows == m_SnapshotStartWithWindows &&
            m_AcrylicEnabled == m_SnapshotAcrylicEnabled &&
            m_AcrylicOpacityPercent == m_SnapshotAcrylicOpacityPercent &&
            m_ShowResetTimeInPlugins == m_SnapshotShowResetTimeInPlugins;

        SettingsStatus = matchesSnapshot ? m_SettingsBaseline : SettingsStatus.Unsaved;
    }

    /// <summary>
    /// Applies editable properties to the shared settings model.
    /// </summary>
    public bool TryApplySettings(out string message)
    {
        int port = ClampOrDefault(PortText, CodexMonitorDefaults.MinimumPort, CodexMonitorDefaults.MaximumPort, CodexMonitorDefaults.Port);
        int refreshInterval = ClampOrDefault(
            RefreshIntervalText,
            CodexMonitorDefaults.MinimumRefreshIntervalMinutes,
            CodexMonitorDefaults.MaximumRefreshIntervalMinutes,
            CodexMonitorDefaults.RefreshIntervalMinutes);
        m_SuppressDirtyTracking = true;
        try
        {
            PortText = port.ToString(CultureInfo.InvariantCulture);
            RefreshIntervalText = refreshInterval.ToString(CultureInfo.InvariantCulture);
            LiteMonitorDir = LiteMonitorDir.Trim();
            TrafficMonitorDir = TrafficMonitorDir.Trim();
        }
        finally
        {
            m_SuppressDirtyTracking = false;
        }

        m_Settings.LiteMonitorDir = LiteMonitorDir;
        m_Settings.TrafficMonitorDir = TrafficMonitorDir;
        m_Settings.Port = port;
        m_Settings.RefreshIntervalMinutes = refreshInterval;
        m_Settings.StartWithWindows = StartWithWindows;
        m_Settings.ThemeMode = ThemeMode;
        m_Settings.AcrylicEnabled = AcrylicEnabled;
        m_Settings.AcrylicOpacityPercent = AcrylicOpacityPercent;
        m_Settings.ShowResetTimeInPlugins = ShowResetTimeInPlugins;
        CaptureSnapshot(SettingsStatus.Saved);
        message = "Changes saved";
        return true;
    }

    /// <summary>
    /// Parses digits from raw input and clamps to the range, falling back to a default when empty.
    /// </summary>
    private static int ClampOrDefault(string rawText, int minimum, int maximum, int fallback)
    {
        string digits = KeepDigits(rawText);
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return fallback;
        }

        return Math.Clamp(value, minimum, maximum);
    }

    /// <summary>
    /// Updates status fields from the current service state.
    /// </summary>
    public void UpdateStatus(bool isRunning, int port, UsageResponse? response, string? error)
    {
        ServiceStatus = error == null
            ? $"Service: {(isRunning ? "Running" : "Stopped")} on {CodexMonitorDefaults.Host}:{port}"
            : $"Service: Error on {CodexMonitorDefaults.Host}:{port} - {error}";

        SourceDisplay = $"Source: {FormatSource(response)}";

        if (response == null)
        {
            PlanDisplay = "None";
            PlanBadgeBrush = s_PlanBadgeInactiveBrush;
            UpdatedAtDisplay = FormatUpdatedAt(null);
            FiveHourQuota.UpdateUnavailable();
            SevenDayQuota.UpdateUnavailable();
            return;
        }

        if (!response.Available)
        {
            PlanDisplay = "None";
            PlanBadgeBrush = s_PlanBadgeInactiveBrush;
            UpdatedAtDisplay = $"Codex usage unavailable{FormatResponseError(response)}";
            FiveHourQuota.UpdateUnavailable();
            SevenDayQuota.UpdateUnavailable();
            return;
        }

        PlanDisplay = FormatPlan(response.PlanType);
        PlanBadgeBrush = s_PlanBadgeActiveBrush;
        UpdatedAtDisplay = FormatUpdatedAt(response.UpdatedAt);
        FiveHourQuota.Update(response.Limits.FiveHour);
        SevenDayQuota.Update(response.Limits.SevenDay);
    }

    /// <summary>
    /// Shows the home page inside the tray popup.
    /// </summary>
    public void ShowHome()
    {
        SetPage(k_HomePageName);
    }

    /// <summary>
    /// Shows the settings page inside the tray popup.
    /// </summary>
    public void ShowSettings()
    {
        LoadSettings(m_Settings);
        SetPage(k_SettingsPageName);
    }

    /// <summary>
    /// Sets the active popup page.
    /// </summary>
    private void SetPage(string pageName)
    {
        if (m_CurrentPage == pageName)
        {
            return;
        }

        m_CurrentPage = pageName;
        OnPropertyChanged(nameof(IsHomeVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
    }

    /// <summary>
    /// Opens a shared folder picker and updates a path property.
    /// </summary>
    private void BrowseMonitorFolder(string description, string currentPath, Action<string> updatePath)
    {
        IsModalOpen = true;
        try
        {
            using Forms.FolderBrowserDialog dialog = new()
            {
                Description = description,
                SelectedPath = Directory.Exists(currentPath) ? currentPath : string.Empty,
                UseDescriptionForTitle = true,
            };
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                updatePath(dialog.SelectedPath);
            }
        }
        finally
        {
            IsModalOpen = false;
        }
    }

    /// <summary>
    /// Auto detects the LiteMonitor directory off the UI thread.
    /// </summary>
    public async Task DetectLiteMonitorAsync(bool showNotFound)
    {
        if (m_IsDetectingLiteMonitor)
        {
            return;
        }

        IsDetectingLiteMonitor = true;
        try
        {
            // Manual detect always runs a full scan instead of short-circuiting on the current path.
            string detected = await Task.Run(() => LiteMonitorLocator.AutoDetect()).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(detected))
            {
                LiteMonitorDir = CodexMonitorDefaults.PluginPathNone;
                if (showNotFound)
                {
                    ShowModalMessage("LiteMonitor was not found.");
                }

                return;
            }

            LiteMonitorDir = detected;
        }
        finally
        {
            IsDetectingLiteMonitor = false;
        }
    }

    /// <summary>
    /// Auto detects the TrafficMonitor directory off the UI thread.
    /// </summary>
    public async Task DetectTrafficMonitorAsync(bool showNotFound)
    {
        if (m_IsDetectingTrafficMonitor)
        {
            return;
        }

        IsDetectingTrafficMonitor = true;
        try
        {
            // Manual detect always runs a full scan instead of short-circuiting on the current path.
            string detected = await Task.Run(() => TrafficMonitorLocator.AutoDetect()).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(detected))
            {
                TrafficMonitorDir = CodexMonitorDefaults.PluginPathNone;
                if (showNotFound)
                {
                    ShowModalMessage("TrafficMonitor was not found.");
                }

                return;
            }

            TrafficMonitorDir = detected;
        }
        finally
        {
            IsDetectingTrafficMonitor = false;
        }
    }

    /// <summary>
    /// Formats a plugin directory for the secondary path label.
    /// </summary>
    private static string FormatPluginPath(string? directory)
    {
        string value = PluginPathOrEmpty(directory);
        return $"Path: {(value.Length == 0 ? CodexMonitorDefaults.PluginPathNone : value)}";
    }

    /// <summary>
    /// Returns the configured directory, treating the None sentinel as empty.
    /// </summary>
    public static string PluginPathOrEmpty(string? directory)
    {
        string value = (directory ?? string.Empty).Trim();
        return string.Equals(value, CodexMonitorDefaults.PluginPathNone, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : value;
    }

    /// <summary>
    /// Shows a WPF message box without triggering popup auto hide.
    /// </summary>
    private void ShowModalMessage(string message)
    {
        IsModalOpen = true;
        try
        {
            System.Windows.MessageBox.Show(message, CodexMonitorDefaults.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            IsModalOpen = false;
        }
    }

    /// <summary>
    /// Formats the usage source for display.
    /// </summary>
    private static string FormatSource(UsageResponse? response)
    {
        string? source = response?.SourceFile;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = response?.Source;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return "unavailable";
        }

        return TrimMiddle(source, 42);
    }

    /// <summary>
    /// Formats the plan type as a supported subscription label.
    /// </summary>
    private static string FormatPlan(string? planType)
    {
        string normalized = (planType ?? string.Empty).Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "free" => "Free",
            "go" => "Go",
            "plus" => "Plus",
            "pro" or "pro_5x" or "pro5x" => "Pro5x",
            "pro_20x" or "pro20x" => "Pro20x",
            "chatgpt" => "Plus",
            _ => "Unknown",
        };
    }

    /// <summary>
    /// Formats an update timestamp for compact display.
    /// </summary>
    private static string FormatUpdatedAt(string? updatedAt)
    {
        if (string.IsNullOrWhiteSpace(updatedAt))
        {
            return "Updated waiting";
        }

        return DateTimeOffset.TryParse(updatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsed)
            ? $"Updated {parsed.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}"
            : updatedAt;
    }

    /// <summary>
    /// Keeps only decimal digits from a raw input string.
    /// </summary>
    private static string KeepDigits(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            if (character is >= '0' and <= '9')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Normalizes a theme mode string for UI selection.
    /// </summary>
    private static string NormalizeThemeMode(string? themeMode)
    {
        return themeMode?.Trim().ToLowerInvariant() switch
        {
            "light" => AppSettings.ThemeModeLight,
            "dark" => AppSettings.ThemeModeDark,
            _ => AppSettings.ThemeModeSystem,
        };
    }

    /// <summary>
    /// Formats a response error suffix for display.
    /// </summary>
    private static string FormatResponseError(UsageResponse response)
    {
        return string.IsNullOrWhiteSpace(response.Error) ? string.Empty : $": {response.Error}";
    }

    /// <summary>
    /// Trims long paths in the middle for compact labels.
    /// </summary>
    private static string TrimMiddle(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        int keep = Math.Max(1, (maxLength - 3) / 2);
        return value[..keep] + "..." + value[^keep..];
    }

    /// <summary>
    /// Sets a property value and notifies listeners.
    /// </summary>
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Raises a property change notification.
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal sealed class QuotaViewModel : INotifyPropertyChanged
    {
        private string m_Title;
        private int m_RemainingPercent;
        private string m_PercentText = "0%";
        private string m_ResetText = "unknown";
        private Media.Brush m_AccentBrush = s_GreenBrush;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title
        {
            get => m_Title;
            private set => SetField(ref m_Title, value);
        }

        public int RemainingPercent
        {
            get => m_RemainingPercent;
            private set => SetField(ref m_RemainingPercent, value);
        }

        public string PercentText
        {
            get => m_PercentText;
            private set => SetField(ref m_PercentText, value);
        }

        public string ResetText
        {
            get => m_ResetText;
            private set => SetField(ref m_ResetText, value);
        }

        public Media.Brush AccentBrush
        {
            get => m_AccentBrush;
            private set => SetField(ref m_AccentBrush, value);
        }

        /// <summary>
        /// Creates a quota display model.
        /// </summary>
        public QuotaViewModel(string title)
        {
            m_Title = title;
        }

        /// <summary>
        /// Updates the quota display from a usage limit.
        /// </summary>
        public void Update(UsageLimit limit)
        {
            int remaining = Math.Max(0, Math.Min(100, limit.RemainingPercent));
            RemainingPercent = remaining;
            PercentText = $"{remaining}%";
            ResetText = string.IsNullOrWhiteSpace(limit.ResetLabel) ? "unknown" : limit.ResetLabel;
            AccentBrush = GetAccentBrush(remaining);
        }

        /// <summary>
        /// Updates the quota display for an unavailable response.
        /// </summary>
        public void UpdateUnavailable()
        {
            RemainingPercent = 0;
            PercentText = "0%";
            ResetText = "unknown";
            AccentBrush = s_RedBrush;
        }

        /// <summary>
        /// Selects a quota color from the remaining percent.
        /// </summary>
        private static Media.Brush GetAccentBrush(int remainingPercent)
        {
            if (remainingPercent > 50)
            {
                return s_GreenBrush;
            }

            return remainingPercent >= 20 ? s_YellowBrush : s_RedBrush;
        }

        /// <summary>
        /// Sets a property value and notifies listeners.
        /// </summary>
        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Raises a property change notification.
        /// </summary>
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
