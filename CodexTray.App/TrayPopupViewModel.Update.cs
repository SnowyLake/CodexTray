using CodexTray.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexTray.App;

internal sealed partial class TrayPopupViewModel
{
    private readonly AppUpdateSession m_UpdateSession = new();
    private AppUpdateRelease? m_AvailableRelease;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateActionIcon))]
    public partial bool HasAvailableUpdate { get; private set; }

    public string UpdateActionIcon => HasAvailableUpdate ? "\uE896" : "\uE117";

    public event EventHandler<AppUpdateHandoff>? UpdateApplyRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunUpdateCommand))]
    public partial bool IsUpdateBusy { get; private set; }

    [ObservableProperty]
    public partial string UpdateActionText { get; private set; } = "Check for updates";

    /// <summary>
    /// Offers the known release or checks GitHub before asking to install.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunUpdate))]
    private Task RunUpdateAsync(CancellationToken cancellationToken)
    {
        if (HasAvailableUpdate)
        {
            ConfirmAvailableUpdate();
            return Task.CompletedTask;
        }

        return RunBusyAsync("Checking...", "Update", token => CheckForUpdateAsync(token), cancellationToken);
    }

    /// <summary>
    /// Checks silently without overlapping a manual operation or changing a pending confirmation.
    /// </summary>
    public Task CheckForUpdateInBackgroundAsync(CancellationToken cancellationToken)
    {
        return IsUpdateBusy || IsInAppDialogOpen || cancellationToken.IsCancellationRequested
            ? Task.CompletedTask
            : RunBusyAsync("Checking...", "Update", token => CheckForUpdateAsync(token, silent: true), cancellationToken, silent: true);
    }

    /// <summary>
    /// Downloads the release chosen in the update dialog and restarts into the updater.
    /// </summary>
    [RelayCommand]
    private Task InstallUpdateAsync(CancellationToken cancellationToken)
    {
        if (m_AvailableRelease == null || IsUpdateBusy)
        {
            return Task.CompletedTask;
        }

        return RunBusyAsync("Downloading...", "Update failed", ApplyAvailableUpdateAsync, cancellationToken);
    }

    /// <summary>
    /// Runs one update operation and shows a dialog when it fails.
    /// </summary>
    private async Task RunBusyAsync(string busyText, string failureTitle, Func<CancellationToken, Task> action, CancellationToken cancellationToken, bool silent = false)
    {
        IsUpdateBusy = true;
        UpdateActionText = busyText;
        try
        {
            await action(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AppUpdateException exception)
        {
            if (!silent)
            {
                ShowUpdateDialog(failureTitle, exception.Message, "OK");
            }
        }
        catch (Exception)
        {
            if (!silent)
            {
                ShowUpdateDialog(failureTitle, "Could not reach GitHub. Check the network and try again.", "OK");
            }
        }
        finally
        {
            IsUpdateBusy = false;
            UpdateActionText = HasAvailableUpdate ? "Install update" : "Check for updates";
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
    private async Task CheckForUpdateAsync(CancellationToken cancellationToken, bool silent = false)
    {
        if (!AppUpdateVersion.TryParseAssemblyVersion(AppVersion, out Version? currentVersion) || currentVersion == null)
        {
            if (!silent)
            {
                ShowUpdateDialog("Update", "This build does not have a release version, so it cannot check for updates.", "OK");
            }
            return;
        }

        string? currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable) || !AppUpdateWork.IsPublishedInstall(currentExecutable))
        {
            if (!silent)
            {
                ShowUpdateDialog("Update", "Updates are unavailable from a development build.", "OK");
            }
            return;
        }

        AppUpdateCheckResult result = await m_UpdateSession.CheckAsync(currentVersion, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        SetAvailableUpdate(result);
        if (silent)
        {
            return;
        }

        if (HasAvailableUpdate)
        {
            ConfirmAvailableUpdate();
            return;
        }

        ShowUpdateDialog("Up to date", "You are on the latest version.", "OK");
    }

    /// <summary>
    /// Publishes the latest successful check to all update indicators.
    /// </summary>
    internal void SetAvailableUpdate(AppUpdateCheckResult result)
    {
        m_AvailableRelease = result.Availability == AppUpdateAvailability.UpdateAvailable ? result.LatestRelease : null;
        HasAvailableUpdate = m_AvailableRelease != null;
        UpdateActionText = HasAvailableUpdate ? "Install update" : "Check for updates";
    }

    /// <summary>
    /// Asks for confirmation before downloading the known release.
    /// </summary>
    private void ConfirmAvailableUpdate()
    {
        if (m_AvailableRelease != null)
        {
            ShowUpdateDialog(
                "Update available",
                $"Version {m_AvailableRelease.Version.ToString(3)} is available. Install it now?",
                "Install",
                "Cancel",
                () => InstallUpdateCommand.Execute(null));
        }
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
