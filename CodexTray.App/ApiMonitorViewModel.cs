using CodexTray.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Media = System.Windows.Media;

namespace CodexTray.App;

internal sealed partial class ApiMonitorViewModel : ObservableObject
{
    private static readonly Media.Brush s_GreenBrush = new Media.SolidColorBrush(Media.Color.FromRgb(26, 188, 137));
    private static readonly Media.Brush s_RedBrush = new Media.SolidColorBrush(Media.Color.FromRgb(224, 91, 77));
    private static readonly Media.Brush s_YellowBrush = new Media.SolidColorBrush(Media.Color.FromRgb(226, 176, 54));

    /// <summary>
    /// Freezes shared status brushes so independently resumed refresh tests and UI reads can safely access them.
    /// </summary>
    static ApiMonitorViewModel()
    {
        s_GreenBrush.Freeze();
        s_RedBrush.Freeze();
        s_YellowBrush.Freeze();
    }

    private string m_Provider;
    public RefreshState BalanceState { get; private set; } = new();
    public RefreshState UsedState { get; private set; } = new();

    [ObservableProperty]
    public partial string StatusTooltip { get; private set; } = string.Empty;

    public event EventHandler? EditingSaved;

    public string Id { get; }

    public string[] ProviderOptions { get; } =
    [
        ApiMonitorSettings.DeepSeekProvider,
        ApiMonitorSettings.OpenRouterProvider,
        ApiMonitorSettings.VercelProvider,
        ApiMonitorSettings.NanoGptProvider,
        ApiMonitorSettings.NewApiProvider,
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string Name { get; set; }

    public string Provider
    {
        get => m_Provider;
        set
        {
            string normalized = value switch
            {
                ApiMonitorSettings.NewApiProvider => ApiMonitorSettings.NewApiProvider,
                ApiMonitorSettings.OpenRouterProvider => ApiMonitorSettings.OpenRouterProvider,
                ApiMonitorSettings.NanoGptProvider => ApiMonitorSettings.NanoGptProvider,
                ApiMonitorSettings.VercelProvider => ApiMonitorSettings.VercelProvider,
                _ => ApiMonitorSettings.DeepSeekProvider,
            };
            string previousProvider = m_Provider;
            if (!SetProperty(ref m_Provider, normalized))
            {
                return;
            }

            if (Name == previousProvider)
            {
                Name = normalized;
            }

            if (BaseUrl.Length == 0 || IsDefaultBaseUrl(BaseUrl))
            {
                BaseUrl = GetDefaultBaseUrl(normalized);
            }

            BalanceDisplay = "N/A";
            BalanceTooltip = string.Empty;
            UsedDisplay = "N/A";
            StatusText = "Waiting for refresh";
            StatusDotBrush = s_RedBrush;
            BalanceState = new();
            UsedState = new();
            StatusTooltip = string.Empty;

            OnPropertyChanged(nameof(IsNewApi));
            OnPropertyChanged(nameof(HasSecondaryDisplay));
            OnPropertyChanged(nameof(PrimaryDisplayLabel));
            OnPropertyChanged(nameof(SecondaryDisplayLabel));
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    [ObservableProperty]
    public partial string BaseUrl { get; set; }

    [ObservableProperty]
    public partial string ApiKey { get; set; }

    [ObservableProperty]
    public partial string UserId { get; set; }

    public bool IsNewApi => m_Provider == ApiMonitorSettings.NewApiProvider;

    public bool HasSecondaryDisplay => IsNewApi || m_Provider == ApiMonitorSettings.NanoGptProvider ||
        (m_Provider is ApiMonitorSettings.OpenRouterProvider or ApiMonitorSettings.NanoGptProvider or ApiMonitorSettings.VercelProvider &&
         !string.IsNullOrEmpty(UsedDisplay) && UsedDisplay != "N/A");

    public string PrimaryDisplayLabel => "Balance:";

    public string SecondaryDisplayLabel => m_Provider == ApiMonitorSettings.NanoGptProvider ? "Used(30d):" : "Used:";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? m_Provider : Name.Trim();

    [ObservableProperty]
    public partial string BalanceDisplay { get; private set; } = "N/A";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBalanceTooltip))]
    public partial string BalanceTooltip { get; private set; } = string.Empty;

