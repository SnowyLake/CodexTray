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

internal sealed record TokenCostChartDay(DateTime Date, string Label, double BarHeight, string Tooltip, bool IsInteractive);

internal sealed record TokenCostChartLabel(string Text, double Left);

internal sealed record TokenCostDonutSegment(string Label, string Share, Media.Brush Brush, Media.Geometry Geometry, string Tooltip);

internal sealed record TokenCostModelShare(string Label, long Tokens);

internal enum TokenCostPeriod
{
    Today,
    LastSevenDays,
    LastThirtyDays,
    CurrentWeek,
    CurrentMonth,
    Lifetime,
}

internal enum CodexTokenCostChartPeriod
{
    LastThirtyDays,
    CurrentMonth,
}

internal sealed record InAppDialogRequest(
    string Title,
    string Message,
    string PrimaryButtonText,
    string? SecondaryButtonText = null,
    Action? PrimaryAction = null);

internal sealed partial class TrayPopupViewModel : ObservableObject
{
    private const string k_CodexPageName = "Codex";
    private const string k_GrokPageName = "Grok";
    private const string k_CursorPageName = "Cursor";
    private const string k_ApiPageName = "API";
    private const string k_SettingsPageName = "Settings";
    private const string k_AboutPageName = "About";
    private const string k_RepositoryUrl = "https://github.com/SnowyLake/CodexTray";
    private const double k_CodexTokenCostChartMaximumBarHeight = 58;
    private const double k_CodexTokenCostChartWidth = 310;
    private const double k_CodexTokenCostChartHorizontalMargin = 10;
    private const double k_CodexTokenCostChartLabelWidth = 28;
    private const double k_TokenCostChartMaximumBarHeight = 96;
    private const double k_TokenCostDonutCenter = 64;
    private const double k_TokenCostDonutOuterRadius = 62;
    private const double k_TokenCostDonutInnerRadius = 49;

    private static readonly Media.Brush s_GreenBrush = CreateFrozenBrush(26, 188, 137);
    private static readonly Media.Brush s_YellowBrush = CreateFrozenBrush(226, 176, 54);
    private static readonly Media.Brush s_RedBrush = CreateFrozenBrush(224, 91, 77);
    private static readonly Media.Brush[] s_TokenCostDonutBrushes =
    [
        s_GreenBrush,
        CreateFrozenBrush(82, 193, 181),
        CreateFrozenBrush(92, 143, 202),
        CreateFrozenBrush(166, 173, 179),
    ];
    private static readonly string[] s_ModelEffortSuffixes = ["-minimal", "-low", "-medium", "-high", "-xhigh"];
    private static readonly Media.Brush s_PlanBadgeActiveBrush = s_GreenBrush;
    private static readonly Media.Brush s_PlanBadgeInactiveBrush = CreateFrozenBrush(107, 122, 117);
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
    private TokenCostStatistics? m_CodexTokenCostStatistics;
    private CodexTokenCostChartPeriod m_CodexTokenCostChartPeriod;

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
    private bool m_SnapshotShowResetTimeInPlugins = CodexTrayDefaults.ShowResetTimeInPlugins;
    private bool m_SnapshotUseAbsoluteResetTime = CodexTrayDefaults.UseAbsoluteResetTime;
    private bool m_SnapshotHideInvalidProgressBars = CodexTrayDefaults.HideInvalidProgressBars;

    public event EventHandler? SaveSettingsRequested;

    public event EventHandler? ApiMonitorsChanged;

    public event EventHandler? InstallLiteMonitorPluginRequested;

    public event EventHandler? InstallTrafficMonitorPluginRequested;

    public event Action<InAppDialogRequest>? InAppDialogRequested;

    public QuotaViewModel SessionQuota { get; } = new("Session");

    public QuotaViewModel WeeklyQuota { get; } = new("Weekly");

    public QuotaViewModel GrokWeeklyQuota { get; } = new("Weekly");

    public QuotaViewModel CursorMonthlyQuota { get; } = new("Monthly");

    public QuotaViewModel CursorAutoQuota { get; } = new("First party");

    public QuotaViewModel CursorApiQuota { get; } = new("APIs");

