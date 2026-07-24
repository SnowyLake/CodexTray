using CodexTray.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
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

internal sealed record InAppDialogRequest(
    string Title,
    string Message,
    string PrimaryButtonText,
    string? SecondaryButtonText = null,
    Action? PrimaryAction = null);

internal sealed class TrayPopupViewModel : INotifyPropertyChanged
{
    private const string k_HomePageName = "Home";
    private const string k_CursorPageName = "Cursor";
    private const string k_ApiPageName = "API";
    private const string k_SettingsPageName = "Settings";
    private const string k_AboutPageName = "About";
    private const string k_RepositoryUrl = "https://github.com/SnowyLake/CodexTray";

    private static readonly Media.Brush s_GreenBrush = new Media.SolidColorBrush(Media.Color.FromRgb(26, 188, 137));
    private static readonly Media.Brush s_YellowBrush = new Media.SolidColorBrush(Media.Color.FromRgb(226, 176, 54));
    private static readonly Media.Brush s_RedBrush = new Media.SolidColorBrush(Media.Color.FromRgb(224, 91, 77));
    private static readonly Media.Brush s_PlanBadgeActiveBrush = s_GreenBrush;
    private static readonly Media.Brush s_PlanBadgeInactiveBrush = new Media.SolidColorBrush(Media.Color.FromRgb(107, 122, 117));
    private static readonly TokenCostDisplay s_UnavailableTokenCostDisplay = new("N/A", "N/A");

