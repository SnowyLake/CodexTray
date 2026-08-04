using CodexTray.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Media = System.Windows.Media;

namespace CodexTray.App;

internal sealed partial class ApiMonitorViewModel : ObservableObject
{
    private static readonly Media.Brush s_GreenBrush = new Media.SolidColorBrush(Media.Color.FromRgb(26, 188, 137));
    private static readonly Media.Brush s_RedBrush = new Media.SolidColorBrush(Media.Color.FromRgb(224, 91, 77));

    private string m_Name;
    private string m_Provider;
    private string m_BaseUrl;
    private string m_ApiKey;
    private string m_UserId;
    private string m_GrokOAuthSource;
    private string m_BalanceDisplay = "N/A";
    private string m_BalanceTooltip = string.Empty;
    private string m_UsedDisplay = "N/A";
    private string m_StatusText = "Waiting for refresh";
    private Media.Brush m_StatusDotBrush = s_RedBrush;
    private bool m_IsEditing;
    private bool m_IsPending;

    public event EventHandler? EditingSaved;

    public string Id { get; }

    public string[] ProviderOptions { get; } =
    [
        ApiMonitorSettings.DeepSeekProvider,
        ApiMonitorSettings.GrokProvider,
        ApiMonitorSettings.NewApiProvider,
    ];

    public string[] GrokOAuthSourceOptions { get; } =
    [
        ApiMonitorSettings.GrokBuildOAuthSource,
        ApiMonitorSettings.OpenCodeOAuthSource,
    ];

    public string Name
    {
        get => m_Name;
        set
        {
            if (SetProperty(ref m_Name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string Provider
    {
        get => m_Provider;
        set
        {
            string normalized = value switch
            {
                ApiMonitorSettings.NewApiProvider => ApiMonitorSettings.NewApiProvider,
                ApiMonitorSettings.GrokProvider => ApiMonitorSettings.GrokProvider,
                _ => ApiMonitorSettings.DeepSeekProvider,
            };
            string previousProvider = m_Provider;
            if (!SetProperty(ref m_Provider, normalized))
            {
                return;
            }

            if (m_Name == previousProvider)
            {
                Name = normalized;
            }

            if (m_BaseUrl.Length == 0 || m_BaseUrl == "https://api.deepseek.com")
            {
                BaseUrl = normalized == ApiMonitorSettings.DeepSeekProvider ? "https://api.deepseek.com" : string.Empty;
            }

            OnPropertyChanged(nameof(IsNewApi));
            OnPropertyChanged(nameof(IsGrok));
            OnPropertyChanged(nameof(IsLocalSessionAuth));
            OnPropertyChanged(nameof(HasSecondaryDisplay));
            OnPropertyChanged(nameof(PrimaryDisplayLabel));
            OnPropertyChanged(nameof(SecondaryDisplayLabel));
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string BaseUrl
    {
        get => m_BaseUrl;
        set => SetProperty(ref m_BaseUrl, value);
    }

    public string ApiKey
    {
        get => m_ApiKey;
        set => SetProperty(ref m_ApiKey, value);
    }

    public string UserId
    {
        get => m_UserId;
        set => SetProperty(ref m_UserId, value);
    }

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

    public bool HasSecondaryDisplay => IsNewApi || IsGrok;

    public string PrimaryDisplayLabel => "Balance:";

    public string SecondaryDisplayLabel => IsGrok ? "Resets:" : "Used:";

    public string DisplayName => string.IsNullOrWhiteSpace(m_Name) ? m_Provider : m_Name.Trim();

    public string BalanceDisplay
    {
        get => m_BalanceDisplay;
        private set => SetProperty(ref m_BalanceDisplay, value);
    }

    public string BalanceTooltip
    {
        get => m_BalanceTooltip;
        private set => SetProperty(ref m_BalanceTooltip, value);
    }

    public bool HasBalanceTooltip => !string.IsNullOrWhiteSpace(m_BalanceTooltip);

    public string UsedDisplay
    {
        get => m_UsedDisplay;
        private set => SetProperty(ref m_UsedDisplay, value);
    }

    public bool IsEditing
    {
        get => m_IsEditing;
        private set => SetProperty(ref m_IsEditing, value);
    }

    public bool IsPending
    {
        get => m_IsPending;
        private set => SetProperty(ref m_IsPending, value);
    }

    public string StatusText
    {
        get => m_StatusText;
        private set => SetProperty(ref m_StatusText, value);
    }

    public Media.Brush StatusDotBrush
    {
        get => m_StatusDotBrush;
        private set => SetProperty(ref m_StatusDotBrush, value);
    }

    /// <summary>
    /// Creates an editable API monitor view model.
    /// </summary>
    public ApiMonitorViewModel(ApiMonitorSettings settings, bool isEditing = false, bool isPending = false)
    {
        Id = settings.Id;
        m_Name = settings.Name;
        m_Provider = settings.Provider;
        m_BaseUrl = settings.BaseUrl;
        m_ApiKey = settings.ApiKey;
        m_UserId = settings.UserId;
        m_GrokOAuthSource = settings.GrokOAuthSource;
        m_IsEditing = isEditing;
        m_IsPending = isPending;
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
    /// Updates the card with a completed usage query.
    /// </summary>
    public void Update(ApiUsageResult result)
    {
        BalanceDisplay = result.BalanceDisplay;
        BalanceTooltip = result.BalanceTooltip;
        OnPropertyChanged(nameof(HasBalanceTooltip));
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