    public bool HasBalanceTooltip => !string.IsNullOrWhiteSpace(BalanceTooltip);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecondaryDisplay))]
    public partial string UsedDisplay { get; private set; } = "N/A";

    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    [ObservableProperty]
    public partial bool IsPending { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Waiting for refresh";

    [ObservableProperty]
    public partial Media.Brush StatusDotBrush { get; private set; } = s_RedBrush;

    /// <summary>
    /// Creates an editable API monitor view model.
    /// </summary>
    public ApiMonitorViewModel(ApiMonitorSettings settings, bool isEditing = false, bool isPending = false)
    {
        Id = settings.Id;
        Name = settings.Name;
        m_Provider = settings.Provider;
        BaseUrl = settings.BaseUrl;
        ApiKey = settings.ApiKey;
        UserId = settings.UserId;
        IsEditing = isEditing;
        IsPending = isPending;
    }

    /// <summary>
    /// Creates a persisted settings snapshot from current fields.
    /// </summary>
    public ApiMonitorSettings ToSettings()
    {
        return new ApiMonitorSettings
        {
            Id = Id,
            Name = Name,
            Provider = Provider,
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            UserId = UserId,
        }.Normalize();
    }

    /// <summary>
    /// Returns the default base URL for a provider.
    /// </summary>
    private static string GetDefaultBaseUrl(string provider)
    {
        return provider switch
        {
            ApiMonitorSettings.DeepSeekProvider => "https://api.deepseek.com",
            ApiMonitorSettings.OpenRouterProvider => "https://openrouter.ai",
            ApiMonitorSettings.NanoGptProvider => "https://nano-gpt.com",
            ApiMonitorSettings.VercelProvider => "https://ai-gateway.vercel.sh",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Returns whether a URL is one of the built-in provider defaults.
    /// </summary>
    private static bool IsDefaultBaseUrl(string baseUrl)
    {
        return baseUrl is "https://api.deepseek.com" or "https://openrouter.ai" or "https://nano-gpt.com" or "https://ai-gateway.vercel.sh";
    }

    /// <summary>
    /// Updates the card with a completed usage query.
    /// </summary>
    public void Update(ApiUsageResult result)
    {
        if (result.Provider.Length > 0 && result.Provider != m_Provider)
        {
            return;
        }

        BalanceDisplay = result.BalanceDisplay;
        BalanceTooltip = result.BalanceTooltip;
        UsedDisplay = result.UsedDisplay;
        BalanceState.Update(result.Available, result.UpdatedAt, result.Error);
        bool hasUsage = !string.IsNullOrEmpty(result.UsedDisplay) && result.UsedDisplay != "N/A";
        UsedState.Update(hasUsage, result.UpdatedAt, result.UsedError.Length > 0 ? result.UsedError : result.Error);
        StatusTooltip = $"Balance: {BalanceState.Tooltip}" + (Provider != ApiMonitorSettings.DeepSeekProvider ? $"\nUsage: {UsedState.Tooltip}" : string.Empty);
        StatusText = result.Available
            ? $"Updated {result.UpdatedAt:HH:mm}{(result.UsedError.Length > 0 ? " · Partial" : string.Empty)}"
            : result.Error;
        StatusDotBrush = result.Available ? result.UsedError.Length > 0 ? s_YellowBrush : s_GreenBrush : s_RedBrush;
    }

    /// <summary>
    /// Toggles editing and commits a pending monitor when editing is saved.
    /// </summary>
    [RelayCommand]
    private void ToggleEditing()
    {
        IsEditing = !IsEditing;
        if (IsEditing)
        {
            return;
        }

        IsPending = false;
        EditingSaved?.Invoke(this, EventArgs.Empty);
    }

}
