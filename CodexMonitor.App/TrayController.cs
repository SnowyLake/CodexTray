using CodexMonitor.Core;
using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace CodexMonitor.App;

/// <summary>
/// Owns the tray icon, background service, and WPF popup window.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly WpfApplication m_Application;
    private readonly EventWaitHandle m_ShowPanelEvent;
    private readonly Dispatcher m_Dispatcher;
    private readonly SettingsStore m_SettingsStore;
    private readonly CodexMonitorCollector m_Collector;
    private readonly UsageCache m_UsageCache = new();
    private readonly Forms.NotifyIcon m_NotifyIcon;
    private readonly System.Drawing.Icon m_AppIcon;
    private readonly DispatcherTimer m_RefreshTimer;
    private readonly CancellationTokenSource m_SignalCancellation = new();
    private AppSettings m_Settings;
    private LightweightHttpServer? m_Server;
    private TrayPopupWindow? m_TrayPopupWindow;
    private TrayPopupViewModel? m_PopupViewModel;
    private int m_IsRefreshing;
    private bool m_IsExiting;

    /// <summary>
    /// Creates the tray controller and starts background work.
    /// </summary>
    public TrayController(WpfApplication application, EventWaitHandle showPanelEvent, Dispatcher dispatcher)
    {
        m_Application = application;
        m_ShowPanelEvent = showPanelEvent;
        m_Dispatcher = dispatcher;
        m_SettingsStore = new SettingsStore();
        m_Collector = new CodexMonitorCollector();
        m_AppIcon = LoadApplicationIcon();
        bool settingsExists = m_SettingsStore.Exists();
        m_Settings = m_SettingsStore.Load();
        m_NotifyIcon = CreateNotifyIcon();
        m_RefreshTimer = new DispatcherTimer(DispatcherPriority.Background, m_Dispatcher);
        m_RefreshTimer.Tick += async (_, _) => await RefreshUsageAsync();
        StartService();
        ConfigureRefreshTimer();
        _ = RefreshUsageAsync();
        StartSignalListener();
        if (!settingsExists)
        {
            m_SettingsStore.Save(m_Settings);
            m_Dispatcher.BeginInvoke(new Action(ShowPanel));
        }
    }

    /// <summary>
    /// Releases tray resources and stops the background service.
    /// </summary>
    public void Dispose()
    {
        m_SignalCancellation.Cancel();
        m_RefreshTimer.Stop();
        m_Server?.Dispose();
        m_NotifyIcon.Dispose();
        m_AppIcon.Dispose();
        m_SignalCancellation.Dispose();
    }

    /// <summary>
    /// Creates the tray icon and context menu.
    /// </summary>
    private Forms.NotifyIcon CreateNotifyIcon()
    {
        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("Open Panel", null, (_, _) => ShowPanel());
        menu.Items.Add("Refresh Now", null, async (_, _) => await RefreshUsageAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        Forms.NotifyIcon notifyIcon = new()
        {
            ContextMenuStrip = menu,
            Icon = m_AppIcon,
            Text = CodexMonitorDefaults.AppName,
            Visible = true,
        };
        notifyIcon.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                TogglePanel();
            }
        };
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
            m_NotifyIcon.Text = $"{CodexMonitorDefaults.AppName} :{m_Server.Port}";
        }
        catch (SocketException exception)
        {
            m_NotifyIcon.Text = $"{CodexMonitorDefaults.AppName} service failed";
            Forms.MessageBox.Show($"Unable to start CodexMonitor service on port {m_Settings.Port}.\n\n{exception.Message}", CodexMonitorDefaults.AppName, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
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
        RefreshPopupStatus();
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
                if (m_ShowPanelEvent.WaitOne(TimeSpan.FromMilliseconds(500)))
                {
                    m_Dispatcher.BeginInvoke(new Action(ShowPanel));
                }
            }
        }, m_SignalCancellation.Token);
    }

    /// <summary>
    /// Toggles the tray popup from the notification icon.
    /// </summary>
    private void TogglePanel()
    {
        if (m_TrayPopupWindow?.IsVisible == true)
        {
            m_TrayPopupWindow.Hide();
            return;
        }

        ShowPanel();
    }

    /// <summary>
    /// Opens the tray popup on the home page.
    /// </summary>
    private void ShowPanel()
    {
        EnsurePopup();
        m_PopupViewModel?.ShowHome();
        RefreshPopupStatus();
        m_TrayPopupWindow?.ShowNearTray();
        _ = RefreshUsageAsync();
    }

    /// <summary>
    /// Creates the WPF tray popup and wires application callbacks.
    /// </summary>
    private void EnsurePopup()
    {
        if (m_TrayPopupWindow != null)
        {
            return;
        }

        m_PopupViewModel = new TrayPopupViewModel(m_Settings);
        m_PopupViewModel.RefreshRequested += async (_, _) => await RefreshUsageAsync();
        m_PopupViewModel.SaveSettingsRequested += (_, _) => SaveSettings();
        m_PopupViewModel.InstallLiteMonitorPluginRequested += (_, _) => InstallLiteMonitorPlugin();
        m_PopupViewModel.InstallTrafficMonitorPluginRequested += (_, _) => InstallTrafficMonitorPlugin();
        m_PopupViewModel.ExitRequested += (_, _) => ExitApplication();
        m_TrayPopupWindow = new TrayPopupWindow(m_PopupViewModel);
        m_TrayPopupWindow.Closed += (_, _) =>
        {
            m_TrayPopupWindow = null;
            m_PopupViewModel = null;
        };
    }

    /// <summary>
    /// Saves settings and applies startup registration changes.
    /// </summary>
    private void SaveSettings()
    {
        int previousPort = m_Settings.Port;
        if (m_PopupViewModel?.TryApplySettings(out _) == false)
        {
            return;
        }

        StartupManager.SetEnabled(Environment.ProcessPath ?? string.Empty, m_Settings.StartWithWindows);
        m_SettingsStore.Save(m_Settings);
        ConfigureRefreshTimer();
        if (previousPort != m_Settings.Port)
        {
            RestartService();
        }

        RefreshPopupStatus();
        _ = RefreshUsageAsync();
    }

    /// <summary>
    /// Applies the configured settings panel refresh interval.
    /// </summary>
    private void ConfigureRefreshTimer()
    {
        m_Settings.Normalize();
        m_RefreshTimer.Stop();
        m_RefreshTimer.Interval = TimeSpan.FromMinutes(m_Settings.RefreshIntervalMinutes);
        m_RefreshTimer.Start();
    }

    /// <summary>
    /// Installs the LiteMonitor plugin file.
    /// </summary>
    private void InstallLiteMonitorPlugin()
    {
        try
        {
            if (m_PopupViewModel?.TryApplySettings(out _) == false)
            {
                return;
            }

            if (!TryValidateLiteMonitorDirectory(m_Settings.LiteMonitorDir, out string message))
            {
                ShowWarning(message);
                return;
            }

            string targetPath = LiteMonitorPluginInstaller.Install(m_Settings.LiteMonitorDir, m_Settings.Port);
            m_SettingsStore.Save(m_Settings);
            RefreshPopupStatus();
            m_PopupViewModel?.SetMessage($"Installed LiteMonitor plugin: {targetPath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            ShowWarning(exception.Message);
        }
    }

    /// <summary>
    /// Installs the TrafficMonitor plugin file.
    /// </summary>
    private void InstallTrafficMonitorPlugin()
    {
        try
        {
            if (m_PopupViewModel?.TryApplySettings(out _) == false)
            {
                return;
            }

            if (!TryValidateTrafficMonitorDirectory(m_Settings.TrafficMonitorDir, out string message))
            {
                ShowWarning(message);
                return;
            }

            string targetPath = TrafficMonitorPluginInstaller.Install(m_Settings.TrafficMonitorDir, m_Settings.Port);
            m_SettingsStore.Save(m_Settings);
            RefreshPopupStatus();
            m_PopupViewModel?.SetMessage($"Installed TrafficMonitor plugin: {targetPath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            ShowWarning(exception.Message);
        }
    }

    /// <summary>
    /// Updates the settings window with current service data.
    /// </summary>
    private void RefreshPopupStatus()
    {
        if (m_PopupViewModel == null)
        {
            return;
        }

        UsageResponse? response = m_UsageCache.Get();
        m_PopupViewModel.UpdateStatus(m_Server?.IsRunning == true, m_Server?.Port ?? m_Settings.Port, response, m_Server?.LastError);
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

        if (m_PopupViewModel != null)
        {
            m_PopupViewModel.IsRefreshing = true;
        }

        try
        {
            UsageResponse response = await Task.Run(() => m_Collector.Collect()).ConfigureAwait(true);
            m_UsageCache.Update(response);
            RefreshPopupStatus();
        }
        finally
        {
            if (m_PopupViewModel != null)
            {
                m_PopupViewModel.IsRefreshing = false;
            }

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
        m_TrayPopupWindow?.Close();
        m_NotifyIcon.Visible = false;
        m_Server?.Stop();
        m_Application.Shutdown();
    }

    /// <summary>
    /// Validates the configured LiteMonitor installation directory.
    /// </summary>
    private static bool TryValidateLiteMonitorDirectory(string directory, out string message)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            message = "LiteMonitor folder is not configured. Use Browse or Auto Detect first.";
            return false;
        }

        if (!LiteMonitorLocator.IsLiteMonitorDirectory(directory))
        {
            message = $"LiteMonitor.exe was not found in:\n{directory}";
            return false;
        }

        message = string.Empty;
        return true;
    }

    /// <summary>
    /// Shows a warning in the popup or a fallback message box.
    /// </summary>
    private void ShowWarning(string message)
    {
        m_PopupViewModel?.SetMessage(message);
        if (m_TrayPopupWindow?.IsVisible != true)
        {
            Forms.MessageBox.Show(message, CodexMonitorDefaults.AppName, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Validates the configured TrafficMonitor installation directory.
    /// </summary>
    private static bool TryValidateTrafficMonitorDirectory(string directory, out string message)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            message = "TrafficMonitor folder is not configured. Use Browse or Auto Detect first.";
            return false;
        }

        if (!TrafficMonitorLocator.IsTrafficMonitorDirectory(directory))
        {
            message = $"TrafficMonitor.exe was not found in:\n{directory}";
            return false;
        }

        message = string.Empty;
        return true;
    }

    /// <summary>
    /// Loads the application icon from the published resources directory.
    /// </summary>
    private static System.Drawing.Icon LoadApplicationIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "icon.ico");
        return File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }
}
