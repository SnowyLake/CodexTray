using CodexTray.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexTray.App;

internal sealed partial class TrayPopupViewModel
{
    private readonly AppUpdateSession m_UpdateSession = new();
    private AppUpdateRelease? m_AvailableRelease;

    public event EventHandler<AppUpdateHandoff>? UpdateApplyRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunUpdateCommand))]
    public partial bool IsUpdateBusy { get; private set; }

    [ObservableProperty]
    public partial string UpdateActionText { get; private set; } = "Check for updates";

    /// <summary>
    /// Checks GitHub for a newer stable release and asks before installing it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunUpdate))]
    private async Task RunUpdateAsync(CancellationToken cancellationToken)
    {
        IsUpdateBusy = true;
        UpdateActionText = "Checking...";
        try
        {
            await CheckForUpdateAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AppUpdateException exception)
        {
            ShowUpdateDialog("Update", exception.Message, "OK");
        }
        catch (Exception)
        {
            ShowUpdateDialog("Update", "Could not reach GitHub. Check the network and try again.", "OK");
        }
        finally
        {
            IsUpdateBusy = false;
            UpdateActionText = "Check for updates";
        }
    }

    /// <summary>
    /// Downloads the release chosen in the update dialog and restarts into the updater.
    /// </summary>
    [RelayCommand]
    private async Task InstallUpdateAsync(CancellationToken cancellationToken)
    {
        if (m_AvailableRelease == null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        UpdateActionText = "Downloading...";
        try
        {
            await ApplyAvailableUpdateAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AppUpdateException exception)
        {
            ShowUpdateDialog("Update failed", exception.Message, "OK");
        }
        catch (Exception)
        {
            ShowUpdateDialog("Update failed", "Could not reach GitHub. Check the network and try again.", "OK");
        }
        finally
        {
            IsUpdateBusy = false;
            UpdateActionText = "Check for updates";
        }
    }

    /// <summary>
    /// Returns true while an update check or download is not already running.
    /// </summary>
    private bool CanRunUpdate()
    {
        return !IsUpdateBusy;
    }

    /// <summary>
    /// Compares this build with the latest stable GitHub release.
    /// </summary>
    private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        if (!AppUpdateVersion.TryParseAssemblyVersion(AppVersion, out Version? currentVersion) || currentVersion == null)
        {
            ShowUpdateDialog("Update", "This build does not have a release version, so it cannot check for updates.", "OK");
            return;
        }

        string? currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable) || !AppUpdateWork.IsPublishedInstall(currentExecutable))
        {
            ShowUpdateDialog("Update", "Updates are unavailable from a development build.", "OK");
            return;
        }

        AppUpdateCheckResult result = await m_UpdateSession.CheckAsync(currentVersion, cancellationToken).ConfigureAwait(true);
        if (result.Availability == AppUpdateAvailability.UpdateAvailable)
        {
            m_AvailableRelease = result.LatestRelease;
            ShowUpdateDialog(
                "Update available",
                $"Version {result.LatestRelease.Version.ToString(3)} is available. Install it now?",
                "Install",
                "Cancel",
                () => InstallUpdateCommand.Execute(null));
            return;
        }

        m_AvailableRelease = null;
        ShowUpdateDialog("Up to date", "You are on the latest version.", "OK");
    }

    /// <summary>
    /// Downloads the available release and asks the tray controller to exit into the updater.
    /// </summary>
    private async Task ApplyAvailableUpdateAsync(CancellationToken cancellationToken)
    {
        if (m_AvailableRelease == null)
        {
            return;
        }

        string? currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable))
        {
            throw new AppUpdateException("Updates can only be applied to CodexTray.exe.");
        }

        AppUpdateHandoff handoff = await m_UpdateSession.PrepareAsync(m_AvailableRelease, currentExecutable, cancellationToken).ConfigureAwait(true);
        if (UpdateApplyRequested == null)
        {
            throw new AppUpdateException("The update could not be started.");
        }

        UpdateActionText = "Restarting...";
        UpdateApplyRequested.Invoke(this, handoff);
    }

    /// <summary>
    /// Shows an update result in the existing in-window dialog.
    /// </summary>
    private void ShowUpdateDialog(string title, string message, string primaryButton, string? secondaryButton = null, Action? primaryAction = null)
    {
        InAppDialogRequested?.Invoke(new InAppDialogRequest(title, message, primaryButton, secondaryButton, primaryAction));
    }
}