    public IReadOnlyList<TokenCostRowViewModel> CodexTokenCostRows { get; } = CreateCompactTokenCostRows(selectToday: true);

    public IReadOnlyList<TokenCostRowViewModel> GrokTokenCostRows { get; } = CreateCompactTokenCostRows();

    public IReadOnlyList<TokenCostRowViewModel> CursorTokenCostRows { get; } = CreateCompactTokenCostRows();

    [ObservableProperty]
    public partial IReadOnlyList<TokenCostChartDay> CodexTokenCostChartDays { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TokenCostChartLabel> CodexTokenCostChartLabels { get; private set; } = [];

    [ObservableProperty]
    public partial int CodexTokenCostChartColumnCount { get; private set; } = 30;

    [ObservableProperty]
    public partial IReadOnlyList<TokenCostChartDay> GrokTokenCostChartDays { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TokenCostChartDay> CursorTokenCostChartDays { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TokenCostDonutSegment> CodexTokenCostDonutSegments { get; private set; } = [];

    [ObservableProperty]
    public partial string CodexSelectedTokenDisplay { get; private set; } = "N/A";

    [ObservableProperty]
    public partial string CodexSelectedCostDisplay { get; private set; } = "N/A";

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
                OnPropertyChanged(nameof(IsGrokTabVisible));
                OnPropertyChanged(nameof(IsCursorTabVisible));
                OnPropertyChanged(nameof(IsApiTabVisible));
                OnPropertyChanged(nameof(ShowCodexPage));
                OnPropertyChanged(nameof(ShowGrokPage));
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

    public bool ShowGrokPage
    {
        get => (m_VisiblePages & PageItem.Grok) != 0;
        set => SetPageItem(PageItem.Grok, value);
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
        !m_HideInvalidProgressBars || SessionQuota.IsVisible || WeeklyQuota.IsVisible;

    public bool IsCodexVisible => m_CurrentPage == k_CodexPageName;

    public bool IsGrokVisible => m_CurrentPage == k_GrokPageName;

    public bool IsCursorVisible => m_CurrentPage == k_CursorPageName;

    public bool IsApiVisible => m_CurrentPage == k_ApiPageName;

    public bool IsSettingsVisible => m_CurrentPage == k_SettingsPageName;

    public bool IsAboutVisible => m_CurrentPage == k_AboutPageName;

    public bool IsCodexSelected => m_CurrentPage == k_CodexPageName;

    public bool IsGrokSelected => m_CurrentPage == k_GrokPageName;

    public bool IsCursorSelected => m_CurrentPage == k_CursorPageName;

    public bool IsApiSelected => m_CurrentPage == k_ApiPageName;

    public bool IsSettingsSelected => m_CurrentPage == k_SettingsPageName;

    public bool IsCodexTabVisible => (m_VisiblePages & PageItem.Codex) != 0;

    public bool IsGrokTabVisible => (m_VisiblePages & PageItem.Grok) != 0;

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
    public partial Media.Brush GrokStatusDotBrush { get; private set; } = s_RedBrush;

    [ObservableProperty]
    public partial string GrokPlanDisplay { get; private set; } = "UNKNOWN";

    [ObservableProperty]
    public partial Media.Brush GrokPlanBadgeBrush { get; private set; } = s_PlanBadgeInactiveBrush;

    [ObservableProperty]
    public partial string GrokUpdatedAtDisplay { get; private set; } = "Waiting for first refresh";

    [ObservableProperty]
    public partial string GrokStatusTooltip { get; private set; } = string.Empty;

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
            : (settings.VisiblePages & PageItem.Grok) != 0
                ? k_GrokPageName
                : (settings.VisiblePages & PageItem.Cursor) != 0
                    ? k_CursorPageName
                    : (settings.VisiblePages & PageItem.Apis) != 0
                        ? k_ApiPageName
                        : k_SettingsPageName;
        LoadSettings(settings);
        LoadApiMonitors(settings.ApiMonitors);
    }

    /// <summary>
    /// Creates an immutable brush that can be shared across UI threads.
    /// </summary>
    private static Media.Brush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        Media.SolidColorBrush brush = new(Media.Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
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
        m_Settings.TokenUnit = TokenUnit;
        m_Settings.VisiblePages = VisiblePages;
        m_Settings.MicaEnabled = MicaEnabled;
        m_Settings.ShowResetTimeInPlugins = ShowResetTimeInPlugins;
        m_Settings.UseAbsoluteResetTime = UseAbsoluteResetTime;
        m_Settings.HideInvalidProgressBars = HideInvalidProgressBars;
        CaptureSnapshot(SettingsStatus.Saved);
    }

    /// <summary>
    /// Creates the compact token-cost rows shown on usage dashboards.
    /// </summary>
    private static IReadOnlyList<TokenCostRowViewModel> CreateCompactTokenCostRows(bool selectToday = false)
    {
        return
        [
            new TokenCostRowViewModel("Today", TokenCostPeriod.Today, isSelected: selectToday),
            new TokenCostRowViewModel("7d", TokenCostPeriod.LastSevenDays),
            new TokenCostRowViewModel("30d", TokenCostPeriod.LastThirtyDays),
            new TokenCostRowViewModel("Lifetime", TokenCostPeriod.Lifetime, isLast: true),
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
            SessionQuota.UpdateUnavailable();
            WeeklyQuota.UpdateUnavailable();
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
            SessionQuota.UpdateUnavailable();
            WeeklyQuota.UpdateUnavailable();
            UpdateResetCredits(null);
            NotifyUsageSectionVisibilityChanged();
            return;
        }

        PlanDisplay = FormatPlan(response.PlanType);
        PlanBadgeBrush = s_PlanBadgeActiveBrush;
        StatusDotBrush = s_GreenBrush;
        UpdatedAtDisplay = FormatUpdatedAt(response.UpdatedAt);
        SessionQuota.Update(response.Limits.Session, HideInvalidProgressBars);
        WeeklyQuota.Update(response.Limits.Weekly, HideInvalidProgressBars);
        UpdateResetCredits(response.ResetCredits);
        NotifyUsageSectionVisibilityChanged();
    }

    /// <summary>
    /// Notifies listeners that Usage section header visibility may have changed.
    /// </summary>
    private void NotifyUsageSectionVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsUsageSectionHeaderVisible));
    }

