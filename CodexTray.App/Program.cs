using CodexTray.Core;
using System.Threading;

namespace CodexTray.App;

internal static class Program
{
    private const string k_MutexName = CodexTrayDefaults.SingleInstanceMutexName;
    private const string k_ShowPanelEventName = CodexTrayDefaults.AppName + "ShowPanel";

    /// <summary>
    /// Starts the tray application, applies an update, or signals an existing instance.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (AppUpdateArguments.IsApplyRequest(args))
        {
            return AppUpdateApplyHost.Run(args);
        }

        using Mutex mutex = new(false, k_MutexName, out bool createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            return 0;
        }

        using EventWaitHandle showPanelEvent = new(false, EventResetMode.AutoReset, k_ShowPanelEventName);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        App app = new(showPanelEvent);
        app.Run();
        return 0;
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