    private readonly AppSettings m_Settings;
    private string m_CurrentPage = k_HomePageName;
    private string m_PlanDisplay = "UNKNOWN";
    private Media.Brush m_PlanBadgeBrush = s_PlanBadgeInactiveBrush;
    private Media.Brush m_StatusDotBrush = s_RedBrush;
    private string m_UpdatedAtDisplay = "Waiting for first refresh";
    private string m_ServiceStatus = "Service: starting";
    private string m_SourceDisplay = "Source: unavailable";
    private string m_ResetCreditsDisplay = "N/A";
    private string m_ResetCreditsResetTime = "unknown";
    private bool m_IsResetCreditsResetTimeVisible = true;
    private string m_LiteMonitorDir = string.Empty;
    private string m_TrafficMonitorDir = string.Empty;
    private string m_PortText = CodexTrayDefaults.Port.ToString(CultureInfo.InvariantCulture);
    private string m_RefreshIntervalText = CodexTrayDefaults.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture);
    private string m_ThemeMode = AppSettings.ThemeModeSystem;
    private string m_TokenUnit = AppSettings.TokenUnitEnglish;
    private TokenCostItem m_TokenCostItems = TokenCostItem.All;
    private TokenCostDisplay m_TodayTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_YesterdayTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_WeekTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_MonthTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_SevenDayTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_ThirtyDayTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_TotalTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_CursorTodayTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_CursorYesterdayTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_CursorWeekTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_CursorMonthTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_CursorSevenDayTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_CursorThirtyDayTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private TokenCostDisplay m_CursorTotalTokenCostDisplay = s_UnavailableTokenCostDisplay;
    private bool m_StartWithWindows;
    private bool m_AcrylicEnabled = CodexTrayDefaults.AcrylicEnabled;
    private int m_AcrylicOpacityPercent = CodexTrayDefaults.AcrylicOpacityPercent;
    private string m_WindowWidthText = CodexTrayDefaults.WindowWidth.ToString(CultureInfo.InvariantCulture);
    private string m_WindowHeightText = CodexTrayDefaults.WindowHeight.ToString(CultureInfo.InvariantCulture);
    private bool m_ShowResetTimeInPlugins = CodexTrayDefaults.ShowResetTimeInPlugins;
    private bool m_UseAbsoluteResetTime = CodexTrayDefaults.UseAbsoluteResetTime;
    private bool m_HideInvalidProgressBars = CodexTrayDefaults.HideInvalidProgressBars;
    private bool m_IsRefreshing;
    private bool m_IsInAppDialogOpen;
    private bool m_IsNativeModalOpen;
    private bool m_IsDetectingLiteMonitor;
    private bool m_IsDetectingTrafficMonitor;
    private ApiUsageRefreshStatus? m_ApiUsageStatus;
    private int m_ApiUsageErrorCount;
    private int m_ApiUsageMonitorCount;
    private DateTimeOffset? m_ApiUsageUpdatedAt;
    private Media.Brush m_CursorStatusDotBrush = s_RedBrush;
    private string m_CursorUpdatedAtDisplay = "Waiting for first refresh";
    private string m_CursorStatusTooltip = string.Empty;
    private string m_InAppDialogTitle = string.Empty;
    private string m_InAppDialogMessage = string.Empty;
    private string m_InAppDialogPrimaryButtonText = "OK";
    private string m_InAppDialogSecondaryButtonText = string.Empty;
    private Action? m_InAppDialogPrimaryAction;

    private SettingsStatus m_SettingsStatus = SettingsStatus.Clean;
    private SettingsStatus m_SettingsBaseline = SettingsStatus.Clean;
    private bool m_SuppressDirtyTracking;
    private string m_SnapshotLiteMonitorDir = string.Empty;
    private string m_SnapshotTrafficMonitorDir = string.Empty;
    private string m_SnapshotPortText = string.Empty;
    private string m_SnapshotRefreshIntervalText = string.Empty;
    private string m_SnapshotThemeMode = AppSettings.ThemeModeSystem;
    private string m_SnapshotTokenUnit = AppSettings.TokenUnitEnglish;
    private TokenCostItem m_SnapshotTokenCostItems = TokenCostItem.All;
    private bool m_SnapshotStartWithWindows;
    private bool m_SnapshotAcrylicEnabled = CodexTrayDefaults.AcrylicEnabled;
    private int m_SnapshotAcrylicOpacityPercent = CodexTrayDefaults.AcrylicOpacityPercent;
    private string m_SnapshotWindowWidthText = string.Empty;
    private string m_SnapshotWindowHeightText = string.Empty;
    private bool m_SnapshotShowResetTimeInPlugins = CodexTrayDefaults.ShowResetTimeInPlugins;
    private bool m_SnapshotUseAbsoluteResetTime = CodexTrayDefaults.UseAbsoluteResetTime;
    private bool m_SnapshotHideInvalidProgressBars = CodexTrayDefaults.HideInvalidProgressBars;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? RefreshRequested;

    public event EventHandler? SaveSettingsRequested;

    public event EventHandler? ApiMonitorsChanged;

    public event EventHandler? InstallLiteMonitorPluginRequested;

    public event EventHandler? InstallTrafficMonitorPluginRequested;

    public event EventHandler? ExitRequested;

    public event Action<InAppDialogRequest>? InAppDialogRequested;

    public QuotaViewModel FiveHourQuota { get; } = new("5-Hour");

    public QuotaViewModel SevenDayQuota { get; } = new("7-Day");

    public QuotaViewModel CursorTotalQuota { get; } = new("Total");

    public QuotaViewModel CursorFirstPartyQuota { get; } = new("First Party");

    public QuotaViewModel CursorApiQuota { get; } = new("API");

    public ICommand ShowHomeCommand { get; }

    public ICommand ShowCursorCommand { get; }

    public ICommand ShowApiCommand { get; }

    public ICommand ShowSettingsCommand { get; }

    public ICommand ShowAboutCommand { get; }

    public ICommand OpenRepositoryCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand SaveSettingsCommand { get; }

    public ICommand InstallLiteMonitorPluginCommand { get; }

    public ICommand InstallTrafficMonitorPluginCommand { get; }

    public ICommand BrowseLiteMonitorCommand { get; }

    public ICommand BrowseTrafficMonitorCommand { get; }

    public ICommand AutoDetectLiteMonitorCommand { get; }

    public ICommand AutoDetectTrafficMonitorCommand { get; }

    public ICommand ExitCommand { get; }

    public ICommand AddApiMonitorCommand { get; }

    public ICommand RemoveApiMonitorCommand { get; }

    public ICommand MoveApiMonitorUpCommand { get; }

    public ICommand MoveApiMonitorDownCommand { get; }

    public ICommand ConfirmInAppDialogCommand { get; }

    public ICommand DismissInAppDialogCommand { get; }

    public ObservableCollection<ApiMonitorViewModel> ApiMonitors { get; } = [];

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

    public Media.Brush StatusDotBrush
    {
        get => m_StatusDotBrush;
        private set => SetField(ref m_StatusDotBrush, value);
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

    public string ResetCreditsDisplay
    {
        get => m_ResetCreditsDisplay;
        private set => SetField(ref m_ResetCreditsDisplay, value);
    }

    public string ResetCreditsResetTime
    {
        get => m_ResetCreditsResetTime;
        private set => SetField(ref m_ResetCreditsResetTime, value);
    }

    public bool IsResetCreditsResetTimeVisible
    {
        get => m_IsResetCreditsResetTimeVisible;
        private set => SetField(ref m_IsResetCreditsResetTimeVisible, value);
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

    public string TokenUnit
    {
        get => m_TokenUnit;
        set
        {
            string normalized = value == AppSettings.TokenUnitChinese ? AppSettings.TokenUnitChinese : AppSettings.TokenUnitEnglish;
            if (SetField(ref m_TokenUnit, normalized))
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

    public TokenCostItem TokenCostItems
    {
        get => m_TokenCostItems;
        set
        {
            if (SetField(ref m_TokenCostItems, value & TokenCostItem.All))
            {
                OnPropertyChanged(nameof(TokenCostItemsDisplay));
                OnPropertyChanged(nameof(IsTokenCostVisible));
                OnPropertyChanged(nameof(ShowTodayTokenCost));
                OnPropertyChanged(nameof(ShowYesterdayTokenCost));
                OnPropertyChanged(nameof(ShowWeekTokenCost));
                OnPropertyChanged(nameof(ShowMonthTokenCost));
                OnPropertyChanged(nameof(ShowSevenDayTokenCost));
                OnPropertyChanged(nameof(ShowThirtyDayTokenCost));
                EvaluateDirtyState();
            }
        }
    }

    public string TokenCostItemsDisplay => m_TokenCostItems switch
    {
        TokenCostItem.None => "None",
        TokenCostItem.All => "All",
        _ => "Custom",
    };

    public bool IsTokenCostVisible => m_TokenCostItems != TokenCostItem.None;

    public bool ShowTodayTokenCost
    {
        get => (m_TokenCostItems & TokenCostItem.Today) != 0;
        set => SetTokenCostItem(TokenCostItem.Today, value);
    }

    public bool ShowYesterdayTokenCost
    {
        get => (m_TokenCostItems & TokenCostItem.Yesterday) != 0;
        set => SetTokenCostItem(TokenCostItem.Yesterday, value);
    }

    public bool ShowWeekTokenCost
    {
        get => (m_TokenCostItems & TokenCostItem.Week) != 0;
        set => SetTokenCostItem(TokenCostItem.Week, value);
    }

    public bool ShowMonthTokenCost
    {
        get => (m_TokenCostItems & TokenCostItem.Month) != 0;
        set => SetTokenCostItem(TokenCostItem.Month, value);
    }

    public bool ShowSevenDayTokenCost
    {
        get => (m_TokenCostItems & TokenCostItem.SevenDay) != 0;
        set => SetTokenCostItem(TokenCostItem.SevenDay, value);
    }

    public bool ShowThirtyDayTokenCost
    {
        get => (m_TokenCostItems & TokenCostItem.ThirtyDay) != 0;
        set => SetTokenCostItem(TokenCostItem.ThirtyDay, value);
    }

    public TokenCostDisplay TodayTokenCostDisplay
    {
        get => m_TodayTokenCostDisplay;
        private set => SetField(ref m_TodayTokenCostDisplay, value);
    }

    public TokenCostDisplay YesterdayTokenCostDisplay
    {
        get => m_YesterdayTokenCostDisplay;
        private set => SetField(ref m_YesterdayTokenCostDisplay, value);
    }

    public TokenCostDisplay WeekTokenCostDisplay
    {
        get => m_WeekTokenCostDisplay;
        private set => SetField(ref m_WeekTokenCostDisplay, value);
    }

    public TokenCostDisplay MonthTokenCostDisplay
    {
        get => m_MonthTokenCostDisplay;
        private set => SetField(ref m_MonthTokenCostDisplay, value);
    }

    public TokenCostDisplay SevenDayTokenCostDisplay
    {
        get => m_SevenDayTokenCostDisplay;
        private set => SetField(ref m_SevenDayTokenCostDisplay, value);
    }

    public TokenCostDisplay ThirtyDayTokenCostDisplay
    {
        get => m_ThirtyDayTokenCostDisplay;
        private set => SetField(ref m_ThirtyDayTokenCostDisplay, value);
    }

    public TokenCostDisplay TotalTokenCostDisplay
    {
        get => m_TotalTokenCostDisplay;
        private set => SetField(ref m_TotalTokenCostDisplay, value);
    }

    public TokenCostDisplay CursorTodayTokenCostDisplay
    {
        get => m_CursorTodayTokenCostDisplay;
        private set => SetField(ref m_CursorTodayTokenCostDisplay, value);
    }

    public TokenCostDisplay CursorYesterdayTokenCostDisplay
    {
        get => m_CursorYesterdayTokenCostDisplay;
        private set => SetField(ref m_CursorYesterdayTokenCostDisplay, value);
    }

    public TokenCostDisplay CursorWeekTokenCostDisplay
    {
        get => m_CursorWeekTokenCostDisplay;
        private set => SetField(ref m_CursorWeekTokenCostDisplay, value);
    }

    public TokenCostDisplay CursorMonthTokenCostDisplay
    {
        get => m_CursorMonthTokenCostDisplay;
        private set => SetField(ref m_CursorMonthTokenCostDisplay, value);
    }

    public TokenCostDisplay CursorSevenDayTokenCostDisplay
    {
        get => m_CursorSevenDayTokenCostDisplay;
        private set => SetField(ref m_CursorSevenDayTokenCostDisplay, value);
    }

    public TokenCostDisplay CursorThirtyDayTokenCostDisplay
    {
        get => m_CursorThirtyDayTokenCostDisplay;
        private set => SetField(ref m_CursorThirtyDayTokenCostDisplay, value);
    }

    public TokenCostDisplay CursorTotalTokenCostDisplay
    {
        get => m_CursorTotalTokenCostDisplay;
        private set => SetField(ref m_CursorTotalTokenCostDisplay, value);
    }

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
            int clamped = Math.Clamp(value, CodexTrayDefaults.MinimumAcrylicOpacityPercent, CodexTrayDefaults.MaximumAcrylicOpacityPercent);
            if (SetField(ref m_AcrylicOpacityPercent, clamped))
            {
                OnPropertyChanged(nameof(AcrylicOpacityDisplay));
                EvaluateDirtyState();
            }
        }
    }

    public string WindowWidthText
    {
        get => m_WindowWidthText;
        set
        {
            if (SetField(ref m_WindowWidthText, value))
            {
                EvaluateDirtyState();
            }
        }
    }

    public string WindowHeightText
    {
        get => m_WindowHeightText;
        set
        {
            if (SetField(ref m_WindowHeightText, value))
            {
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

    public bool UseAbsoluteResetTime
    {
        get => m_UseAbsoluteResetTime;
        set
        {
            if (SetField(ref m_UseAbsoluteResetTime, value))
            {
                EvaluateDirtyState();
            }
        }
    }

    public bool HideInvalidProgressBars
    {
        get => m_HideInvalidProgressBars;
        set
        {
            if (SetField(ref m_HideInvalidProgressBars, value))
            {
                EvaluateDirtyState();
            }
        }
    }

    public bool IsUsageSectionHeaderVisible =>
        !m_HideInvalidProgressBars || FiveHourQuota.IsVisible || SevenDayQuota.IsVisible;

    public string AcrylicOpacityDisplay => $"{m_AcrylicOpacityPercent}%";

    public int AcrylicOpacityMinimum => CodexTrayDefaults.MinimumAcrylicOpacityPercent;

    public int AcrylicOpacityMaximum => CodexTrayDefaults.MaximumAcrylicOpacityPercent;

    public bool IsHomeVisible => m_CurrentPage == k_HomePageName;

    public bool IsCursorVisible => m_CurrentPage == k_CursorPageName;

    public bool IsApiVisible => m_CurrentPage == k_ApiPageName;

    public bool IsSettingsVisible => m_CurrentPage == k_SettingsPageName;

    public bool IsAboutVisible => m_CurrentPage == k_AboutPageName;

    public bool IsHomeSelected => m_CurrentPage == k_HomePageName;

    public bool IsCursorSelected => m_CurrentPage == k_CursorPageName;

    public bool IsApiSelected => m_CurrentPage == k_ApiPageName;

    public bool IsSettingsSelected => m_CurrentPage == k_SettingsPageName;

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

    public Media.Brush CursorStatusDotBrush
    {
        get => m_CursorStatusDotBrush;
        private set => SetField(ref m_CursorStatusDotBrush, value);
    }

    public string CursorUpdatedAtDisplay
    {
        get => m_CursorUpdatedAtDisplay;
        private set => SetField(ref m_CursorUpdatedAtDisplay, value);
    }

    public string CursorStatusTooltip
    {
        get => m_CursorStatusTooltip;
        private set => SetField(ref m_CursorStatusTooltip, value);
    }

    public bool IsRefreshing
    {
        get => m_IsRefreshing;
        set => SetField(ref m_IsRefreshing, value);
    }

    public bool IsModalOpen => m_IsInAppDialogOpen || m_IsNativeModalOpen;

    public bool IsInAppDialogOpen
    {
        get => m_IsInAppDialogOpen;
        private set
        {
            if (SetField(ref m_IsInAppDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsModalOpen));
            }
        }
    }

    public string InAppDialogTitle
    {
        get => m_InAppDialogTitle;
        private set => SetField(ref m_InAppDialogTitle, value);
    }

    public string InAppDialogMessage
    {
        get => m_InAppDialogMessage;
        private set => SetField(ref m_InAppDialogMessage, value);
    }

    public string InAppDialogPrimaryButtonText
    {
        get => m_InAppDialogPrimaryButtonText;
        private set => SetField(ref m_InAppDialogPrimaryButtonText, value);
    }

    public string InAppDialogSecondaryButtonText
    {
        get => m_InAppDialogSecondaryButtonText;
        private set
        {
            if (SetField(ref m_InAppDialogSecondaryButtonText, value))
            {
                OnPropertyChanged(nameof(HasInAppDialogSecondaryButton));
            }
        }
    }

    public bool HasInAppDialogSecondaryButton => m_InAppDialogSecondaryButtonText.Length > 0;

    /// <summary>
    /// Creates a view model for the WPF tray popup.
    /// </summary>
    public TrayPopupViewModel(AppSettings settings)
    {
        m_Settings = settings;
        LoadSettings(settings);
        LoadApiMonitors(settings.ApiMonitors);
        ShowHomeCommand = new RelayCommand(_ => ShowHome());
        ShowCursorCommand = new RelayCommand(_ => ShowCursor());
        ShowApiCommand = new RelayCommand(_ => ShowApi());
        ShowSettingsCommand = new RelayCommand(_ => ShowSettings());
        ShowAboutCommand = new RelayCommand(_ => ShowAbout());
        OpenRepositoryCommand = new RelayCommand(_ => OpenUrl(k_RepositoryUrl));
        RefreshCommand = new RelayCommand(_ => RefreshRequested?.Invoke(this, EventArgs.Empty));
        SaveSettingsCommand = new RelayCommand(_ => SaveSettingsRequested?.Invoke(this, EventArgs.Empty));
        InstallLiteMonitorPluginCommand = new RelayCommand(_ => InstallLiteMonitorPluginRequested?.Invoke(this, EventArgs.Empty));
        InstallTrafficMonitorPluginCommand = new RelayCommand(_ => InstallTrafficMonitorPluginRequested?.Invoke(this, EventArgs.Empty));
        BrowseLiteMonitorCommand = new RelayCommand(_ => BrowseMonitorFolder("Select LiteMonitor folder", LiteMonitorDir, value => LiteMonitorDir = value));
        BrowseTrafficMonitorCommand = new RelayCommand(_ => BrowseMonitorFolder("Select TrafficMonitor folder", TrafficMonitorDir, value => TrafficMonitorDir = value));
        AutoDetectLiteMonitorCommand = new RelayCommand(async _ => await DetectLiteMonitorAsync(showNotFound: true));
        AutoDetectTrafficMonitorCommand = new RelayCommand(async _ => await DetectTrafficMonitorAsync(showNotFound: true));
        ExitCommand = new RelayCommand(_ => ExitRequested?.Invoke(this, EventArgs.Empty));
        AddApiMonitorCommand = new RelayCommand(_ => AddApiMonitor());
        RemoveApiMonitorCommand = new RelayCommand(RemoveApiMonitor);
        MoveApiMonitorUpCommand = new RelayCommand(parameter => MoveApiMonitor(parameter, -1));
        MoveApiMonitorDownCommand = new RelayCommand(parameter => MoveApiMonitor(parameter, 1));
        ConfirmInAppDialogCommand = new RelayCommand(_ => ConfirmInAppDialog());
        DismissInAppDialogCommand = new RelayCommand(_ => DismissInAppDialog());
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
            TokenCostItems = settings.TokenCostItems;
            StartWithWindows = settings.StartWithWindows;
            AcrylicEnabled = settings.AcrylicEnabled;
            AcrylicOpacityPercent = settings.AcrylicOpacityPercent;
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
    /// Captures the current editable values as the saved snapshot baseline.
    /// </summary>
    private void CaptureSnapshot(SettingsStatus baseline)
    {
        m_SnapshotLiteMonitorDir = m_LiteMonitorDir;
        m_SnapshotTrafficMonitorDir = m_TrafficMonitorDir;
        m_SnapshotPortText = m_PortText;
        m_SnapshotRefreshIntervalText = m_RefreshIntervalText;
        m_SnapshotThemeMode = m_ThemeMode;
        m_SnapshotTokenUnit = m_TokenUnit;
        m_SnapshotTokenCostItems = m_TokenCostItems;
        m_SnapshotStartWithWindows = m_StartWithWindows;
        m_SnapshotAcrylicEnabled = m_AcrylicEnabled;
        m_SnapshotAcrylicOpacityPercent = m_AcrylicOpacityPercent;
        m_SnapshotWindowWidthText = m_WindowWidthText;
        m_SnapshotWindowHeightText = m_WindowHeightText;
        m_SnapshotShowResetTimeInPlugins = m_ShowResetTimeInPlugins;
        m_SnapshotUseAbsoluteResetTime = m_UseAbsoluteResetTime;
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
            m_LiteMonitorDir == m_SnapshotLiteMonitorDir &&
            m_TrafficMonitorDir == m_SnapshotTrafficMonitorDir &&
            m_PortText == m_SnapshotPortText &&
            m_RefreshIntervalText == m_SnapshotRefreshIntervalText &&
            m_ThemeMode == m_SnapshotThemeMode &&
            m_TokenUnit == m_SnapshotTokenUnit &&
            m_TokenCostItems == m_SnapshotTokenCostItems &&
            m_StartWithWindows == m_SnapshotStartWithWindows &&
            m_AcrylicEnabled == m_SnapshotAcrylicEnabled &&
            m_AcrylicOpacityPercent == m_SnapshotAcrylicOpacityPercent &&
            m_WindowWidthText == m_SnapshotWindowWidthText &&
            m_WindowHeightText == m_SnapshotWindowHeightText &&
            m_ShowResetTimeInPlugins == m_SnapshotShowResetTimeInPlugins &&
            m_UseAbsoluteResetTime == m_SnapshotUseAbsoluteResetTime &&
            m_HideInvalidProgressBars == m_SnapshotHideInvalidProgressBars;

        SettingsStatus = matchesSnapshot ? m_SettingsBaseline : SettingsStatus.Unsaved;
    }

    /// <summary>
    /// Applies editable properties to the shared settings model.
    /// </summary>
    public bool TryApplySettings(out string message)
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
        m_Settings.TokenCostItems = TokenCostItems;
        m_Settings.AcrylicEnabled = AcrylicEnabled;
        m_Settings.AcrylicOpacityPercent = AcrylicOpacityPercent;
        m_Settings.WindowWidth = windowWidth;
        m_Settings.WindowHeight = windowHeight;
        m_Settings.ShowResetTimeInPlugins = ShowResetTimeInPlugins;
        m_Settings.UseAbsoluteResetTime = UseAbsoluteResetTime;
        m_Settings.HideInvalidProgressBars = HideInvalidProgressBars;
        CaptureSnapshot(SettingsStatus.Saved);
        message = "Changes saved";
        return true;
    }

    /// <summary>
    /// Adds or removes one token cost item from the current selection.
    /// </summary>
    private void SetTokenCostItem(TokenCostItem item, bool isShown)
    {
        TokenCostItems = isShown
            ? m_TokenCostItems | item
            : m_TokenCostItems & ~item;
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
            UpdateResetCredits(null);
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
            UpdateResetCredits(null);
            return;
        }

        PlanDisplay = FormatPlan(response.PlanType);
        PlanBadgeBrush = s_PlanBadgeActiveBrush;
        StatusDotBrush = s_GreenBrush;
        UpdatedAtDisplay = FormatUpdatedAt(response.UpdatedAt);
        FiveHourQuota.Update(response.Limits.FiveHour, HideInvalidProgressBars);
        SevenDayQuota.Update(response.Limits.SevenDay, HideInvalidProgressBars);
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
            ResetCreditsDisplay = $"{resetCredits.AvailableCount} Available";
            ResetCreditsResetTime = resetCredits.NearestExpiryLocal;
            // Hide expiry only after a successful response with no remaining credits.
            IsResetCreditsResetTimeVisible = resetCredits.AvailableCount > 0;
        }
        else
        {
            // Request failed / no account: keep the full unavailable row visible.
            ResetCreditsDisplay = "N/A";
            ResetCreditsResetTime = "unknown";
            IsResetCreditsResetTimeVisible = true;
        }
    }

    /// <summary>
    /// Updates token and cost displays or marks them unavailable after a failed read.
    /// </summary>
    public void UpdateTokenCost(TokenCostStatistics? statistics)
    {
        if (statistics == null)
        {
            TodayTokenCostDisplay = s_UnavailableTokenCostDisplay;
            YesterdayTokenCostDisplay = s_UnavailableTokenCostDisplay;
            WeekTokenCostDisplay = s_UnavailableTokenCostDisplay;
            MonthTokenCostDisplay = s_UnavailableTokenCostDisplay;
            SevenDayTokenCostDisplay = s_UnavailableTokenCostDisplay;
            ThirtyDayTokenCostDisplay = s_UnavailableTokenCostDisplay;
            TotalTokenCostDisplay = s_UnavailableTokenCostDisplay;
            return;
        }

        TodayTokenCostDisplay = FormatTokenCost(statistics.Today);
        YesterdayTokenCostDisplay = FormatTokenCost(statistics.Yesterday);
        WeekTokenCostDisplay = FormatTokenCost(statistics.Week);
        MonthTokenCostDisplay = FormatTokenCost(statistics.Month);
        SevenDayTokenCostDisplay = FormatTokenCost(statistics.SevenDay);
        ThirtyDayTokenCostDisplay = FormatTokenCost(statistics.ThirtyDay);
        TotalTokenCostDisplay = FormatTokenCost(statistics.Total);
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
            string reset = m_Settings.UseAbsoluteResetTime
                ? CodexTrayCollector.FormatSevenDayResetDate(usage.ResetsAt, dashboard.UpdatedAt)
                : CodexTrayCollector.FormatSevenDayResetLabel(usage.ResetsAt, dashboard.UpdatedAt);
            CursorTotalQuota.UpdateCursorUsage(usage.TotalUsedPercent, reset, showReset: true);
            CursorFirstPartyQuota.UpdateCursorUsage(usage.FirstPartyUsedPercent, string.Empty, showReset: false);
            CursorApiQuota.UpdateCursorUsage(usage.ApiUsedPercent, string.Empty, showReset: false);
        }
        else
        {
            CursorTotalQuota.UpdateUnavailable(showReset: true, unavailableResetText: "N/A");
            CursorFirstPartyQuota.UpdateUnavailable(showReset: false, unavailableResetText: "N/A");
            CursorApiQuota.UpdateUnavailable(showReset: false, unavailableResetText: "N/A");
        }

        UpdateCursorTokenCost(dashboard.TokenCost);
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
    /// Updates Cursor token-cost rows or clears every row after an incomplete events refresh.
    /// </summary>
    private void UpdateCursorTokenCost(TokenCostStatistics? statistics)
    {
        if (statistics == null)
        {
            CursorTodayTokenCostDisplay = s_UnavailableTokenCostDisplay;
            CursorYesterdayTokenCostDisplay = s_UnavailableTokenCostDisplay;
            CursorWeekTokenCostDisplay = s_UnavailableTokenCostDisplay;
            CursorMonthTokenCostDisplay = s_UnavailableTokenCostDisplay;
            CursorSevenDayTokenCostDisplay = s_UnavailableTokenCostDisplay;
            CursorThirtyDayTokenCostDisplay = s_UnavailableTokenCostDisplay;
            CursorTotalTokenCostDisplay = s_UnavailableTokenCostDisplay;
            return;
        }

        CursorTodayTokenCostDisplay = FormatTokenCost(statistics.Today);
        CursorYesterdayTokenCostDisplay = FormatTokenCost(statistics.Yesterday);
        CursorWeekTokenCostDisplay = FormatTokenCost(statistics.Week);
        CursorMonthTokenCostDisplay = FormatTokenCost(statistics.Month);
        CursorSevenDayTokenCostDisplay = FormatTokenCost(statistics.SevenDay);
        CursorThirtyDayTokenCostDisplay = FormatTokenCost(statistics.ThirtyDay);
        CursorTotalTokenCostDisplay = FormatTokenCost(statistics.Total);
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
        return new TokenCostDisplay(cost, FormatTokens(summary.TotalTokens, m_Settings.TokenUnit));
    }

    /// <summary>
    /// Formats token counts using the selected compact unit family.
    /// </summary>
    private static string FormatTokens(long tokens, string tokenUnit)
    {
        (decimal divisor, string suffix) = tokenUnit == AppSettings.TokenUnitChinese
            ? tokens >= 100_000_000 ? (100_000_000m, "亿") : (10_000m, "万")
            : tokens >= 1_000_000_000 ? (1_000_000_000m, "B") : (1_000_000m, "M");
        return $"{tokens / divisor:0.00}{suffix}";
    }

    /// <summary>
    /// Shows the home page inside the tray popup.
    /// </summary>
    public void ShowHome()
    {
        SetPage(k_HomePageName);
    }

    /// <summary>
    /// Shows the Cursor dashboard page inside the tray popup.
    /// </summary>
    public void ShowCursor()
    {
        SetPage(k_CursorPageName);
    }

    /// <summary>
    /// Shows the API monitoring page inside the tray popup.
    /// </summary>
    public void ShowApi()
    {
        SetPage(k_ApiPageName);
    }

    /// <summary>
    /// Shows the settings page inside the tray popup.
    /// </summary>
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
        OnPropertyChanged(nameof(IsHomeVisible));
        OnPropertyChanged(nameof(IsCursorVisible));
        OnPropertyChanged(nameof(IsApiVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(IsAboutVisible));
        OnPropertyChanged(nameof(IsHomeSelected));
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
            monitor.Changed += HandleApiMonitorChanged;
            monitor.EditingSaved += HandleApiMonitorSaved;
            ApiMonitors.Add(monitor);
        }

        NotifyApiMonitorCountChanged();
    }

    /// <summary>
    /// Adds a default DeepSeek API monitor card.
    /// </summary>
    private void AddApiMonitor()
    {
        ApiMonitorViewModel monitor = new(new ApiMonitorSettings(), isEditing: true, isPending: true);
        monitor.Changed += HandleApiMonitorChanged;
        monitor.EditingSaved += HandleApiMonitorSaved;
        ApiMonitors.Add(monitor);
        NotifyApiMonitorCountChanged();
    }

    /// <summary>
    /// Requests confirmation before removing an API monitor card.
    /// </summary>
    private void RemoveApiMonitor(object? parameter)
    {
        if (parameter is not ApiMonitorViewModel monitor)
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
        monitor.Changed -= HandleApiMonitorChanged;
        monitor.EditingSaved -= HandleApiMonitorSaved;
        ApiMonitors.Remove(monitor);
        SaveApiMonitors();
        if (!wasPending)
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Moves an API monitor card by one position.
    /// </summary>
    private void MoveApiMonitor(object? parameter, int offset)
    {
        if (parameter is not ApiMonitorViewModel monitor)
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
    /// Persists a changed API monitor field.
    /// </summary>
    private void HandleApiMonitorChanged(object? sender, EventArgs args)
    {
        if (sender is ApiMonitorViewModel { IsPending: true })
        {
            return;
        }

        SaveApiMonitors();
    }

    /// <summary>
    /// Persists a confirmed API monitor and refreshes its usage.
    /// </summary>
    private void HandleApiMonitorSaved(object? sender, EventArgs args)
    {
        SaveApiMonitors();
        RefreshRequested?.Invoke(this, EventArgs.Empty);
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
    private void ConfirmInAppDialog()
    {
        Action? primaryAction = m_InAppDialogPrimaryAction;
        DismissInAppDialog();
        primaryAction?.Invoke();
    }

    /// <summary>
    /// Dismisses the active in-window dialog.
    /// </summary>
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
                LiteMonitorDir = CodexTrayDefaults.PluginPathNone;
                if (showNotFound)
                {
                    InAppDialogRequested?.Invoke(new InAppDialogRequest("LiteMonitor not found", "LiteMonitor was not found.", "OK"));
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
                TrafficMonitorDir = CodexTrayDefaults.PluginPathNone;
                if (showNotFound)
                {
                    InAppDialogRequested?.Invoke(new InAppDialogRequest("TrafficMonitor not found", "TrafficMonitor was not found.", "OK"));
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
        private string m_PercentText = "N/A";
        private string m_ResetText = "unknown";
        private Media.Brush m_AccentBrush = s_RedBrush;
        private bool m_IsVisible = true;
        private bool m_IsResetVisible = true;

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

        public bool IsVisible
        {
            get => m_IsVisible;
            private set => SetField(ref m_IsVisible, value);
        }

        public bool IsResetVisible
        {
            get => m_IsResetVisible;
            private set => SetField(ref m_IsResetVisible, value);
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
