using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexTray.App;

/// <summary>
/// Retains the last successful timestamp while exposing the current read failure without retaining old values.
/// </summary>
internal sealed partial class RefreshState : ObservableObject
{
    public DateTimeOffset? LastSuccess { get; private set; }

    public string Error { get; private set; } = string.Empty;

    public string Tooltip => $"Last success: {(LastSuccess.HasValue ? LastSuccess.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "Never")}"
        + (Error.Length > 0 ? $"\nUnavailable: {Error}" : string.Empty);

    /// <summary>
    /// Records an outcome without advancing the last successful time on failure.
    /// </summary>
    public void Update(bool available, DateTimeOffset timestamp, string? error = null)
    {
        if (available)
        {
            LastSuccess = timestamp;
        }

        Error = available ? string.Empty : string.IsNullOrWhiteSpace(error) ? "Data could not be read" : error;
        OnPropertyChanged(nameof(Tooltip));
    }
}
