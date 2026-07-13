using System.Windows;

namespace CodexTray.App;

/// <summary>
/// WPF application host for the tray application.
/// </summary>
internal sealed class App : System.Windows.Application
{
    private readonly EventWaitHandle m_ShowPanelEvent;
    private TrayController? m_Controller;

    /// <summary>
    /// Creates the WPF application host.
    /// </summary>
    public App(EventWaitHandle showPanelEvent)
    {
        m_ShowPanelEvent = showPanelEvent;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    /// <summary>
    /// Starts the tray controller when the application launches.
    /// </summary>
    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        m_Controller = new TrayController(this, m_ShowPanelEvent, Dispatcher);
    }

    /// <summary>
    /// Releases the tray controller when the application exits.
    /// </summary>
    protected override void OnExit(ExitEventArgs args)
    {
        m_Controller?.Dispose();
        m_Controller = null;
        base.OnExit(args);
    }
}
