using System.Threading;

namespace CodexMonitor.App;

internal static class Program
{
    private const string k_MutexName = "CodexUsageLiteMonitorTrayMutex";
    private const string k_ShowSettingsEventName = "CodexUsageLiteMonitorTrayShowSettings";

    /// <summary>
    /// Starts the tray application or signals an existing instance.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        using Mutex mutex = new(false, k_MutexName, out bool createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            return;
        }

        using EventWaitHandle showSettingsEvent = new(false, EventResetMode.AutoReset, k_ShowSettingsEventName);
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext(showSettingsEvent));
    }

    /// <summary>
    /// Requests the existing instance to open its settings window.
    /// </summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using EventWaitHandle existingEvent = EventWaitHandle.OpenExisting(k_ShowSettingsEventName);
            existingEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }
}
