using CodexMonitor.Core;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace CodexMonitor.App;

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
    private string m_StatusMessage = "Starting CodexMonitor...";
    private string m_ServiceStatus = "Service: starting";
    private string m_SourceDisplay = "Source: unavailable";
    private string m_LiteMonitorDir = string.Empty;
    private string m_TrafficMonitorDir = string.Empty;
    private string m_PortText = CodexMonitorDefaults.Port.ToString(CultureInfo.InvariantCulture);
    private string m_RefreshIntervalText = CodexMonitorDefaults.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture);
    private string m_ThemeMode = AppSettings.ThemeModeSystem;
    private bool m_StartWithWindows;
    private bool m_IsRefreshing;
    private bool m_IsModalOpen;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? RefreshRequested;

    public event EventHandler? SaveSettingsRequested;

    public event EventHandler? InstallLiteMonitorPluginRequested;

    public event EventHandler? InstallTrafficMonitorPluginRequested;

    public event EventHandler? ExitRequested;

    public QuotaViewModel FiveHourQuota { get; } = new("5 hours");

    public QuotaViewModel WeeklyQuota { get; } = new("Weekly");

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

    public string StatusMessage
    {
        get => m_StatusMessage;
        private set => SetField(ref m_StatusMessage, value);
    }

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
        set => SetField(ref m_LiteMonitorDir, value);
    }

    public string TrafficMonitorDir
    {
        get => m_TrafficMonitorDir;
        set => SetField(ref m_TrafficMonitorDir, value);
    }

    public string PortText
    {
        get => m_PortText;
        set => SetField(ref m_PortText, value);
    }

    public string RefreshIntervalText
    {
        get => m_RefreshIntervalText;
        set => SetField(ref m_RefreshIntervalText, value);
    }

    public string ThemeMode
    {
        get => m_ThemeMode;
        set => SetField(ref m_ThemeMode, NormalizeThemeMode(value));
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
        set => SetField(ref m_StartWithWindows, value);
    }

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
        AutoDetectLiteMonitorCommand = new RelayCommand(_ => AutoDetectLiteMonitor());
        AutoDetectTrafficMonitorCommand = new RelayCommand(_ => AutoDetectTrafficMonitor());
        ExitCommand = new RelayCommand(_ => ExitRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Loads settings values into editable properties.
    /// </summary>
    public void LoadSettings(AppSettings settings)
    {
        LiteMonitorDir = settings.LiteMonitorDir;
        TrafficMonitorDir = settings.TrafficMonitorDir;
        PortText = settings.Port.ToString(CultureInfo.InvariantCulture);
        RefreshIntervalText = settings.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        ThemeMode = settings.ThemeMode;
        StartWithWindows = settings.StartWithWindows;
    }

    /// <summary>
    /// Applies editable properties to the shared settings model.
    /// </summary>
    public bool TryApplySettings(out string message)
    {
        if (!int.TryParse(PortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port <= 0 || port > 65535)
        {
            message = "Port must be between 1 and 65535.";
            StatusMessage = message;
            return false;
        }

        if (!int.TryParse(RefreshIntervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int refreshInterval) ||
            refreshInterval < CodexMonitorDefaults.MinimumRefreshIntervalMinutes ||
            refreshInterval > CodexMonitorDefaults.MaximumRefreshIntervalMinutes)
        {
            message = $"Refresh interval must be between {CodexMonitorDefaults.MinimumRefreshIntervalMinutes} and {CodexMonitorDefaults.MaximumRefreshIntervalMinutes} minutes.";
            StatusMessage = message;
            return false;
        }

        m_Settings.LiteMonitorDir = LiteMonitorDir.Trim();
        m_Settings.TrafficMonitorDir = TrafficMonitorDir.Trim();
        m_Settings.Port = port;
        m_Settings.RefreshIntervalMinutes = refreshInterval;
        m_Settings.StartWithWindows = StartWithWindows;
        m_Settings.ThemeMode = ThemeMode;
        message = "Settings saved.";
        StatusMessage = message;
        return true;
    }

    /// <summary>
    /// Updates status fields from the current service state.
    /// </summary>
    public void UpdateStatus(bool isRunning, int port, UsageResponse? response, string? error)
    {
        ServiceStatus = error == null
            ? $"Service: {(isRunning ? "Running" : "Stopped")} on 127.0.0.1:{port}"
            : $"Service: Error on 127.0.0.1:{port} - {error}";

        SourceDisplay = $"Source: {FormatSource(response)}";
        UpdatedAtDisplay = FormatUpdatedAt(response?.UpdatedAt);

        if (response == null)
        {
            PlanDisplay = "None";
            PlanBadgeBrush = s_PlanBadgeInactiveBrush;
            FiveHourQuota.UpdateUnavailable();
            WeeklyQuota.UpdateUnavailable();
            return;
        }

        if (!response.Available)
        {
            PlanDisplay = "None";
            PlanBadgeBrush = s_PlanBadgeInactiveBrush;
            StatusMessage = $"Codex usage unavailable{FormatResponseError(response)}";
            FiveHourQuota.UpdateUnavailable();
            WeeklyQuota.UpdateUnavailable();
            return;
        }

        PlanDisplay = FormatPlan(response.PlanType);
        PlanBadgeBrush = s_PlanBadgeActiveBrush;
        StatusMessage = string.Empty;
        FiveHourQuota.Update(response.Limits.FiveHour);
        WeeklyQuota.Update(response.Limits.Weekly);
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
    /// Sets a short user-visible operation message.
    /// </summary>
    public void SetMessage(string message)
    {
        StatusMessage = message;
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
    /// Auto detects the LiteMonitor directory.
    /// </summary>
    private void AutoDetectLiteMonitor()
    {
        string detected = LiteMonitorLocator.AutoDetect(LiteMonitorDir);
        if (string.IsNullOrWhiteSpace(detected))
        {
            ShowModalMessage("LiteMonitor was not found.");
            return;
        }

        LiteMonitorDir = detected;
        StatusMessage = "LiteMonitor folder detected.";
    }

    /// <summary>
    /// Auto detects the TrafficMonitor directory.
    /// </summary>
    private void AutoDetectTrafficMonitor()
    {
        string detected = TrafficMonitorLocator.AutoDetect(TrafficMonitorDir);
        if (string.IsNullOrWhiteSpace(detected))
        {
            ShowModalMessage("TrafficMonitor was not found.");
            return;
        }

        TrafficMonitorDir = detected;
        StatusMessage = "TrafficMonitor folder detected.";
    }

    /// <summary>
    /// Shows a WPF message box without triggering popup auto hide.
    /// </summary>
    private void ShowModalMessage(string message)
    {
        IsModalOpen = true;
        try
        {
            StatusMessage = message;
            System.Windows.MessageBox.Show(message, "CodexMonitor", MessageBoxButton.OK, MessageBoxImage.Information);
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
        private string m_ResetText = "Resets unknown";
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
            ResetText = string.IsNullOrWhiteSpace(limit.ResetLabel) ? "Resets unknown" : $"Resets {limit.ResetLabel}";
            AccentBrush = GetAccentBrush(remaining);
        }

        /// <summary>
        /// Updates the quota display for an unavailable response.
        /// </summary>
        public void UpdateUnavailable()
        {
            RemainingPercent = 0;
            PercentText = "0%";
            ResetText = "Resets unknown";
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
