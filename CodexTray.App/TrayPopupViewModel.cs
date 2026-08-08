using CodexTray.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace CodexTray.App;

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

internal sealed record TokenCostDisplay(string Cost, string Tokens);

internal sealed record TokenCostChartDay(string Label, double BarHeight, string Tooltip);

internal sealed record InAppDialogRequest(
    string Title,
    string Message,
    string PrimaryButtonText,
    string? SecondaryButtonText = null,
    Action? PrimaryAction = null);

internal sealed partial class TrayPopupViewModel : ObservableObject
{
    private const string k_CodexPageName = "Codex";
    private const string k_CursorPageName = "Cursor";
    private const string k_ApiPageName = "API";
    private const string k_SettingsPageName = "Settings";
    private const string k_AboutPageName = "About";
    private const string k_RepositoryUrl = "https://github.com/SnowyLake/CodexTray";
    private const double k_TokenCostChartMaximumBarHeight = 96;

    private static readonly Media.Brush s_GreenBrush = new Media.SolidColorBrush(Media.Color.FromRgb(26, 188, 137));
    private static readonly Media.Brush s_YellowBrush = new Media.SolidColorBrush(Media.Color.FromRgb(226, 176, 54));
    private static readonly Media.Brush s_RedBrush = new Media.SolidColorBrush(Media.Color.FromRgb(224, 91, 77));
    private static readonly Media.Brush s_PlanBadgeActiveBrush = s_GreenBrush;
    private static readonly Media.Brush s_PlanBadgeInactiveBrush = new Media.SolidColorBrush(Media.Color.FromRgb(107, 122, 117));
    private static readonly TokenCostDisplay s_UnavailableTokenCostDisplay = new("N/A", "N/A");

    private readonly AppSettings m_Settings;
    private string m_CurrentPage = k_CodexPageName;
    private string m_ThemeMode = AppSettings.ThemeModeSystem;
    private string m_TokenUnit = AppSettings.TokenUnitEnglish;
    private PageItem m_VisiblePages = PageItem.All;
    private bool m_MicaEnabled;
    private bool m_HideInvalidProgressBars = CodexTrayDefaults.HideInvalidProgressBars;
    private bool m_IsInAppDialogOpen;
    private bool m_IsNativeModalOpen;
    private ApiUsageRefreshStatus? m_ApiUsageStatus;
    private int m_ApiUsageErrorCount;
    private int m_ApiUsageMonitorCount;
    private DateTimeOffset? m_ApiUsageUpdatedAt;
    private Action? m_InAppDialogPrimaryAction;

    private SettingsStatus m_SettingsBaseline = SettingsStatus.Clean;
    private bool m_SuppressDirtyTracking;
    private string m_SnapshotLiteMonitorDir = string.Empty;
    private string m_SnapshotTrafficMonitorDir = string.Empty;
    private string m_SnapshotPortText = string.Empty;
    private string m_SnapshotRefreshIntervalText = string.Empty;
    private string m_SnapshotThemeMode = AppSettings.ThemeModeSystem;
    private string m_SnapshotTokenUnit = AppSettings.TokenUnitEnglish;
    private PageItem m_SnapshotVisiblePages = PageItem.All;
    private bool m_SnapshotStartWithWindows;
    private bool m_SnapshotMicaEnabled;
    private string m_SnapshotWindowWidthText = string.Empty;
    private string m_SnapshotWindowHeightText = string.Empty;
    private bool m_SnapshotShowResetTimeInPlugins = CodexTrayDefaults.ShowResetTimeInPlugins;
    private bool m_SnapshotUseAbsoluteResetTime = CodexTrayDefaults.UseAbsoluteResetTime;
    private bool m_SnapshotHideInvalidProgressBars = CodexTrayDefaults.HideInvalidProgressBars;

    public event EventHandler? SaveSettingsRequested;

    public event EventHandler? ApiMonitorsChanged;

    public event EventHandler? InstallLiteMonitorPluginRequested;

    public event EventHandler? InstallTrafficMonitorPluginRequested;

    public event Action<InAppDialogRequest>? InAppDialogRequested;

    public QuotaViewModel FiveHourQuota { get; } = new("5-Hour");

    public QuotaViewModel SevenDayQuota { get; } = new("7-Day");

    public QuotaViewModel SparkFiveHourQuota { get; } = new("GPT-5.3 Spark 5-Hour");

    public QuotaViewModel SparkSevenDayQuota { get; } = new("GPT-5.3 Spark 7-Day");

    public QuotaViewModel CursorTotalQuota { get; } = new("Total");

    public QuotaViewModel CursorAutoQuota { get; } = new("First Party");

    public QuotaViewModel CursorApiQuota { get; } = new("APIs");

    public IReadOnlyList<TokenCostRowViewModel> CodexTokenCostRows { get; } = CreateCompactTokenCostRows();

    public IReadOnlyList<TokenCostRowViewModel> CursorTokenCostRows { get; } = CreateCompactTokenCostRows();

