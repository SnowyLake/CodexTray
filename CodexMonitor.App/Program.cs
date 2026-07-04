using CodexMonitor.Core;
using System.Threading;

namespace CodexMonitor.App;

internal static class Program
{
    private const string k_MutexName = CodexMonitorDefaults.AppName + "TrayMutex";
    private const string k_ShowPanelEventName = CodexMonitorDefaults.AppName + "TrayShowPanel";

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

        using EventWaitHandle showPanelEvent = new(false, EventResetMode.AutoReset, k_ShowPanelEventName);
        ApplicationConfiguration.Initialize();
        App app = new(showPanelEvent);
        app.Run();
    }

    /// <summary>
    /// Requests the existing instance to open its settings window.
    /// </summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using EventWaitHandle existingEvent = EventWaitHandle.OpenExisting(k_ShowPanelEventName);
            existingEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }
}