    /// <summary>
    /// Updates the reset credit display values.
    /// </summary>
    private void UpdateResetCredits(ResetCredits? resetCredits)
    {
        if (resetCredits?.Available == true)
        {
            HasResetCredits = resetCredits.AvailableCount > 0;
            ResetCreditsDisplay = $"{resetCredits.AvailableCount} available";
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
        m_CodexTokenCostStatistics = statistics;
        UpdateTokenCostRows(CodexTokenCostRows, statistics);
        UpdateCodexTokenCostChart();
        UpdateCodexTokenCostDonut();
    }

    /// <summary>
    /// Updates the Grok page from one Grok Build billing result.
    /// </summary>
    public void UpdateGrokDashboard(GrokUsageDashboard dashboard, TokenCostStatistics? tokenCost = null)
    {
        bool usageAvailable = dashboard.Usage != null;
        bool tokenCostAvailable = tokenCost != null;
        if (dashboard.Usage is GrokUsageSnapshot usage)
        {
            GrokPlanDisplay = FormatGrokPlan(usage.SubscriptionTier);
            GrokPlanBadgeBrush = GrokPlanDisplay == "UNKNOWN" ? s_PlanBadgeInactiveBrush : s_PlanBadgeActiveBrush;
            string reset = m_Settings.UseAbsoluteResetTime
                ? CodexTrayCollector.FormatWeeklyResetDate(usage.ResetsAt, dashboard.UpdatedAt)
                : CodexTrayCollector.FormatWeeklyResetLabel(usage.ResetsAt, dashboard.UpdatedAt);
            GrokWeeklyQuota.UpdateUsedPercent(usage.UsedPercent, reset, showReset: true);
        }
        else
        {
            GrokPlanDisplay = "UNKNOWN";
            GrokPlanBadgeBrush = s_PlanBadgeInactiveBrush;
            GrokWeeklyQuota.UpdateUnavailable(showReset: true, unavailableResetText: "N/A");
        }

        UpdateTokenCostRows(GrokTokenCostRows, tokenCost);
        GrokTokenCostChartDays = CreateTokenCostChartDays(tokenCost);
        GrokStatusDotBrush = usageAvailable && tokenCostAvailable
            ? s_GreenBrush
            : usageAvailable || tokenCostAvailable
                ? s_YellowBrush
                : s_RedBrush;
        GrokUpdatedAtDisplay = usageAvailable && tokenCostAvailable
            ? FormatUpdatedAt(dashboard.UpdatedAt.ToString("O", CultureInfo.InvariantCulture))
            : usageAvailable
                ? "Usage updated, Token Cost N/A"
                : tokenCostAvailable
                    ? "Token Cost updated, Usage N/A"
                    : "Update error";
        GrokStatusTooltip = FormatGrokStatusTooltip(dashboard, usageAvailable, tokenCostAvailable);
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
                ? CodexTrayCollector.FormatWeeklyResetDate(usage.ResetsAt, dashboard.UpdatedAt)
                : CodexTrayCollector.FormatWeeklyResetLabel(usage.ResetsAt, dashboard.UpdatedAt);
            CursorMonthlyQuota.UpdateUsedPercent(usage.MonthlyUsedPercent, reset, showReset: true);
            CursorAutoQuota.UpdateUsedPercent(usage.AutoUsedPercent, string.Empty, showReset: false);
            CursorApiQuota.UpdateUsedPercent(usage.ApiUsedPercent, string.Empty, showReset: false);
        }
        else
        {
            CursorPlanDisplay = "UNKNOWN";
            CursorPlanBadgeBrush = s_PlanBadgeInactiveBrush;
            CursorMonthlyQuota.UpdateUnavailable(showReset: true, unavailableResetText: "N/A");
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

        foreach (TokenCostRowViewModel row in rows)
        {
            row.Display = FormatTokenCost(GetPeriodSummary(statistics, row.Period));
        }
    }

    /// <summary>
    /// Selects the Codex token-cost period used by the model distribution chart.
    /// </summary>
    [RelayCommand]
    private void SelectCodexTokenCostPeriod(TokenCostRowViewModel? row)
    {
        if (row == null || row.IsSelected)
        {
            return;
        }

        foreach (TokenCostRowViewModel candidate in CodexTokenCostRows)
        {
            candidate.IsSelected = ReferenceEquals(candidate, row);
        }

        UpdateCodexTokenCostDonut();
    }

    /// <summary>
    /// Updates the selected Codex token total, cost, and per-model donut segments.
    /// </summary>
    private void UpdateCodexTokenCostDonut()
    {
        TokenCostPeriod period = CodexTokenCostRows.FirstOrDefault(row => row.IsSelected)?.Period ?? TokenCostPeriod.Today;
        if (m_CodexTokenCostStatistics == null)
        {
            CodexSelectedTokenDisplay = "N/A";
            CodexSelectedCostDisplay = "N/A";
            CodexTokenCostDonutSegments = [];
            return;
        }

        TokenCostSummary summary = GetPeriodSummary(m_CodexTokenCostStatistics, period);
        CodexSelectedTokenDisplay = AppSettings.FormatTokenCount(summary.TotalTokens, m_Settings.TokenUnit);
        CodexSelectedCostDisplay = summary.CostUsd?.ToString("$0.00", CultureInfo.InvariantCulture) ?? "N/A";

        List<TokenCostModelShare> modelShares = m_CodexTokenCostStatistics.Models
            .Select(model => new TokenCostModelShare(FormatModelLabel(model.Model), GetPeriodSummary(model, period).TotalTokens))
            .Where(model => model.Tokens > 0)
            .GroupBy(model => model.Label, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TokenCostModelShare(group.Key, group.Sum(model => model.Tokens)))
            .OrderByDescending(model => model.Tokens)
            .ThenBy(model => model.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (modelShares.Count == 0)
        {
            CodexTokenCostDonutSegments = [];
            return;
        }

        int directModelCount = modelShares.Count > 4 ? 3 : Math.Min(4, modelShares.Count);
        List<TokenCostModelShare> displayedShares = modelShares.Take(directModelCount).ToList();
        long otherTokens = modelShares.Skip(directModelCount).Sum(model => model.Tokens);
        if (otherTokens > 0)
        {
            displayedShares.Add(new TokenCostModelShare("Other", otherTokens));
        }

        long totalTokens = displayedShares.Sum(model => model.Tokens);
        double startAngle = -90;
        List<TokenCostDonutSegment> segments = new(displayedShares.Count);
        for (int index = 0; index < displayedShares.Count; index++)
        {
            TokenCostModelShare model = displayedShares[index];
            double sweepAngle = index == displayedShares.Count - 1
                ? 270 - startAngle
                : model.Tokens / (double)totalTokens * 360;
            double share = model.Tokens / (double)totalTokens;
            string shareText = $"{Math.Round(share * 100):0}%";
            string tokensText = AppSettings.FormatTokenCount(model.Tokens, m_Settings.TokenUnit);
            string tooltip = $"{model.Label}{Environment.NewLine}Tokens: {tokensText}{Environment.NewLine}Share: {shareText}";
            segments.Add(new TokenCostDonutSegment(
                model.Label,
                shareText,
                s_TokenCostDonutBrushes[index],
                CreateDonutSegmentGeometry(startAngle, sweepAngle),
                tooltip));
            startAngle += sweepAngle;
        }

        CodexTokenCostDonutSegments = segments;
    }

    /// <summary>
    /// Returns the requested total token-cost period.
    /// </summary>
    private static TokenCostSummary GetPeriodSummary(TokenCostStatistics statistics, TokenCostPeriod period)
    {
        return period switch
        {
            TokenCostPeriod.Today => statistics.Today,
            TokenCostPeriod.LastSevenDays => statistics.LastSevenDays,
            TokenCostPeriod.LastThirtyDays => statistics.LastThirtyDays,
            TokenCostPeriod.CurrentWeek => statistics.CurrentWeek,
            TokenCostPeriod.CurrentMonth => statistics.CurrentMonth,
            _ => statistics.Lifetime,
        };
    }

    /// <summary>
    /// Returns the requested model-specific token-cost period.
    /// </summary>
    private static TokenCostSummary GetPeriodSummary(TokenCostModelStatistics statistics, TokenCostPeriod period)
    {
        return period switch
        {
            TokenCostPeriod.Today => statistics.Today,
            TokenCostPeriod.LastSevenDays => statistics.LastSevenDays,
            TokenCostPeriod.LastThirtyDays => statistics.LastThirtyDays,
            TokenCostPeriod.CurrentWeek => statistics.CurrentWeek,
            TokenCostPeriod.CurrentMonth => statistics.CurrentMonth,
            _ => statistics.Lifetime,
        };
    }

    /// <summary>
    /// Formats normalized model identifiers for the compact donut legend.
    /// </summary>
    private static string FormatModelLabel(string model)
    {
        string label = model;
        string? suffix = s_ModelEffortSuffixes.FirstOrDefault(label.EndsWith);
        if (suffix != null)
        {
            label = label[..^suffix.Length];
        }

        if (label.Equals("unknown", StringComparison.OrdinalIgnoreCase) || label.Length == 0)
        {
            return "Unknown";
        }

        return label.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
            ? $"GPT-{label[4..]}"
            : label;
    }

    /// <summary>
    /// Creates one filled ring segment for the Codex model distribution chart.
    /// </summary>
    private static Media.Geometry CreateDonutSegmentGeometry(double startAngle, double sweepAngle)
    {
        System.Windows.Point center = new(k_TokenCostDonutCenter, k_TokenCostDonutCenter);
        if (sweepAngle >= 359.999)
        {
            Media.CombinedGeometry ring = new(
                Media.GeometryCombineMode.Exclude,
                new Media.EllipseGeometry(center, k_TokenCostDonutOuterRadius, k_TokenCostDonutOuterRadius),
                new Media.EllipseGeometry(center, k_TokenCostDonutInnerRadius, k_TokenCostDonutInnerRadius));
            ring.Freeze();
            return ring;
        }

        double endAngle = startAngle + sweepAngle;
        System.Windows.Point outerStart = PointOnCircle(center, k_TokenCostDonutOuterRadius, startAngle);
        System.Windows.Point outerEnd = PointOnCircle(center, k_TokenCostDonutOuterRadius, endAngle);
        System.Windows.Point innerEnd = PointOnCircle(center, k_TokenCostDonutInnerRadius, endAngle);
        System.Windows.Point innerStart = PointOnCircle(center, k_TokenCostDonutInnerRadius, startAngle);
        bool isLargeArc = sweepAngle > 180;
        Media.PathFigure figure = new() { StartPoint = outerStart, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new Media.ArcSegment(outerEnd, new System.Windows.Size(k_TokenCostDonutOuterRadius, k_TokenCostDonutOuterRadius), 0, isLargeArc, Media.SweepDirection.Clockwise, true));
        figure.Segments.Add(new Media.LineSegment(innerEnd, true));
        figure.Segments.Add(new Media.ArcSegment(innerStart, new System.Windows.Size(k_TokenCostDonutInnerRadius, k_TokenCostDonutInnerRadius), 0, isLargeArc, Media.SweepDirection.Counterclockwise, true));
        Media.PathGeometry geometry = new([figure]);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Returns one Cartesian point on a circle for the supplied clockwise angle.
    /// </summary>
    private static System.Windows.Point PointOnCircle(System.Windows.Point center, double radius, double angle)
    {
        double radians = angle * Math.PI / 180;
        return new System.Windows.Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    /// <summary>
    /// Toggles the Codex chart between rolling thirty-day and current-month periods.
    /// </summary>
    [RelayCommand]
    private void ToggleCodexTokenCostChartPeriod()
    {
        m_CodexTokenCostChartPeriod = m_CodexTokenCostChartPeriod == CodexTokenCostChartPeriod.LastThirtyDays
            ? CodexTokenCostChartPeriod.CurrentMonth
            : CodexTokenCostChartPeriod.LastThirtyDays;
        UpdateCodexTokenCostChart();
        UpdateCodexTokenCostPeriodRows();
        UpdateCodexTokenCostDonut();
    }

    /// <summary>
    /// Synchronizes Codex row titles, periods, and values with the active chart mode.
    /// </summary>
    private void UpdateCodexTokenCostPeriodRows()
    {
        bool useCalendarPeriods = m_CodexTokenCostChartPeriod == CodexTokenCostChartPeriod.CurrentMonth;
        CodexTokenCostRows[1].Title = useCalendarPeriods ? "Week" : "7d";
        CodexTokenCostRows[1].Period = useCalendarPeriods ? TokenCostPeriod.CurrentWeek : TokenCostPeriod.LastSevenDays;
        CodexTokenCostRows[2].Title = useCalendarPeriods ? "Month" : "30d";
        CodexTokenCostRows[2].Period = useCalendarPeriods ? TokenCostPeriod.CurrentMonth : TokenCostPeriod.LastThirtyDays;
        UpdateTokenCostRows(CodexTokenCostRows, m_CodexTokenCostStatistics);
    }

    /// <summary>
    /// Updates the Codex chart data, columns, labels, and toggle presentation.
    /// </summary>
    private void UpdateCodexTokenCostChart()
    {
        bool isCurrentMonth = m_CodexTokenCostChartPeriod == CodexTokenCostChartPeriod.CurrentMonth;
        IReadOnlyList<TokenCostDailySummary>? dailySummaries;
        int slotCount;
        int interactiveSlotCount;
        if (isCurrentMonth)
        {
            dailySummaries = CreateCurrentMonthChartDailySummaries(m_CodexTokenCostStatistics?.CurrentMonthDaily, out interactiveSlotCount);
            slotCount = dailySummaries.Count;
        }
        else
        {
            dailySummaries = m_CodexTokenCostStatistics?.LastThirtyDaysDaily;
            slotCount = 30;
            interactiveSlotCount = slotCount;
        }

        CodexTokenCostChartColumnCount = slotCount;
        CodexTokenCostChartDays = CreateTokenCostChartDays(dailySummaries, slotCount, k_CodexTokenCostChartMaximumBarHeight, useSparseDayLabels: true, interactiveSlotCount);
        CodexTokenCostChartLabels = CreateCodexTokenCostChartLabels(CodexTokenCostChartDays, !isCurrentMonth);
    }

    /// <summary>
    /// Pads current-month daily summaries through the final calendar day while keeping future slots empty.
    /// </summary>
    private static IReadOnlyList<TokenCostDailySummary> CreateCurrentMonthChartDailySummaries(
        IReadOnlyList<TokenCostDailySummary>? dailySummaries,
        out int interactiveSlotCount)
    {
        DateTime asOfDate = dailySummaries is { Count: > 0 }
            ? dailySummaries.Max(day => day.Date)
            : DateTime.Today;
        DateTime monthStart = new(asOfDate.Year, asOfDate.Month, 1);
        int daysInMonth = DateTime.DaysInMonth(asOfDate.Year, asOfDate.Month);
        interactiveSlotCount = Math.Clamp(asOfDate.Day, 1, daysInMonth);
        Dictionary<DateTime, TokenCostSummary> summariesByDate = dailySummaries?
            .Where(day => day.Date.Year == asOfDate.Year && day.Date.Month == asOfDate.Month)
            .ToDictionary(day => day.Date.Date, day => day.Summary)
            ?? [];
        return Enumerable.Range(0, daysInMonth)
            .Select(index =>
            {
                DateTime date = monthStart.AddDays(index);
                return new TokenCostDailySummary
                {
                    Date = date,
                    Summary = summariesByDate.GetValueOrDefault(date) ?? new TokenCostSummary(),
                };
            })
            .ToArray();
    }

    /// <summary>
    /// Creates up to four independently positioned labels for the Codex chart axis.
    /// </summary>
    private static IReadOnlyList<TokenCostChartLabel> CreateCodexTokenCostChartLabels(IReadOnlyList<TokenCostChartDay> days, bool useRollingThirtyDayTicks)
    {
        if (days.Count == 0)
        {
            return [];
        }

        int lastIndex = days.Count - 1;
        int[] indexes = useRollingThirtyDayTicks && days.Count == 30
            ? [0, 9, 19, 29]
            : [0, lastIndex / 3, lastIndex * 2 / 3, lastIndex];
        double plotWidth = k_CodexTokenCostChartWidth - k_CodexTokenCostChartHorizontalMargin * 2;
        return indexes
            .Distinct()
            .Select(index =>
            {
                double center = k_CodexTokenCostChartHorizontalMargin + plotWidth * (index + 0.5) / days.Count;
                double left = Math.Clamp(center - k_CodexTokenCostChartLabelWidth / 2, 0, k_CodexTokenCostChartWidth - k_CodexTokenCostChartLabelWidth);
                return new TokenCostChartLabel(days[index].Date.Day.ToString(CultureInfo.InvariantCulture), left);
            })
            .ToArray();
    }

    /// <summary>
    /// Creates the rolling seven-day chart with today in the final slot.
    /// </summary>
    private IReadOnlyList<TokenCostChartDay> CreateTokenCostChartDays(TokenCostStatistics? statistics)
    {
        return CreateTokenCostChartDays(statistics?.LastSevenDaysDaily, 7, k_TokenCostChartMaximumBarHeight, useSparseDayLabels: false);
    }

    /// <summary>
    /// Creates a rolling token-cost chart for the supplied daily sequence.
    /// </summary>
    private IReadOnlyList<TokenCostChartDay> CreateTokenCostChartDays(
        IReadOnlyList<TokenCostDailySummary>? dailySummaries,
        int slotCount,
        double maximumBarHeight,
        bool useSparseDayLabels,
        int? interactiveSlotCount = null)
    {
        int interactiveCount = Math.Clamp(interactiveSlotCount ?? slotCount, 0, slotCount);
        if (dailySummaries == null || dailySummaries.Count == 0)
        {
            List<TokenCostChartDay> unavailableDays = new(slotCount);
            for (int offset = 1 - slotCount; offset <= 0; offset++)
            {
                int index = offset + slotCount - 1;
                DateTime date = DateTime.Today.AddDays(offset);
                string tooltip = $"{date:yyyy-MM-dd}{Environment.NewLine}Tokens: N/A{Environment.NewLine}Cost: N/A";
                string label = useSparseDayLabels && (index == 0 || index == slotCount - 1 || (index + 1) % 10 == 0)
                    ? date.Day.ToString(CultureInfo.InvariantCulture)
                    : useSparseDayLabels
                        ? string.Empty
                        : date.ToString("ddd", CultureInfo.InvariantCulture)[..1];
                unavailableDays.Add(new TokenCostChartDay(date, label, 0, tooltip, index < interactiveCount));
            }

            return unavailableDays;
        }

        decimal maximumCost = dailySummaries.Max(day => day.Summary.CostUsd ?? 0);
        long maximumTokens = dailySummaries.Max(day => day.Summary.TotalTokens);
        bool chartByCost = maximumCost > 0;
        List<TokenCostChartDay> days = new(dailySummaries.Count);
        for (int index = 0; index < dailySummaries.Count; index++)
        {
            TokenCostDailySummary day = dailySummaries[index];
            decimal cost = day.Summary.CostUsd ?? 0;
            double height = chartByCost
                ? cost <= 0 ? 0 : Math.Max(2, (double)(cost / maximumCost) * maximumBarHeight)
                : maximumTokens <= 0 || day.Summary.TotalTokens <= 0
                    ? 0
                    : Math.Max(2, (double)day.Summary.TotalTokens / maximumTokens * maximumBarHeight);
            string costText = day.Summary.CostUsd?.ToString("$0.00", CultureInfo.InvariantCulture) ?? "N/A";
            string tokensText = AppSettings.FormatTokenCount(day.Summary.TotalTokens, m_Settings.TokenUnit);
            string tooltip = $"{day.Date:yyyy-MM-dd}{Environment.NewLine}Tokens: {tokensText}{Environment.NewLine}Cost: {costText}";
            string label = useSparseDayLabels && (index == 0 || index == dailySummaries.Count - 1 || (index + 1) % 10 == 0)
                ? day.Date.Day.ToString(CultureInfo.InvariantCulture)
                : useSparseDayLabels
                    ? string.Empty
                    : day.Date.ToString("ddd", CultureInfo.InvariantCulture)[..1];
            days.Add(new TokenCostChartDay(day.Date, label, height, tooltip, index < interactiveCount));
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
    /// Combines Grok quota and local token-statistics errors for the status tooltip.
    /// </summary>
    private static string FormatGrokStatusTooltip(GrokUsageDashboard dashboard, bool usageAvailable, bool tokenCostAvailable)
    {
        List<string> errors = [];
        if (!usageAvailable && dashboard.Error.Length > 0)
        {
            errors.Add($"Usage: {dashboard.Error}");
        }

        if (!tokenCostAvailable)
        {
            errors.Add("Token Cost: Local Grok Build session data could not be read.");
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
    /// Shows the Grok dashboard page inside the tray popup.
    /// </summary>
    [RelayCommand]
    public void ShowGrok()
    {
        SetPage(k_GrokPageName);
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
        OnPropertyChanged(nameof(IsGrokVisible));
        OnPropertyChanged(nameof(IsCursorVisible));
        OnPropertyChanged(nameof(IsApiVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(IsAboutVisible));
        OnPropertyChanged(nameof(IsCodexSelected));
        OnPropertyChanged(nameof(IsGrokSelected));
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
    /// Formats a Grok billing subscription tier while preserving future tier names.
    /// </summary>
    private static string FormatGrokPlan(string? subscriptionTier)
    {
        string normalized = string.Join(' ', (subscriptionTier ?? string.Empty)
            .Trim()
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(normalized)
            ? "UNKNOWN"
            : normalized.Replace("premium plus", "premium+", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
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
        [ObservableProperty]
        public partial string Title { get; internal set; }

        public TokenCostPeriod Period { get; internal set; }

        [ObservableProperty]
        public partial TokenCostDisplay Display { get; internal set; } = s_UnavailableTokenCostDisplay;

        [ObservableProperty]
        public partial bool IsSelected { get; internal set; }

        public bool IsLast { get; }

        /// <summary>
        /// Creates one fixed token-cost display row.
        /// </summary>
        public TokenCostRowViewModel(string title, TokenCostPeriod period, bool isLast = false, bool isSelected = false)
        {
            Title = title;
            Period = period;
            IsLast = isLast;
            IsSelected = isSelected;
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

                UpdateUnavailable();
                return;
            }

            UpdateRemaining(limit.RemainingPercent, limit.ResetLabel, showReset: true);
        }

        /// <summary>
        /// Updates the quota display from used-percent data.
        /// </summary>
        public void UpdateUsedPercent(double usedPercent, string resetText, bool showReset)
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