    [ObservableProperty]
    public partial IReadOnlyList<TokenCostChartDay> CodexTokenCostChartDays { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TokenCostChartDay> CursorTokenCostChartDays { get; private set; } = [];

    public IRelayCommand OpenRepositoryCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IRelayCommand SaveSettingsCommand { get; }

    public IRelayCommand InstallLiteMonitorPluginCommand { get; }

    public IRelayCommand InstallTrafficMonitorPluginCommand { get; }

    public IRelayCommand BrowseLiteMonitorCommand { get; }

    public IRelayCommand BrowseTrafficMonitorCommand { get; }

    public IAsyncRelayCommand AutoDetectLiteMonitorCommand { get; }

    public IAsyncRelayCommand AutoDetectTrafficMonitorCommand { get; }

    public ObservableCollection<ApiMonitorViewModel> ApiMonitors { get; } = [];

    [ObservableProperty]
    public partial string PlanDisplay { get; private set; } = "UNKNOWN";

    [ObservableProperty]
    public partial Media.Brush PlanBadgeBrush { get; private set; } = s_PlanBadgeInactiveBrush;

    [ObservableProperty]
    public partial Media.Brush StatusDotBrush { get; private set; } = s_RedBrush;

    [ObservableProperty]
    public partial string UpdatedAtDisplay { get; private set; } = "Waiting for first refresh";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsStatusText))]
    [NotifyPropertyChangedFor(nameof(SettingsStatusBrush))]
    [NotifyCanExecuteChangedFor(nameof(SaveSettingsCommand))]
    public partial SettingsStatus SettingsStatus { get; private set; } = SettingsStatus.Clean;

    public string SettingsStatusText => SettingsStatus switch
    {
        SettingsStatus.Saved => "Changes saved",
        SettingsStatus.Unsaved => "Unsaved changes",
        _ => string.Empty,
    };

    public Media.Brush SettingsStatusBrush => SettingsStatus == SettingsStatus.Unsaved ? s_YellowBrush : s_GreenBrush;

    [ObservableProperty]
    public partial string ServiceStatus { get; private set; } = "Service: starting";

    [ObservableProperty]
    public partial string SourceDisplay { get; private set; } = "Source: unavailable";

    [ObservableProperty]
    public partial string ResetCreditsDisplay { get; private set; } = "N/A";

    [ObservableProperty]
    public partial string ResetCreditsExpiryDates { get; private set; } = "unknown";

    [ObservableProperty]
    public partial bool HasResetCredits { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiteMonitorDirDisplay))]
    public partial string LiteMonitorDir { get; set; } = string.Empty;

    public string LiteMonitorDirDisplay => FormatPluginPath(LiteMonitorDir);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrafficMonitorDirDisplay))]
    public partial string TrafficMonitorDir { get; set; } = string.Empty;

    public string TrafficMonitorDirDisplay => FormatPluginPath(TrafficMonitorDir);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiteMonitorActionsEnabled))]
    public partial bool IsDetectingLiteMonitor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrafficMonitorActionsEnabled))]
    public partial bool IsDetectingTrafficMonitor { get; set; }

    // Plugin row actions are disabled while an auto detect is running to avoid racing the detected path.
    public bool IsLiteMonitorActionsEnabled => !IsDetectingLiteMonitor;

    public bool IsTrafficMonitorActionsEnabled => !IsDetectingTrafficMonitor;

    [ObservableProperty]
    public partial string PortText { get; set; } = CodexTrayDefaults.Port.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string RefreshIntervalText { get; set; } = CodexTrayDefaults.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture);

    public string ThemeMode
    {
        get => m_ThemeMode;
        set
        {
            if (SetProperty(ref m_ThemeMode, NormalizeThemeMode(value)))
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

    public string TokenUnit
    {
        get => m_TokenUnit;
        set
        {
            string normalized = value == AppSettings.TokenUnitChinese ? AppSettings.TokenUnitChinese : AppSettings.TokenUnitEnglish;
            if (SetProperty(ref m_TokenUnit, normalized))
            {
                EvaluateDirtyState();
            }
        }
    }

    public string[] TokenUnitOptions { get; } =
    [
        AppSettings.TokenUnitEnglish,
        AppSettings.TokenUnitChinese,
    ];

    public PageItem VisiblePages
    {
        get => m_VisiblePages;
        set
        {
            if (SetProperty(ref m_VisiblePages, value & PageItem.All))
            {
                OnPropertyChanged(nameof(VisiblePagesDisplay));
                OnPropertyChanged(nameof(IsCodexTabVisible));
                OnPropertyChanged(nameof(IsCursorTabVisible));
                OnPropertyChanged(nameof(IsApiTabVisible));
                OnPropertyChanged(nameof(ShowCodexPage));
                OnPropertyChanged(nameof(ShowCursorPage));
                OnPropertyChanged(nameof(ShowApisPage));
                EvaluateDirtyState();
            }
        }
    }

    public string VisiblePagesDisplay => m_VisiblePages switch
    {
        PageItem.None => "None",
        PageItem.All => "All",
        _ => "Custom",
    };

    public bool ShowCodexPage
    {
        get => (m_VisiblePages & PageItem.Codex) != 0;
        set => SetPageItem(PageItem.Codex, value);
    }

    public bool ShowCursorPage
    {
        get => (m_VisiblePages & PageItem.Cursor) != 0;
        set => SetPageItem(PageItem.Cursor, value);
    }

    public bool ShowApisPage
    {
        get => (m_VisiblePages & PageItem.Apis) != 0;
        set => SetPageItem(PageItem.Apis, value);
    }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    public bool MicaEnabled
    {
        get => IsMicaSupported && m_MicaEnabled;
        set
        {
            bool normalized = IsMicaSupported && value;
            if (SetProperty(ref m_MicaEnabled, normalized))
            {
                EvaluateDirtyState();
            }
        }
    }

    /// <summary>
    /// Returns true when Mica is available on Windows 11 or later.
    /// </summary>
    public bool IsMicaSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    [ObservableProperty]
    public partial string WindowWidthText { get; set; } = CodexTrayDefaults.WindowWidth.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string WindowHeightText { get; set; } = CodexTrayDefaults.WindowHeight.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial bool ShowResetTimeInPlugins { get; set; } = CodexTrayDefaults.ShowResetTimeInPlugins;

    [ObservableProperty]
    public partial bool UseAbsoluteResetTime { get; set; } = CodexTrayDefaults.UseAbsoluteResetTime;

    public bool HideInvalidProgressBars
    {
        get => m_HideInvalidProgressBars;
        set
        {
            if (SetProperty(ref m_HideInvalidProgressBars, value))
            {
                EvaluateDirtyState();
            }
        }
    }

    public bool IsUsageSectionHeaderVisible =>
        !m_HideInvalidProgressBars || FiveHourQuota.IsVisible || SevenDayQuota.IsVisible || SparkFiveHourQuota.IsVisible || SparkSevenDayQuota.IsVisible;

    public bool IsSparkQuotaSectionVisible => SparkFiveHourQuota.IsVisible || SparkSevenDayQuota.IsVisible;

    public bool IsCodexVisible => m_CurrentPage == k_CodexPageName;

    public bool IsCursorVisible => m_CurrentPage == k_CursorPageName;

    public bool IsApiVisible => m_CurrentPage == k_ApiPageName;

    public bool IsSettingsVisible => m_CurrentPage == k_SettingsPageName;

    public bool IsAboutVisible => m_CurrentPage == k_AboutPageName;

    public bool IsCodexSelected => m_CurrentPage == k_CodexPageName;

    public bool IsCursorSelected => m_CurrentPage == k_CursorPageName;

    public bool IsApiSelected => m_CurrentPage == k_ApiPageName;

    public bool IsSettingsSelected => m_CurrentPage == k_SettingsPageName;

    public bool IsCodexTabVisible => (m_VisiblePages & PageItem.Codex) != 0;

    public bool IsCursorTabVisible => (m_VisiblePages & PageItem.Cursor) != 0;

    public bool IsApiTabVisible => (m_VisiblePages & PageItem.Apis) != 0;

    public string AppVersion => typeof(TrayPopupViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown";

    public bool IsApiEmpty => ApiMonitors.Count == 0;

    private int MonitoredApiCount => ApiMonitors.Count(monitor => !monitor.IsPending);

    public bool HasApiMonitorStatus => m_ApiUsageStatus != null;

    public Media.Brush ApiMonitorStatusDotBrush => m_ApiUsageStatus switch
    {
        ApiUsageRefreshStatus.AllAvailable => s_GreenBrush,
        ApiUsageRefreshStatus.PartiallyAvailable => s_YellowBrush,
        _ => s_RedBrush,
    };

    public string ApiMonitorStatusText
    {
        get
        {
            if (m_ApiUsageStatus is not ApiUsageRefreshStatus status)
            {
                return string.Empty;
            }

            int apiCount = m_ApiUsageMonitorCount;
            string apiLabel = apiCount == 1 ? "API" : "APIs";
            string updatedStatus = $"{apiCount} {apiLabel} {FormatUpdatedAt(m_ApiUsageUpdatedAt?.ToString("O", CultureInfo.InvariantCulture))}";
            string errorLabel = m_ApiUsageErrorCount == 1 ? "API" : "APIs";
            return status switch
            {
                ApiUsageRefreshStatus.AllAvailable => updatedStatus,
                ApiUsageRefreshStatus.PartiallyAvailable => $"{updatedStatus}, {m_ApiUsageErrorCount} {errorLabel} Update Error",
                _ => $"{m_ApiUsageErrorCount} {errorLabel} Update Error",
            };
        }
    }

    [ObservableProperty]
    public partial Media.Brush CursorStatusDotBrush { get; private set; } = s_RedBrush;

    [ObservableProperty]
    public partial string CursorPlanDisplay { get; private set; } = "UNKNOWN";

    [ObservableProperty]
    public partial Media.Brush CursorPlanBadgeBrush { get; private set; } = s_PlanBadgeInactiveBrush;

    [ObservableProperty]
    public partial string CursorUpdatedAtDisplay { get; private set; } = "Waiting for first refresh";

    [ObservableProperty]
    public partial string CursorStatusTooltip { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    public bool IsModalOpen => m_IsInAppDialogOpen || m_IsNativeModalOpen;

    public bool IsInAppDialogOpen
    {
        get => m_IsInAppDialogOpen;
        private set
        {
            if (SetProperty(ref m_IsInAppDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsModalOpen));
            }
        }
    }

    [ObservableProperty]
    public partial string InAppDialogTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string InAppDialogMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string InAppDialogPrimaryButtonText { get; private set; } = "OK";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInAppDialogSecondaryButton))]
    public partial string InAppDialogSecondaryButtonText { get; private set; } = string.Empty;

    public bool HasInAppDialogSecondaryButton => InAppDialogSecondaryButtonText.Length > 0;

    /// <summary>
    /// Creates a view model for the WPF tray popup.
    /// </summary>
    public TrayPopupViewModel(AppSettings settings, Func<Task> refreshAsync)
    {
        m_Settings = settings;
        OpenRepositoryCommand = new RelayCommand(() => OpenUrl(k_RepositoryUrl));
        RefreshCommand = new AsyncRelayCommand(refreshAsync);
        SaveSettingsCommand = new RelayCommand(() => SaveSettingsRequested?.Invoke(this, EventArgs.Empty), CanSaveSettings);
        InstallLiteMonitorPluginCommand = new RelayCommand(() => InstallLiteMonitorPluginRequested?.Invoke(this, EventArgs.Empty));
        InstallTrafficMonitorPluginCommand = new RelayCommand(() => InstallTrafficMonitorPluginRequested?.Invoke(this, EventArgs.Empty));
        BrowseLiteMonitorCommand = new RelayCommand(() => BrowseMonitorFolder("Select LiteMonitor folder", LiteMonitorDir, value => LiteMonitorDir = value));
        BrowseTrafficMonitorCommand = new RelayCommand(() => BrowseMonitorFolder("Select TrafficMonitor folder", TrafficMonitorDir, value => TrafficMonitorDir = value));
        AutoDetectLiteMonitorCommand = new AsyncRelayCommand(cancellationToken => DetectLiteMonitorAsync(showNotFound: true, cancellationToken));
        AutoDetectTrafficMonitorCommand = new AsyncRelayCommand(cancellationToken => DetectTrafficMonitorAsync(showNotFound: true, cancellationToken));
        m_CurrentPage = (settings.VisiblePages & PageItem.Codex) != 0
            ? k_CodexPageName
            : (settings.VisiblePages & PageItem.Cursor) != 0
                ? k_CursorPageName
                : (settings.VisiblePages & PageItem.Apis) != 0
                    ? k_ApiPageName
                    : k_SettingsPageName;
        LoadSettings(settings);
        LoadApiMonitors(settings.ApiMonitors);
    }

    /// <summary>
    /// Returns whether settings contain unsaved changes.
    /// </summary>
    private bool CanSaveSettings()
    {
        return SettingsStatus == SettingsStatus.Unsaved;
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
            TokenUnit = settings.TokenUnit;
            VisiblePages = settings.VisiblePages;
            StartWithWindows = settings.StartWithWindows;
            MicaEnabled = settings.MicaEnabled;
            WindowWidthText = settings.WindowWidth.ToString(CultureInfo.InvariantCulture);
            WindowHeightText = settings.WindowHeight.ToString(CultureInfo.InvariantCulture);
            ShowResetTimeInPlugins = settings.ShowResetTimeInPlugins;
            UseAbsoluteResetTime = settings.UseAbsoluteResetTime;
            HideInvalidProgressBars = settings.HideInvalidProgressBars;
        }
        finally
        {
            m_SuppressDirtyTracking = false;
        }

        CaptureSnapshot(SettingsStatus.Clean);
    }

    /// <summary>
    /// Recomputes dirty state after the LiteMonitor directory changes.
    /// </summary>
    partial void OnLiteMonitorDirChanged(string value) => EvaluateDirtyState();

    /// <summary>
    /// Recomputes dirty state after the TrafficMonitor directory changes.
    /// </summary>
    partial void OnTrafficMonitorDirChanged(string value) => EvaluateDirtyState();

    /// <summary>
    /// Recomputes dirty state after the port text changes.
    /// </summary>
    partial void OnPortTextChanged(string value) => EvaluateDirtyState();

    /// <summary>
    /// Recomputes dirty state after the refresh interval text changes.
    /// </summary>
    partial void OnRefreshIntervalTextChanged(string value) => EvaluateDirtyState();

    /// <summary>
    /// Recomputes dirty state after the startup setting changes.
    /// </summary>
    partial void OnStartWithWindowsChanged(bool value) => EvaluateDirtyState();

    /// <summary>
    /// Recomputes dirty state after the window width text changes.
    /// </summary>
    partial void OnWindowWidthTextChanged(string value) => EvaluateDirtyState();

    /// <summary>
    /// Recomputes dirty state after the window height text changes.
    /// </summary>
    partial void OnWindowHeightTextChanged(string value) => EvaluateDirtyState();

    /// <summary>
    /// Recomputes dirty state after the plugin reset-time setting changes.
    /// </summary>
    partial void OnShowResetTimeInPluginsChanged(bool value) => EvaluateDirtyState();

    /// <summary>
    /// Recomputes dirty state after the absolute reset-time setting changes.
    /// </summary>
    partial void OnUseAbsoluteResetTimeChanged(bool value) => EvaluateDirtyState();

    /// <summary>
    /// Captures the current editable values as the saved snapshot baseline.
    /// </summary>
    private void CaptureSnapshot(SettingsStatus baseline)
    {
        m_SnapshotLiteMonitorDir = LiteMonitorDir;
        m_SnapshotTrafficMonitorDir = TrafficMonitorDir;
        m_SnapshotPortText = PortText;
        m_SnapshotRefreshIntervalText = RefreshIntervalText;
        m_SnapshotThemeMode = m_ThemeMode;
        m_SnapshotTokenUnit = m_TokenUnit;
        m_SnapshotVisiblePages = m_VisiblePages;
        m_SnapshotStartWithWindows = StartWithWindows;
        m_SnapshotMicaEnabled = m_MicaEnabled;
        m_SnapshotWindowWidthText = WindowWidthText;
        m_SnapshotWindowHeightText = WindowHeightText;
        m_SnapshotShowResetTimeInPlugins = ShowResetTimeInPlugins;
        m_SnapshotUseAbsoluteResetTime = UseAbsoluteResetTime;
        m_SnapshotHideInvalidProgressBars = m_HideInvalidProgressBars;
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
            LiteMonitorDir == m_SnapshotLiteMonitorDir &&
            TrafficMonitorDir == m_SnapshotTrafficMonitorDir &&
            PortText == m_SnapshotPortText &&
            RefreshIntervalText == m_SnapshotRefreshIntervalText &&
            m_ThemeMode == m_SnapshotThemeMode &&
            m_TokenUnit == m_SnapshotTokenUnit &&
            m_VisiblePages == m_SnapshotVisiblePages &&
            StartWithWindows == m_SnapshotStartWithWindows &&
            m_MicaEnabled == m_SnapshotMicaEnabled &&
            WindowWidthText == m_SnapshotWindowWidthText &&
            WindowHeightText == m_SnapshotWindowHeightText &&
            ShowResetTimeInPlugins == m_SnapshotShowResetTimeInPlugins &&
            UseAbsoluteResetTime == m_SnapshotUseAbsoluteResetTime &&
            m_HideInvalidProgressBars == m_SnapshotHideInvalidProgressBars;

        SettingsStatus = matchesSnapshot ? m_SettingsBaseline : SettingsStatus.Unsaved;
    }

    /// <summary>
    /// Applies editable properties to the shared settings model.
    /// </summary>
    public void ApplySettings()
    {
        int port = ClampOrDefault(PortText, CodexTrayDefaults.MinimumPort, CodexTrayDefaults.MaximumPort, CodexTrayDefaults.Port);
        int refreshInterval = ClampOrDefault(
            RefreshIntervalText,
            CodexTrayDefaults.MinimumRefreshIntervalMinutes,
            CodexTrayDefaults.MaximumRefreshIntervalMinutes,
            CodexTrayDefaults.RefreshIntervalMinutes);
        int windowWidth = ClampOrDefault(
            WindowWidthText,
            CodexTrayDefaults.MinimumWindowWidth,
            CodexTrayDefaults.MaximumWindowWidth,
            CodexTrayDefaults.WindowWidth);
        int windowHeight = ClampOrDefault(
            WindowHeightText,
            CodexTrayDefaults.MinimumWindowHeight,
            CodexTrayDefaults.MaximumWindowHeight,
            CodexTrayDefaults.WindowHeight);
        m_SuppressDirtyTracking = true;
        try
        {
            PortText = port.ToString(CultureInfo.InvariantCulture);
            RefreshIntervalText = refreshInterval.ToString(CultureInfo.InvariantCulture);
            WindowWidthText = windowWidth.ToString(CultureInfo.InvariantCulture);
            WindowHeightText = windowHeight.ToString(CultureInfo.InvariantCulture);
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
        m_Settings.TokenUnit = TokenUnit;
        m_Settings.VisiblePages = VisiblePages;
        m_Settings.MicaEnabled = MicaEnabled;
        m_Settings.WindowWidth = windowWidth;
        m_Settings.WindowHeight = windowHeight;
        m_Settings.ShowResetTimeInPlugins = ShowResetTimeInPlugins;
        m_Settings.UseAbsoluteResetTime = UseAbsoluteResetTime;
        m_Settings.HideInvalidProgressBars = HideInvalidProgressBars;
        CaptureSnapshot(SettingsStatus.Saved);
    }

    /// <summary>
    /// Creates the compact token-cost rows shown on usage dashboards.
    /// </summary>
    private static IReadOnlyList<TokenCostRowViewModel> CreateCompactTokenCostRows()
    {
        return
        [
            new TokenCostRowViewModel("Today"),
            new TokenCostRowViewModel("7D"),
            new TokenCostRowViewModel("30D"),
            new TokenCostRowViewModel("Lifetime", isLast: true),
        ];
    }

    /// <summary>
    /// Adds or removes one page from the current selection.
    /// </summary>
    private void SetPageItem(PageItem item, bool isShown)
    {
        VisiblePages = isShown
            ? m_VisiblePages | item
            : m_VisiblePages & ~item;
    }

    /// <summary>
    /// Parses and clamps raw input, falling back to a default when invalid.
    /// </summary>
    private static int ClampOrDefault(string rawText, int minimum, int maximum, int fallback)
    {
        if (!int.TryParse(rawText, out int value))
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
            ? $"Service: {(isRunning ? "Running" : "Stopped")} on {CodexTrayDefaults.Host}:{port}"
            : $"Service: Error on {CodexTrayDefaults.Host}:{port} - {error}";

        SourceDisplay = $"Source: {FormatSource(response)}";

        if (response == null)
        {
            PlanDisplay = "UNKNOWN";
            PlanBadgeBrush = s_PlanBadgeInactiveBrush;
            StatusDotBrush = s_RedBrush;
            UpdatedAtDisplay = FormatUpdatedAt(null);
            FiveHourQuota.UpdateUnavailable();
            SevenDayQuota.UpdateUnavailable();
            SparkFiveHourQuota.UpdateUnavailable();
            SparkSevenDayQuota.UpdateUnavailable();
            UpdateResetCredits(null);
            NotifyUsageSectionVisibilityChanged();
            return;
        }

        if (!response.Available)
        {
            PlanDisplay = "UNKNOWN";
            PlanBadgeBrush = s_PlanBadgeInactiveBrush;
            StatusDotBrush = s_RedBrush;
            UpdatedAtDisplay = $"Error{FormatResponseError(response)}";
            FiveHourQuota.UpdateUnavailable();
            SevenDayQuota.UpdateUnavailable();
            SparkFiveHourQuota.UpdateUnavailable();
            SparkSevenDayQuota.UpdateUnavailable();
            UpdateResetCredits(null);
            NotifyUsageSectionVisibilityChanged();
            return;
        }

        PlanDisplay = FormatPlan(response.PlanType);
        PlanBadgeBrush = s_PlanBadgeActiveBrush;
        StatusDotBrush = s_GreenBrush;
        UpdatedAtDisplay = FormatUpdatedAt(response.UpdatedAt);
        FiveHourQuota.Update(response.Limits.FiveHour, HideInvalidProgressBars);
        SevenDayQuota.Update(response.Limits.SevenDay, HideInvalidProgressBars);
        SparkFiveHourQuota.Update(response.Limits.SparkFiveHour, HideInvalidProgressBars);
        SparkSevenDayQuota.Update(response.Limits.SparkSevenDay, HideInvalidProgressBars);
        UpdateResetCredits(response.ResetCredits);
        NotifyUsageSectionVisibilityChanged();
    }

    /// <summary>
    /// Notifies listeners that Usage section header visibility may have changed.
    /// </summary>
    private void NotifyUsageSectionVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsUsageSectionHeaderVisible));
        OnPropertyChanged(nameof(IsSparkQuotaSectionVisible));
    }

    /// <summary>
    /// Updates the reset credit display values.
    /// </summary>
    private void UpdateResetCredits(ResetCredits? resetCredits)
    {
        if (resetCredits?.Available == true)
        {
            HasResetCredits = resetCredits.AvailableCount > 0;
            ResetCreditsDisplay = $"{resetCredits.AvailableCount} Available";
            string nearestExpiry = resetCredits.NearestExpiryLocal.Length >= 10
                ? resetCredits.NearestExpiryLocal[5..10]
                : resetCredits.NearestExpiryLocal;
            ResetCreditsExpiryDates = resetCredits.AvailableCount > 0
                ? string.Join(" · ", new[] { nearestExpiry, resetCredits.OtherExpiriesLocal }.Where(value => !string.IsNullOrWhiteSpace(value)))
                : string.Empty;
        }
        else
        {
            HasResetCredits = false;
            ResetCreditsDisplay = "N/A";
            ResetCreditsExpiryDates = "unknown";
        }
    }

    /// <summary>
    /// Updates token and cost displays or marks them unavailable after a failed read.
    /// </summary>
    public void UpdateTokenCost(TokenCostStatistics? statistics)
    {
        UpdateTokenCostRows(CodexTokenCostRows, statistics);
        CodexTokenCostChartDays = CreateTokenCostChartDays(statistics);
    }

    /// <summary>
    /// Updates the Cursor page from one shared dashboard collection result.
    /// </summary>
    public void UpdateCursorDashboard(CursorUsageDashboard dashboard)
    {
        CursorUsageSnapshot? usage = dashboard.Usage;
        bool usageAvailable = usage != null;
        bool tokenCostAvailable = dashboard.TokenCost != null;
        if (usage != null)
        {
            CursorPlanDisplay = FormatCursorPlan(usage.PlanType);
            CursorPlanBadgeBrush = CursorPlanDisplay == "UNKNOWN" ? s_PlanBadgeInactiveBrush : s_PlanBadgeActiveBrush;
            string reset = m_Settings.UseAbsoluteResetTime
                ? CodexTrayCollector.FormatSevenDayResetDate(usage.ResetsAt, dashboard.UpdatedAt)
                : CodexTrayCollector.FormatSevenDayResetLabel(usage.ResetsAt, dashboard.UpdatedAt);
            CursorTotalQuota.UpdateCursorUsage(usage.TotalUsedPercent, reset, showReset: true);
            CursorAutoQuota.UpdateCursorUsage(usage.AutoUsedPercent, string.Empty, showReset: false);
            CursorApiQuota.UpdateCursorUsage(usage.ApiUsedPercent, string.Empty, showReset: false);
        }
        else
        {
            CursorPlanDisplay = "UNKNOWN";
            CursorPlanBadgeBrush = s_PlanBadgeInactiveBrush;
            CursorTotalQuota.UpdateUnavailable(showReset: true, unavailableResetText: "N/A");
            CursorAutoQuota.UpdateUnavailable(showReset: false, unavailableResetText: "N/A");
            CursorApiQuota.UpdateUnavailable(showReset: false, unavailableResetText: "N/A");
        }

        UpdateTokenCostRows(CursorTokenCostRows, dashboard.TokenCost);
        CursorTokenCostChartDays = CreateTokenCostChartDays(dashboard.TokenCost);
        CursorStatusDotBrush = usageAvailable && tokenCostAvailable
            ? s_GreenBrush
            : usageAvailable || tokenCostAvailable
                ? s_YellowBrush
                : s_RedBrush;
        CursorUpdatedAtDisplay = usageAvailable && tokenCostAvailable
            ? FormatUpdatedAt(dashboard.UpdatedAt.ToString("O", CultureInfo.InvariantCulture))
            : usageAvailable
                ? "Usage updated, Token Cost N/A"
                : tokenCostAvailable
                    ? "Token Cost updated, Usage N/A"
                    : "Update error";
        CursorStatusTooltip = FormatCursorStatusTooltip(dashboard, usageAvailable, tokenCostAvailable);
    }

    /// <summary>
    /// Updates one fixed token-cost row group from the supplied statistics.
    /// </summary>
    private void UpdateTokenCostRows(IReadOnlyList<TokenCostRowViewModel> rows, TokenCostStatistics? statistics)
    {
        if (statistics == null)
        {
            foreach (TokenCostRowViewModel row in rows)
            {
                row.Display = s_UnavailableTokenCostDisplay;
            }

            return;
        }

        TokenCostSummary[] summaries = [statistics.Today, statistics.LastSevenDays, statistics.LastThirtyDays, statistics.Lifetime];
        for (int index = 0; index < rows.Count; index++)
        {
            rows[index].Display = FormatTokenCost(summaries[index]);
        }
    }

    /// <summary>
    /// Creates the rolling seven-day chart with today in the final slot.
    /// </summary>
    private IReadOnlyList<TokenCostChartDay> CreateTokenCostChartDays(TokenCostStatistics? statistics)
    {
        if (statistics == null || statistics.LastSevenDaysDaily.Count == 0)
        {
            List<TokenCostChartDay> unavailableDays = new(7);
            for (int offset = -6; offset <= 0; offset++)
            {
                DateTime date = DateTime.Today.AddDays(offset);
                string tooltip = $"{date:yyyy-MM-dd}{Environment.NewLine}Tokens: N/A{Environment.NewLine}Cost: N/A";
                unavailableDays.Add(new TokenCostChartDay(date.ToString("ddd", CultureInfo.InvariantCulture)[..1], 0, tooltip));
            }

            return unavailableDays;
        }

        decimal maximumCost = statistics.LastSevenDaysDaily.Max(day => day.Summary.CostUsd ?? 0);
        List<TokenCostChartDay> days = new(statistics.LastSevenDaysDaily.Count);
        foreach (TokenCostDailySummary day in statistics.LastSevenDaysDaily)
        {
            decimal cost = day.Summary.CostUsd ?? 0;
            double height = maximumCost <= 0 || cost <= 0
                ? 0
                : Math.Max(2, (double)(cost / maximumCost) * k_TokenCostChartMaximumBarHeight);
            string costText = day.Summary.CostUsd?.ToString("$0.00", CultureInfo.InvariantCulture) ?? "N/A";
            string tokensText = AppSettings.FormatTokenCount(day.Summary.TotalTokens, m_Settings.TokenUnit);
            string tooltip = $"{day.Date:yyyy-MM-dd}{Environment.NewLine}Tokens: {tokensText}{Environment.NewLine}Cost: {costText}";
            days.Add(new TokenCostChartDay(day.Date.ToString("ddd", CultureInfo.InvariantCulture)[..1], height, tooltip));
        }

        return days;
    }

    /// <summary>
    /// Combines sanitized Cursor endpoint errors for the status tooltip.
    /// </summary>
    private static string FormatCursorStatusTooltip(CursorUsageDashboard dashboard, bool usageAvailable, bool tokenCostAvailable)
    {
        List<string> errors = [];
        if (!usageAvailable && dashboard.UsageError.Length > 0)
        {
            errors.Add($"Usage: {dashboard.UsageError}");
        }

        if (!tokenCostAvailable && dashboard.TokenCostError.Length > 0)
        {
            errors.Add($"Token Cost: {dashboard.TokenCostError}");
        }

        return string.Join(Environment.NewLine, errors);
    }

    /// <summary>
    /// Formats one token cost period for display.
    /// </summary>
    private TokenCostDisplay FormatTokenCost(TokenCostSummary summary)
    {
        string cost = summary.CostUsd?.ToString("$0.00", CultureInfo.InvariantCulture) ?? "N/A";
        return new TokenCostDisplay(cost, AppSettings.FormatTokenCount(summary.TotalTokens, m_Settings.TokenUnit));
    }

    /// <summary>
    /// Shows the Codex dashboard page inside the tray popup.
    /// </summary>
    [RelayCommand]
    public void ShowCodex()
    {
        SetPage(k_CodexPageName);
    }

    /// <summary>
    /// Shows the Cursor dashboard page inside the tray popup.
    /// </summary>
    [RelayCommand]
    public void ShowCursor()
    {
        SetPage(k_CursorPageName);
    }

    /// <summary>
    /// Shows the API monitoring page inside the tray popup.
    /// </summary>
    [RelayCommand]
    public void ShowApi()
    {
        SetPage(k_ApiPageName);
    }

    /// <summary>
    /// Shows the settings page inside the tray popup.
    /// </summary>
    [RelayCommand]
    public void ShowSettings()
    {
        if (!IsAboutVisible)
        {
            LoadSettings(m_Settings);
        }

        SetPage(k_SettingsPageName);
    }

    /// <summary>
    /// Shows the about page inside the tray popup.
    /// </summary>
    [RelayCommand]
    public void ShowAbout()
    {
        SetPage(k_AboutPageName);
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
        OnPropertyChanged(nameof(IsCodexVisible));
        OnPropertyChanged(nameof(IsCursorVisible));
        OnPropertyChanged(nameof(IsApiVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(IsAboutVisible));
        OnPropertyChanged(nameof(IsCodexSelected));
        OnPropertyChanged(nameof(IsCursorSelected));
        OnPropertyChanged(nameof(IsApiSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
    }

    /// <summary>
    /// Opens an external URL in the default browser.
    /// </summary>
    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            InAppDialogRequested?.Invoke(new InAppDialogRequest("Unable to open link", exception.Message, "OK"));
        }
    }

    /// <summary>
    /// Loads persisted API monitor cards.
    /// </summary>
    private void LoadApiMonitors(IEnumerable<ApiMonitorSettings> settings)
    {
        ApiMonitors.Clear();
        foreach (ApiMonitorSettings monitorSettings in settings)
        {
            ApiMonitorViewModel monitor = new(monitorSettings);
            monitor.EditingSaved += HandleApiMonitorSaved;
            ApiMonitors.Add(monitor);
        }

        NotifyApiMonitorCountChanged();
    }

    /// <summary>
    /// Adds a default DeepSeek API monitor card.
    /// </summary>
    [RelayCommand]
    private void AddApiMonitor()
    {
        ApiMonitorViewModel monitor = new(new ApiMonitorSettings(), isEditing: true, isPending: true);
        monitor.EditingSaved += HandleApiMonitorSaved;
        ApiMonitors.Add(monitor);
        NotifyApiMonitorCountChanged();
    }

    /// <summary>
    /// Requests confirmation before removing an API monitor card.
    /// </summary>
    [RelayCommand]
    private void RemoveApiMonitor(ApiMonitorViewModel? monitor)
    {
        if (monitor == null)
        {
            return;
        }

        InAppDialogRequested?.Invoke(new InAppDialogRequest(
            "Delete API monitor?",
            $"Delete the {monitor.Name} API monitor?",
            "Delete",
            "Cancel",
            () => DeleteApiMonitor(monitor)));
    }

    /// <summary>
    /// Removes a confirmed API monitor card.
    /// </summary>
    private void DeleteApiMonitor(ApiMonitorViewModel monitor)
    {
        bool wasPending = monitor.IsPending;
        monitor.EditingSaved -= HandleApiMonitorSaved;
        ApiMonitors.Remove(monitor);
        SaveApiMonitors();
        if (!wasPending)
        {
            RequestRefresh();
        }
    }

    /// <summary>
    /// Returns whether an API monitor can move up.
    /// </summary>
    private bool CanMoveApiMonitorUp(ApiMonitorViewModel? monitor)
    {
        return monitor != null && ApiMonitors.IndexOf(monitor) > 0;
    }

    /// <summary>
    /// Moves an API monitor card up by one position.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveApiMonitorUp))]
    private void MoveApiMonitorUp(ApiMonitorViewModel? monitor)
    {
        MoveApiMonitor(monitor, -1);
    }

    /// <summary>
    /// Returns whether an API monitor can move down.
    /// </summary>
    private bool CanMoveApiMonitorDown(ApiMonitorViewModel? monitor)
    {
        int index = monitor == null ? -1 : ApiMonitors.IndexOf(monitor);
        return index >= 0 && index < ApiMonitors.Count - 1;
    }

    /// <summary>
    /// Moves an API monitor card down by one position.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveApiMonitorDown))]
    private void MoveApiMonitorDown(ApiMonitorViewModel? monitor)
    {
        MoveApiMonitor(monitor, 1);
    }

    /// <summary>
    /// Moves an API monitor card by one position.
    /// </summary>
    private void MoveApiMonitor(ApiMonitorViewModel? monitor, int offset)
    {
        if (monitor == null)
        {
            return;
        }

        int oldIndex = ApiMonitors.IndexOf(monitor);
        int newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= ApiMonitors.Count)
        {
            return;
        }

        ApiMonitors.Move(oldIndex, newIndex);
        SaveApiMonitors();
    }

    /// <summary>
    /// Persists a confirmed API monitor and refreshes its usage.
    /// </summary>
    private void HandleApiMonitorSaved(object? sender, EventArgs args)
    {
        SaveApiMonitors();
        RequestRefresh();
    }

    /// <summary>
    /// Starts a refresh when the asynchronous command is available.
    /// </summary>
    private void RequestRefresh()
    {
        if (RefreshCommand.CanExecute(null))
        {
            RefreshCommand.Execute(null);
        }
    }

    /// <summary>
    /// Copies API monitor cards into application settings and requests persistence.
    /// </summary>
    private void SaveApiMonitors()
    {
        m_Settings.ApiMonitors = ApiMonitors.Where(monitor => !monitor.IsPending).Select(monitor => monitor.ToSettings()).ToList();
        NotifyApiMonitorCountChanged();
        ApiMonitorsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Updates API monitor count properties after a collection change.
    /// </summary>
    private void NotifyApiMonitorCountChanged()
    {
        OnPropertyChanged(nameof(IsApiEmpty));
        NotifyApiMonitorStatusChanged();
        MoveApiMonitorUpCommand.NotifyCanExecuteChanged();
        MoveApiMonitorDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Clears the API refresh summary when no complete result is available.
    /// </summary>
    private void ResetApiMonitorStatus()
    {
        m_ApiUsageStatus = null;
        m_ApiUsageErrorCount = 0;
        m_ApiUsageMonitorCount = 0;
        m_ApiUsageUpdatedAt = null;
        NotifyApiMonitorStatusChanged();
    }

    /// <summary>
    /// Raises change notifications for the API refresh summary.
    /// </summary>
    private void NotifyApiMonitorStatusChanged()
    {
        OnPropertyChanged(nameof(ApiMonitorStatusText));
        OnPropertyChanged(nameof(ApiMonitorStatusDotBrush));
        OnPropertyChanged(nameof(HasApiMonitorStatus));
    }

    /// <summary>
    /// Applies query results to matching API monitor cards.
    /// </summary>
    public void UpdateApiUsage(IReadOnlyList<ApiUsageResult> results)
    {
        Dictionary<string, ApiUsageResult> byId = results.ToDictionary(result => result.MonitorId, StringComparer.Ordinal);
        List<ApiUsageResult> monitorResults = [];
        foreach (ApiMonitorViewModel monitor in ApiMonitors)
        {
            if (monitor.IsPending)
            {
                continue;
            }

            if (byId.TryGetValue(monitor.Id, out ApiUsageResult? result))
            {
                monitor.Update(result);
                monitorResults.Add(result);
            }
        }

        if (monitorResults.Count != MonitoredApiCount || monitorResults.Count == 0)
        {
            ResetApiMonitorStatus();
            return;
        }

        ApiUsageSummary summary = ApiUsageCollector.Summarize(monitorResults);
        m_ApiUsageStatus = summary.Status;
        m_ApiUsageErrorCount = summary.ErrorCount;
        m_ApiUsageMonitorCount = monitorResults.Count;
        m_ApiUsageUpdatedAt = summary.LatestAvailableUpdatedAt;
        NotifyApiMonitorStatusChanged();
    }

    /// <summary>
    /// Opens an in-window dialog requested by the tray controller.
    /// </summary>
    public void ShowInAppDialog(InAppDialogRequest request)
    {
        InAppDialogTitle = request.Title;
        InAppDialogMessage = request.Message;
        InAppDialogPrimaryButtonText = request.PrimaryButtonText;
        InAppDialogSecondaryButtonText = request.SecondaryButtonText ?? string.Empty;
        m_InAppDialogPrimaryAction = request.PrimaryAction;
        IsInAppDialogOpen = true;
    }

    /// <summary>
    /// Confirms the active in-window dialog.
    /// </summary>
    [RelayCommand]
    private void ConfirmInAppDialog()
    {
        Action? primaryAction = m_InAppDialogPrimaryAction;
        DismissInAppDialog();
        primaryAction?.Invoke();
    }

    /// <summary>
    /// Dismisses the active in-window dialog.
    /// </summary>
    [RelayCommand]
    public void DismissInAppDialog()
    {
        if (!m_IsInAppDialogOpen)
        {
            return;
        }

        m_InAppDialogPrimaryAction = null;
        IsInAppDialogOpen = false;
    }

    /// <summary>
    /// Opens a shared folder picker and updates a path property.
    /// </summary>
    private void BrowseMonitorFolder(string description, string currentPath, Action<string> updatePath)
    {
        SetNativeModalOpen(true);
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
            SetNativeModalOpen(false);
        }
    }

    /// <summary>
    /// Updates modal state while a native folder picker is open.
    /// </summary>
    private void SetNativeModalOpen(bool isOpen)
    {
        if (m_IsNativeModalOpen == isOpen)
        {
            return;
        }

        m_IsNativeModalOpen = isOpen;
        OnPropertyChanged(nameof(IsModalOpen));
    }

    /// <summary>
    /// Auto detects the LiteMonitor directory off the UI thread.
    /// </summary>
    public async Task DetectLiteMonitorAsync(bool showNotFound, CancellationToken cancellationToken = default)
    {
        if (IsDetectingLiteMonitor)
        {
            return;
        }

        IsDetectingLiteMonitor = true;
        try
        {
            // Manual detect always runs a full scan instead of short-circuiting on the current path.
            string detected = await Task.Run(() => LiteMonitorLocator.AutoDetect(cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(detected))
            {
                LiteMonitorDir = CodexTrayDefaults.PluginPathNone;
                if (showNotFound)
                {
                    InAppDialogRequested?.Invoke(new InAppDialogRequest("LiteMonitor not found", "LiteMonitor was not found.", "OK"));
                }

                return;
            }

            LiteMonitorDir = detected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            IsDetectingLiteMonitor = false;
        }
    }

    /// <summary>
    /// Auto detects the TrafficMonitor directory off the UI thread.
    /// </summary>
    public async Task DetectTrafficMonitorAsync(bool showNotFound, CancellationToken cancellationToken = default)
    {
        if (IsDetectingTrafficMonitor)
        {
            return;
        }

        IsDetectingTrafficMonitor = true;
        try
        {
            // Manual detect always runs a full scan instead of short-circuiting on the current path.
            string detected = await Task.Run(() => TrafficMonitorLocator.AutoDetect(cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(detected))
            {
                TrafficMonitorDir = CodexTrayDefaults.PluginPathNone;
                if (showNotFound)
                {
                    InAppDialogRequested?.Invoke(new InAppDialogRequest("TrafficMonitor not found", "TrafficMonitor was not found.", "OK"));
                }

                return;
            }

            TrafficMonitorDir = detected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
        return $"Path: {(value.Length == 0 ? CodexTrayDefaults.PluginPathNone : value)}";
    }

    /// <summary>
    /// Returns the configured directory, treating the None sentinel as empty.
    /// </summary>
    public static string PluginPathOrEmpty(string? directory)
    {
        string value = (directory ?? string.Empty).Trim();
        return string.Equals(value, CodexTrayDefaults.PluginPathNone, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : value;
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
        string normalized = (planType ?? string.Empty).Trim().Replace("-", "_", StringComparison.Ordinal).Replace(" ", "_", StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "free" => "FREE",
            "go" => "GO",
            "plus" => "PLUS",
            "pro_lite" or "prolite" or "pro_5x" or "pro5x" => "PRO",
            "pro" or "pro_20x" or "pro20x" => "PRO MAX",
            "chatgpt" => "PLUS",
            _ => "UNKNOWN",
        };
    }

    /// <summary>
    /// Formats a Cursor membership type as a compact subscription label.
    /// </summary>
    private static string FormatCursorPlan(string? planType)
    {
        string normalized = (planType ?? string.Empty).Trim().Replace("-", "_", StringComparison.Ordinal).Replace(" ", "_", StringComparison.Ordinal);
        return string.IsNullOrEmpty(normalized)
            ? "UNKNOWN"
            : normalized.Replace("_plus", "+", StringComparison.OrdinalIgnoreCase).Replace("_", " ", StringComparison.Ordinal).ToUpperInvariant();
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

    internal sealed partial class TokenCostRowViewModel : ObservableObject
    {
        public string Title { get; }

        [ObservableProperty]
        public partial TokenCostDisplay Display { get; internal set; } = s_UnavailableTokenCostDisplay;

        public bool IsLast { get; }

        /// <summary>
        /// Creates one fixed token-cost display row.
        /// </summary>
        public TokenCostRowViewModel(string title, bool isLast = false)
        {
            Title = title;
            IsLast = isLast;
        }
    }

    internal sealed partial class QuotaViewModel : ObservableObject
    {
        public string Title { get; }

        [ObservableProperty]
        public partial int RemainingPercent { get; private set; }

        [ObservableProperty]
        public partial string PercentText { get; private set; } = "N/A";

        [ObservableProperty]
        public partial string ResetText { get; private set; } = "unknown";

        [ObservableProperty]
        public partial Media.Brush AccentBrush { get; private set; } = s_RedBrush;

        [ObservableProperty]
        public partial bool IsVisible { get; private set; } = true;

        [ObservableProperty]
        public partial bool IsResetVisible { get; private set; } = true;

        /// <summary>
        /// Creates a quota display model.
        /// </summary>
        public QuotaViewModel(string title)
        {
            Title = title;
        }

        /// <summary>
        /// Updates the quota display from a usage limit.
        /// </summary>
        public void Update(UsageLimit limit, bool hideInvalidProgressBars)
        {
            if (limit.WindowMinutes <= 0)
            {
                if (hideInvalidProgressBars)
                {
                    // Successful response with an inactive window: hide this quota row.
                    IsVisible = false;
                    return;
                }

                UpdateUnavailable(showReset: false);
                return;
            }

            UpdateRemaining(limit.RemainingPercent, limit.ResetLabel, showReset: true);
        }

        /// <summary>
        /// Updates the quota display from Cursor used-percent data.
        /// </summary>
        public void UpdateCursorUsage(double usedPercent, string resetText, bool showReset)
        {
            int remaining = (int)Math.Round(Math.Clamp(100 - usedPercent, 0, 100), MidpointRounding.AwayFromZero);
            UpdateRemaining(remaining, resetText, showReset);
        }

        /// <summary>
        /// Updates the quota display for a failed or unauthorized response.
        /// </summary>
        public void UpdateUnavailable(bool showReset = true, string unavailableResetText = "unknown")
        {
            RemainingPercent = 0;
            PercentText = "N/A";
            ResetText = unavailableResetText;
            AccentBrush = s_RedBrush;
            IsVisible = true;
            IsResetVisible = showReset;
        }

        /// <summary>
        /// Applies one remaining percentage and optional reset label to the quota display.
        /// </summary>
        private void UpdateRemaining(int remainingPercent, string resetText, bool showReset)
        {
            int remaining = Math.Max(0, Math.Min(100, remainingPercent));
            RemainingPercent = remaining;
            PercentText = $"{remaining}%";
            ResetText = string.IsNullOrWhiteSpace(resetText) ? "unknown" : resetText;
            AccentBrush = GetAccentBrush(remaining);
            IsVisible = true;
            IsResetVisible = showReset;
        }

        /// <summary>
        /// Selects a quota color from the remaining percent.
        /// </summary>
        private static Media.Brush GetAccentBrush(int remainingPercent)
        {
            if (remainingPercent >= 50)
            {
                return s_GreenBrush;
            }

            return remainingPercent >= 20 ? s_YellowBrush : s_RedBrush;
        }

    }
}
