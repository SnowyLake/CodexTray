using CodexTray.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Media = System.Windows.Media;

namespace CodexTray.App;

internal sealed partial class ApiMonitorViewModel : ObservableObject
{
    private static readonly Media.Brush s_GreenBrush = new Media.SolidColorBrush(Media.Color.FromRgb(26, 188, 137));
    private static readonly Media.Brush s_RedBrush = new Media.SolidColorBrush(Media.Color.FromRgb(224, 91, 77));

    private string m_Provider;
    private string m_GrokOAuthSource;

    public event EventHandler? EditingSaved;

    public string Id { get; }

    public string[] ProviderOptions { get; } =
    [
        ApiMonitorSettings.DeepSeekProvider,
        ApiMonitorSettings.GrokProvider,
        ApiMonitorSettings.NanoGptProvider,
        ApiMonitorSettings.NewApiProvider,
        ApiMonitorSettings.OpenRouterProvider,
    ];

    public string[] GrokOAuthSourceOptions { get; } =
    [
        ApiMonitorSettings.GrokBuildOAuthSource,
        ApiMonitorSettings.OpenCodeOAuthSource,
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
                ApiMonitorSettings.GrokProvider => ApiMonitorSettings.GrokProvider,
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

            OnPropertyChanged(nameof(IsNewApi));
            OnPropertyChanged(nameof(IsGrok));
            OnPropertyChanged(nameof(IsLocalSessionAuth));
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

    public string GrokOAuthSource
    {
        get => m_GrokOAuthSource;
        set
        {
            string normalized = string.Equals(value, ApiMonitorSettings.OpenCodeOAuthSource, StringComparison.OrdinalIgnoreCase)
                ? ApiMonitorSettings.OpenCodeOAuthSource
                : ApiMonitorSettings.GrokBuildOAuthSource;
            SetProperty(ref m_GrokOAuthSource, normalized);
        }
    }

    public bool IsNewApi => m_Provider == ApiMonitorSettings.NewApiProvider;

    public bool IsGrok => m_Provider == ApiMonitorSettings.GrokProvider;

    public bool IsLocalSessionAuth => IsGrok;

    public bool HasSecondaryDisplay => IsNewApi || IsGrok ||
        (m_Provider is ApiMonitorSettings.OpenRouterProvider or ApiMonitorSettings.NanoGptProvider &&
         !string.IsNullOrEmpty(UsedDisplay) && UsedDisplay != "N/A");

    public string PrimaryDisplayLabel => "Balance:";

    public string SecondaryDisplayLabel => IsGrok ? "Resets:" : m_Provider == ApiMonitorSettings.NanoGptProvider ? "30D Used:" : "Used:";

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
        m_GrokOAuthSource = settings.GrokOAuthSource;
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
            GrokOAuthSource = GrokOAuthSource,
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
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Returns whether a URL is one of the built-in provider defaults.
    /// </summary>
    private static bool IsDefaultBaseUrl(string baseUrl)
    {
        return baseUrl is "https://api.deepseek.com" or "https://openrouter.ai" or "https://nano-gpt.com";
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
        StatusText = result.Available
            ? $"Updated {result.UpdatedAt:HH:mm}"
            : result.Error;
        StatusDotBrush = result.Available ? s_GreenBrush : s_RedBrush;
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
