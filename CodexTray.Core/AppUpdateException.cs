namespace CodexTray.Core;

/// <summary>
/// Describes a failed application update with a message safe to show in the UI.
/// </summary>
public sealed class AppUpdateException : Exception
{
    /// <summary>
    /// Creates an update failure. Set <paramref name="canRestartPreviousVersion"/> when the previous files are still usable.
    /// </summary>
    public AppUpdateException(string message, bool canRestartPreviousVersion = false, Exception? innerException = null)
        : base(message, innerException)
    {
        CanRestartPreviousVersion = canRestartPreviousVersion;
    }

    /// <summary>
    /// Gets whether the previous installation can be started again.
    /// </summary>
    public bool CanRestartPreviousVersion { get; }
}
