using CodexTray.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Forms = System.Windows.Forms;

namespace CodexTray.App;

/// <summary>
/// Starts the temporary executable that applies a downloaded release.
/// </summary>
internal static class AppUpdateProcess
{
    /// <summary>
    /// Starts the staged executable and asks it to wait for this process to exit.
    /// </summary>
    public static void Start(AppUpdateHandoff handoff, int currentProcessId)
    {
        DateTimeOffset parentStartedUtc = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();
        AppUpdateApplyRequest request = new(
            currentProcessId,
            parentStartedUtc,
            handoff.SourceDirectory,
            handoff.TargetDirectory,
            handoff.RestartExecutablePath);
        ProcessStartInfo startInfo = new()
        {
            FileName = handoff.ApplyExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(handoff.ApplyExecutablePath) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in AppUpdateArguments.Build(request))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new AppUpdateException("The update could not be started.");
    }
}

/// <summary>
/// Applies an update from a temporary copy of the new executable and restarts the installation.
/// </summary>
internal static class AppUpdateApplyHost
{
    /// <summary>
    /// Runs the apply-update process and returns its exit code.
    /// </summary>
    public static int Run(string[] args)
    {
        if (!AppUpdateArguments.TryParse(args, out AppUpdateApplyRequest? request, out string error) || request == null)
        {
            Show(error);
            return 1;
        }

        string backupDirectory = Path.Combine(
            Path.GetTempPath(),
            CodexTrayDefaults.AppName,
            "update",
            "backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            WaitForParentExit(request);
            EnsureRestartPath(request);
            using (AcquireSingleInstanceMutex())
            {
                AppUpdateInstaller.Apply(request.SourceDirectory, request.TargetDirectory, backupDirectory);
                AppUpdateWork.DeleteQuietly(request.SourceDirectory);
            }

            try
            {
                StartInstalledExecutable(request.RestartExecutablePath, request.TargetDirectory);
            }
            catch (Exception exception)
            {
                try
                {
                    using (AcquireSingleInstanceMutex())
                    {
                        AppUpdateInstaller.Restore(backupDirectory, request.TargetDirectory);
                    }
                }
                catch (AppUpdateException)
                {
                    throw;
                }
                catch (Exception restoreException)
                {
                    throw new AppUpdateException(
                        "The new version could not be started and the previous version could not be restored. Install the release zip manually.",
                        canRestartPreviousVersion: false,
                        restoreException);
                }

                throw new AppUpdateException(
                    "The new version could not be started. CodexTray was restored to the previous version.",
                    canRestartPreviousVersion: true,
                    exception);
            }

            AppUpdateWork.DeleteQuietly(backupDirectory);
            return 0;
        }
        catch (AppUpdateException exception)
        {
            Show(exception.Message);
            if (exception.CanRestartPreviousVersion && !IsParentAlive(request))
            {
                TryStartInstalledExecutable(request.RestartExecutablePath, request.TargetDirectory);
            }

            return 1;
        }
        catch (Exception exception)
        {
            Show($"The update failed. {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Requires the restart path to be the executable inside the installation directory.
    /// </summary>
    private static void EnsureRestartPath(AppUpdateApplyRequest request)
    {
        string installedExecutable = Path.Combine(Path.GetFullPath(request.TargetDirectory), CodexTrayDefaults.AppName + ".exe");
        string restartExecutable = Path.GetFullPath(request.RestartExecutablePath);
        if (!string.Equals(installedExecutable, restartExecutable, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppUpdateException("The update restart path is not the installed CodexTray.exe.");
        }
    }

    /// <summary>
    /// Waits until the process that started this update exits. A reused PID is treated as already gone.
    /// </summary>
    private static void WaitForParentExit(AppUpdateApplyRequest request)
    {
        if (request.WaitForProcessId <= 0)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(request.WaitForProcessId);
            if (!IsSameProcess(process, request.ParentStartedUtc))
            {
                return;
            }

            if (!process.WaitForExit(CodexTrayDefaults.UpdateProcessWaitTimeoutSeconds * 1000))
            {
                throw new AppUpdateException("CodexTray did not exit in time, so the update was not applied.");
            }
        }
        catch (ArgumentException)
        {
        }
        catch (AppUpdateException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AppUpdateException("CodexTray did not exit in time, so the update was not applied.", innerException: exception);
        }
    }

    /// <summary>
    /// Returns true when the process that started this update is still running.
    /// </summary>
    private static bool IsParentAlive(AppUpdateApplyRequest request)
    {
        if (request.WaitForProcessId <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(request.WaitForProcessId);
            return IsSameProcess(process, request.ParentStartedUtc) && !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the process is the one that launched the updater.
    /// </summary>
    private static bool IsSameProcess(Process process, DateTimeOffset parentStartedUtc)
    {
        try
        {
            TimeSpan difference = process.StartTime.ToUniversalTime() - parentStartedUtc.UtcDateTime;
            return difference.Duration() <= TimeSpan.FromSeconds(2);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Holds the single-instance mutex so a relaunch cannot open the files being replaced.
    /// </summary>
    private static Mutex AcquireSingleInstanceMutex()
    {
        const int attempts = 20;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            Mutex mutex = new(false, CodexTrayDefaults.SingleInstanceMutexName, out bool createdNew);
            if (createdNew)
            {
                return mutex;
            }

            mutex.Dispose();
            if (attempt == attempts)
            {
                break;
            }

            Thread.Sleep(100);
        }

        throw new AppUpdateException(
            "Another CodexTray instance is running, so the update was not applied.",
            canRestartPreviousVersion: true);
    }

    /// <summary>
    /// Starts the installed executable in the installation directory.
    /// </summary>
    private static void StartInstalledExecutable(string executablePath, string workingDirectory)
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("The process did not start.");
    }

    /// <summary>
    /// Starts the installed executable and reports a failure without leaving the update process.
    /// </summary>
    private static void TryStartInstalledExecutable(string executablePath, string workingDirectory)
    {
        try
        {
            StartInstalledExecutable(executablePath, workingDirectory);
        }
        catch (Exception exception)
        {
            Show($"CodexTray could not restart. {exception.Message}");
        }
    }

    /// <summary>
    /// Shows a failure from the update process, which has no main window.
    /// </summary>
    private static void Show(string message)
    {
        Forms.MessageBox.Show(message, CodexTrayDefaults.AppName, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
    }
}
