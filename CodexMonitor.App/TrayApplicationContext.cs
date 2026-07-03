using CodexMonitor.Core;
using System.Diagnostics;
using System.Net.Sockets;

namespace CodexMonitor.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly EventWaitHandle m_ShowSettingsEvent;
    private readonly SettingsStore m_SettingsStore;
    private readonly CodexMonitorCollector m_Collector;
    private readonly UsageCache m_UsageCache = new();
    private readonly Icon m_AppIcon;
    private readonly NotifyIcon m_NotifyIcon;
    private readonly System.Windows.Forms.Timer m_RefreshTimer = new();
    private readonly SynchronizationContext m_SynchronizationContext;
    private readonly CancellationTokenSource m_SignalCancellation = new();
    private AppSettings m_Settings;
    private LightweightHttpServer? m_Server;
    private SettingsForm? m_SettingsForm;
    private int m_IsRefreshing;
    private bool m_IsExiting;

    /// <summary>
    /// Creates the tray application context.
    /// </summary>
    public TrayApplicationContext(EventWaitHandle showSettingsEvent)
    {
        m_ShowSettingsEvent = showSettingsEvent;
        m_SynchronizationContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        m_SettingsStore = new SettingsStore();
        m_Collector = new CodexMonitorCollector();
        m_AppIcon = LoadApplicationIcon();
        m_Settings = m_SettingsStore.Load();
        if (string.IsNullOrWhiteSpace(m_Settings.LiteMonitorDir))
        {
            m_Settings.LiteMonitorDir = LiteMonitorLocator.AutoDetect();
        }

        if (string.IsNullOrWhiteSpace(m_Settings.TrafficMonitorDir))
        {
            m_Settings.TrafficMonitorDir = TrafficMonitorLocator.AutoDetect();
        }

        m_NotifyIcon = CreateNotifyIcon();
        m_RefreshTimer.Tick += async (_, _) => await RefreshUsageAsync();
        StartService();
        ConfigureRefreshTimer();
        _ = RefreshUsageAsync();
        StartSignalListener();
        if (!m_Settings.FirstRunCompleted)
        {
            m_Settings.FirstRunCompleted = true;
            m_SettingsStore.Save(m_Settings);
            ShowSettings();
        }
    }

    /// <summary>
    /// Releases tray resources and stops the background service.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            m_SignalCancellation.Cancel();
            m_Server?.Dispose();
            m_RefreshTimer.Dispose();
            m_NotifyIcon.Dispose();
            m_AppIcon.Dispose();
            m_SignalCancellation.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Creates the tray icon and context menu.
    /// </summary>
    private NotifyIcon CreateNotifyIcon()
    {
        ContextMenuStrip menu = new();
        menu.Items.Add("Open Settings", null, (_, _) => ShowSettings());
        menu.Items.Add("Install LiteMonitor Plugin", null, (_, _) => InstallLiteMonitorPlugin());
        menu.Items.Add("Install TrafficMonitor Plugin", null, (_, _) => InstallTrafficMonitorPlugin());
        menu.Items.Add("Open LiteMonitor Folder", null, (_, _) => OpenLiteMonitorFolder());
        menu.Items.Add("Open TrafficMonitor Folder", null, (_, _) => OpenTrafficMonitorFolder());
        menu.Items.Add("Restart Service", null, (_, _) => RestartService());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        NotifyIcon notifyIcon = new()
        {
            ContextMenuStrip = menu,
            Icon = m_AppIcon,
            Text = "CodexMonitor",
            Visible = true,
        };
        notifyIcon.DoubleClick += (_, _) => ShowSettings();
        return notifyIcon;
    }

    /// <summary>
    /// Starts the local HTTP service.
    /// </summary>
    private void StartService()
    {
        try
        {
            m_Server = new LightweightHttpServer(m_UsageCache, m_Settings.Port);
            m_Server.Start();
            m_NotifyIcon.Text = $"CodexMonitor :{m_Server.Port}";
        }
        catch (SocketException exception)
        {
            m_NotifyIcon.Text = "CodexMonitor service failed";
            MessageBox.Show($"Unable to start CodexMonitor service on port {m_Settings.Port}.\n\n{exception.Message}", "CodexMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Restarts the local HTTP service.
    /// </summary>
    private void RestartService()
    {
        m_Server?.Dispose();
        m_Server = null;
        StartService();
        RefreshSettingsStatus();
    }

    /// <summary>
    /// Starts a background listener for second-instance requests.
    /// </summary>
    private void StartSignalListener()
    {
        Task.Run(() =>
        {
            while (!m_SignalCancellation.IsCancellationRequested)
            {
                if (m_ShowSettingsEvent.WaitOne(TimeSpan.FromMilliseconds(500)))
                {
                    m_SynchronizationContext.Post(_ => ShowSettings(), null);
                }
            }
        }, m_SignalCancellation.Token);
    }

    /// <summary>
    /// Opens or focuses the settings window.
    /// </summary>
    private void ShowSettings()
    {
        if (m_SettingsForm != null && !m_SettingsForm.IsDisposed)
        {
            m_SettingsForm.WindowState = FormWindowState.Normal;
            m_SettingsForm.Show();
            m_SettingsForm.Activate();
            RefreshSettingsStatus();
            return;
        }

        m_SettingsForm = new SettingsForm(m_Settings, m_AppIcon);
        m_SettingsForm.SettingsSaved += (_, args) => SaveSettings(args.PreviousPort);
        m_SettingsForm.InstallLiteMonitorPluginRequested += (_, _) => InstallLiteMonitorPlugin();
        m_SettingsForm.InstallTrafficMonitorPluginRequested += (_, _) => InstallTrafficMonitorPlugin();
        m_SettingsForm.RefreshNowRequested += async (_, _) => await RefreshUsageAsync();
        m_SettingsForm.FormClosed += (_, _) => m_SettingsForm = null;
        m_SettingsForm.Show();
        RefreshSettingsStatus();
        _ = RefreshUsageAsync();
    }

    /// <summary>
    /// Saves settings and applies startup registration changes.
    /// </summary>
    private void SaveSettings(int previousPort)
    {
        StartupManager.SetEnabled(Application.ExecutablePath, m_Settings.StartWithWindows);
        m_SettingsStore.Save(m_Settings);
        ConfigureRefreshTimer();
        if (previousPort != m_Settings.Port)
        {
            RestartService();
        }

        RefreshSettingsStatus();
        _ = RefreshUsageAsync();
    }

    /// <summary>
    /// Applies the configured settings panel refresh interval.
    /// </summary>
    private void ConfigureRefreshTimer()
    {
        m_Settings.Normalize();
        m_RefreshTimer.Stop();
        m_RefreshTimer.Interval = m_Settings.RefreshIntervalMinutes * 60 * 1000;
        m_RefreshTimer.Start();
    }

    /// <summary>
    /// Installs the LiteMonitor plugin file.
    /// </summary>
    private void InstallLiteMonitorPlugin()
    {
        try
        {
            if (!LiteMonitorLocator.IsLiteMonitorDirectory(m_Settings.LiteMonitorDir))
            {
                m_Settings.LiteMonitorDir = LiteMonitorLocator.AutoDetect(m_Settings.LiteMonitorDir);
                m_SettingsStore.Save(m_Settings);
            }

            string targetPath = LiteMonitorPluginInstaller.Install(m_Settings.LiteMonitorDir);
            RefreshSettingsStatus();
            MessageBox.Show($"Installed LiteMonitor plugin:\n{targetPath}", "CodexMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            MessageBox.Show(exception.Message, "CodexMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Installs the TrafficMonitor plugin file.
    /// </summary>
    private void InstallTrafficMonitorPlugin()
    {
        try
        {
            if (!TrafficMonitorLocator.IsTrafficMonitorDirectory(m_Settings.TrafficMonitorDir))
            {
                m_Settings.TrafficMonitorDir = TrafficMonitorLocator.AutoDetect(m_Settings.TrafficMonitorDir);
                m_SettingsStore.Save(m_Settings);
            }

            string targetPath = TrafficMonitorPluginInstaller.Install(m_Settings.TrafficMonitorDir, m_Settings.Port);
            RefreshSettingsStatus();
            MessageBox.Show($"Installed TrafficMonitor plugin:\n{targetPath}", "CodexMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            MessageBox.Show(exception.Message, "CodexMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Opens the LiteMonitor installation folder.
    /// </summary>
    private void OpenLiteMonitorFolder()
    {
        if (!LiteMonitorLocator.IsLiteMonitorDirectory(m_Settings.LiteMonitorDir))
        {
            MessageBox.Show("LiteMonitor folder is not configured.", "CodexMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = m_Settings.LiteMonitorDir,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Updates the settings window with current service data.
    /// </summary>
    private void RefreshSettingsStatus()
    {
        if (m_SettingsForm == null || m_SettingsForm.IsDisposed)
        {
            return;
        }

        UsageResponse? response = m_UsageCache.Get();
        m_SettingsForm.UpdateStatus(m_Server?.IsRunning == true, m_Server?.Port ?? m_Settings.Port, response, m_Server?.LastError);
    }

    /// <summary>
    /// Opens the TrafficMonitor installation folder.
    /// </summary>
    private void OpenTrafficMonitorFolder()
    {
        if (!TrafficMonitorLocator.IsTrafficMonitorDirectory(m_Settings.TrafficMonitorDir))
        {
            MessageBox.Show("TrafficMonitor folder is not configured.", "CodexMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = m_Settings.TrafficMonitorDir,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Collects fresh usage data and publishes it to the cache.
    /// </summary>
    private async Task RefreshUsageAsync()
    {
        if (Interlocked.Exchange(ref m_IsRefreshing, 1) == 1)
        {
            return;
        }

        try
        {
            UsageResponse response = await Task.Run(() => m_Collector.Collect()).ConfigureAwait(true);
            m_UsageCache.Update(response);
            RefreshSettingsStatus();
        }
        finally
        {
            Interlocked.Exchange(ref m_IsRefreshing, 0);
        }
    }

    /// <summary>
    /// Stops the service and exits the tray application.
    /// </summary>
    private void ExitApplication()
    {
        if (m_IsExiting)
        {
            return;
        }

        m_IsExiting = true;
        m_NotifyIcon.Visible = false;
        m_Server?.Stop();
        ExitThread();
    }

    /// <summary>
    /// Loads the application icon from embedded resources.
    /// </summary>
    private static Icon LoadApplicationIcon()
    {
        Stream? stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("CodexMonitor.App.Resources.icon.ico");
        return stream == null ? (Icon)SystemIcons.Application.Clone() : new Icon(stream);
    }
}
