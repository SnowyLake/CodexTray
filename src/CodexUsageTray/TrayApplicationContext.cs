using CodexUsage.Core;
using System.Diagnostics;
using System.Net.Sockets;

namespace CodexUsageTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly EventWaitHandle m_ShowSettingsEvent;
    private readonly SettingsStore m_SettingsStore;
    private readonly CodexUsageCollector m_Collector;
    private readonly NotifyIcon m_NotifyIcon;
    private readonly SynchronizationContext m_SynchronizationContext;
    private readonly CancellationTokenSource m_SignalCancellation = new();
    private AppSettings m_Settings;
    private LightweightHttpServer? m_Server;
    private SettingsForm? m_SettingsForm;
    private bool m_IsExiting;

    /// <summary>
    /// Creates the tray application context.
    /// </summary>
    public TrayApplicationContext(EventWaitHandle showSettingsEvent)
    {
        m_ShowSettingsEvent = showSettingsEvent;
        m_SynchronizationContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        m_SettingsStore = new SettingsStore();
        m_Collector = new CodexUsageCollector();
        m_Settings = m_SettingsStore.Load();
        if (string.IsNullOrWhiteSpace(m_Settings.LiteMonitorDir))
        {
            m_Settings.LiteMonitorDir = LiteMonitorLocator.AutoDetect();
        }

        m_NotifyIcon = CreateNotifyIcon();
        StartService();
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
            m_NotifyIcon.Dispose();
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
        menu.Items.Add("Install LiteMonitor Plugin", null, (_, _) => InstallPlugin());
        menu.Items.Add("Open LiteMonitor Folder", null, (_, _) => OpenLiteMonitorFolder());
        menu.Items.Add("Restart Service", null, (_, _) => RestartService());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        NotifyIcon notifyIcon = new()
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Application,
            Text = "CodexUsage LiteMonitor",
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
            m_Server = new LightweightHttpServer(m_Collector, CodexUsageCollector.GetDefaultCodexDirectory(), m_Settings.Port);
            m_Server.Start();
            m_NotifyIcon.Text = $"CodexUsage :{m_Server.Port}";
        }
        catch (SocketException exception)
        {
            m_NotifyIcon.Text = "CodexUsage service failed";
            MessageBox.Show($"Unable to start CodexUsage service on port {m_Settings.Port}.\n\n{exception.Message}", "CodexUsage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        m_SettingsForm = new SettingsForm(m_Settings);
        m_SettingsForm.SettingsSaved += (_, args) => SaveSettings(args.PreviousPort);
        m_SettingsForm.InstallPluginRequested += (_, _) => InstallPlugin();
        m_SettingsForm.FormClosed += (_, _) => m_SettingsForm = null;
        m_SettingsForm.Show();
        RefreshSettingsStatus();
    }

    /// <summary>
    /// Saves settings and applies startup registration changes.
    /// </summary>
    private void SaveSettings(int previousPort)
    {
        StartupManager.SetEnabled(Application.ExecutablePath, m_Settings.StartWithWindows);
        m_SettingsStore.Save(m_Settings);
        if (previousPort != m_Settings.Port)
        {
            RestartService();
        }

        RefreshSettingsStatus();
    }

    /// <summary>
    /// Installs the LiteMonitor plugin file.
    /// </summary>
    private void InstallPlugin()
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
            MessageBox.Show($"Installed LiteMonitor plugin:\n{targetPath}", "CodexUsage", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            MessageBox.Show(exception.Message, "CodexUsage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Opens the LiteMonitor installation folder.
    /// </summary>
    private void OpenLiteMonitorFolder()
    {
        if (!LiteMonitorLocator.IsLiteMonitorDirectory(m_Settings.LiteMonitorDir))
        {
            MessageBox.Show("LiteMonitor folder is not configured.", "CodexUsage", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        UsageResponse? response = null;
        try
        {
            response = m_Collector.Collect();
        }
        catch (IOException)
        {
        }

        m_SettingsForm.UpdateStatus(m_Server?.IsRunning == true, m_Server?.Port ?? m_Settings.Port, response, m_Server?.LastError);
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
}
