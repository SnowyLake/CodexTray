using CodexTray.Core;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace CodexTray.App;

/// <summary>
/// Owns the tray icon, background service, and WPF popup window.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly WpfApplication m_Application;
    private readonly EventWaitHandle m_ShowPanelEvent;
    private readonly Dispatcher m_Dispatcher;
    private readonly SettingsStore m_SettingsStore;
    private readonly CodexTrayCollector m_Collector;
    private readonly TokenCostCollector m_TokenCostCollector;
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
    private bool m_StartupDetectingLiteMonitor;
    private bool m_StartupDetectingTrafficMonitor;

    /// <summary>
    /// Creates the tray controller and starts background work.
    /// </summary>
    public TrayController(WpfApplication application, EventWaitHandle showPanelEvent, Dispatcher dispatcher)
    {
        m_Application = application;
        m_ShowPanelEvent = showPanelEvent;
        m_Dispatcher = dispatcher;
        m_SettingsStore = new SettingsStore();
        m_Collector = new CodexTrayCollector();
        m_TokenCostCollector = new TokenCostCollector();
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
        SyncStartupRegistration();
        if (!settingsExists)
        {
            m_SettingsStore.Save(m_Settings);
            m_Dispatcher.BeginInvoke(new Action(() => ShowPanel()));
        }

        _ = AutoDetectMissingPluginPathsAsync();
    }

    /// <summary>
    /// Re-registers startup when settings request it but the Run key no longer matches the current exe.
    /// </summary>
    private void SyncStartupRegistration()
    {
        if (!m_Settings.StartWithWindows)
        {
            return;
        }

        string executablePath = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath) || StartupManager.IsEnabled(executablePath))
        {
            return;
        }

        StartupManager.SetEnabled(executablePath, true);
    }

    /// <summary>
    /// Auto detects plugin folders whose configured path is still empty, writing the None sentinel when nothing is found.
    /// </summary>
    private async Task AutoDetectMissingPluginPathsAsync()
    {
        bool detectLite = m_Settings.LiteMonitorDir.Length == 0;
        bool detectTraffic = m_Settings.TrafficMonitorDir.Length == 0;
        if (!detectLite && !detectTraffic)
        {
            return;
        }

        m_StartupDetectingLiteMonitor = detectLite;
        m_StartupDetectingTrafficMonitor = detectTraffic;
        ApplyStartupDetectingState();

        (string liteResult, string trafficResult) = await Task.Run(() =>
        {
            string lite = detectLite ? LiteMonitorLocator.AutoDetect() : m_Settings.LiteMonitorDir;
            string traffic = detectTraffic ? TrafficMonitorLocator.AutoDetect() : m_Settings.TrafficMonitorDir;
            return (lite, traffic);
        }).ConfigureAwait(true);

        if (detectLite)
        {
            m_Settings.LiteMonitorDir = string.IsNullOrWhiteSpace(liteResult) ? CodexTrayDefaults.PluginPathNone : liteResult;
        }

        if (detectTraffic)
        {
            m_Settings.TrafficMonitorDir = string.IsNullOrWhiteSpace(trafficResult) ? CodexTrayDefaults.PluginPathNone : trafficResult;
        }

        m_StartupDetectingLiteMonitor = false;
        m_StartupDetectingTrafficMonitor = false;
        m_SettingsStore.Save(m_Settings);
        m_PopupViewModel?.LoadSettings(m_Settings);
        ApplyStartupDetectingState();
    }

    /// <summary>
    /// Reflects the startup auto detect progress on the popup spinners when the popup exists.
    /// </summary>
    private void ApplyStartupDetectingState()
    {
        if (m_PopupViewModel == null)
        {
            return;
        }

        m_PopupViewModel.IsDetectingLiteMonitor = m_StartupDetectingLiteMonitor;
        m_PopupViewModel.IsDetectingTrafficMonitor = m_StartupDetectingTrafficMonitor;
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
        Drawing.Point? trayIconPosition = null;
        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("Open Panel", null, (_, _) => ShowPanel(trayIconPosition));
        menu.Items.Add("Refresh Now", null, async (_, _) => await RefreshUsageAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        Forms.NotifyIcon notifyIcon = new()
        {
            ContextMenuStrip = menu,
            Icon = m_AppIcon,
            Text = CodexTrayDefaults.AppName,
            Visible = true,
        };
        notifyIcon.MouseDown += (_, _) => trayIconPosition = Forms.Control.MousePosition;
        notifyIcon.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                TogglePanel(trayIconPosition);
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
            m_NotifyIcon.Text = $"{CodexTrayDefaults.AppName} :{m_Server.Port}";
        }
        catch (SocketException exception)
        {
            m_NotifyIcon.Text = $"{CodexTrayDefaults.AppName} service failed";
            Forms.MessageBox.Show($"Unable to start {CodexTrayDefaults.AppName} service on port {m_Settings.Port}.\n\n{exception.Message}", CodexTrayDefaults.AppName, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
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
                    m_Dispatcher.BeginInvoke(new Action(() => ShowPanel()));
                }
            }
        }, m_SignalCancellation.Token);
    }

    /// <summary>
    /// Toggles the tray popup from the notification icon.
    /// </summary>
    private void TogglePanel(Drawing.Point? trayIconPosition)
    {
        // Clicking the tray icon deactivates the popup first, so a just-hidden popup means the click was a close request.
        if (m_TrayPopupWindow?.IsVisible == true
            || (m_TrayPopupWindow != null
                && DateTime.UtcNow - m_TrayPopupWindow.LastDeactivatedHideUtc < TimeSpan.FromMilliseconds(300)))
        {
            m_TrayPopupWindow?.Hide();
            return;
        }

        ShowPanel(trayIconPosition);
    }

    /// <summary>
    /// Opens the tray popup on the home page.
    /// </summary>
    private void ShowPanel(Drawing.Point? trayIconPosition = null)
    {
        EnsurePopup();
        m_PopupViewModel?.ShowHome();
        RefreshPopupStatus();
        m_TrayPopupWindow?.ShowNearTray(trayIconPosition);
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
        ApplyStartupDetectingState();
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
            ShowInformation($"Installed LiteMonitor plugin:\n{targetPath}");
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
            ShowInformation($"Installed TrafficMonitor plugin:\n{targetPath}");
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
            bool showResetTimeInPlugins = m_Settings.ShowResetTimeInPlugins;
            bool useAbsoluteResetTime = m_Settings.UseAbsoluteResetTime;
            UsageResponse response = await Task.Run(() => m_Collector.Collect(showResetTimeInPlugins, useAbsoluteResetTime)).ConfigureAwait(true);
            m_UsageCache.Update(response);
            RefreshPopupStatus();
            TokenCostStatistics? tokenCost;
            try
            {
                tokenCost = await Task.Run(() => m_TokenCostCollector.Collect()).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                tokenCost = null;
            }
            m_PopupViewModel?.UpdateTokenCost(tokenCost);
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
        string normalized = NormalizePluginDirectory(directory);
        if (normalized.Length == 0)
        {
            message = "LiteMonitor folder is not configured. Use Browse or Auto Detect first.";
            return false;
        }

        if (!LiteMonitorLocator.IsLiteMonitorDirectory(normalized))
        {
            message = $"LiteMonitor.exe was not found in:\n{normalized}";
            return false;
        }

        message = string.Empty;
        return true;
    }

    /// <summary>
    /// Returns the plugin directory, treating the None sentinel as unset.
    /// </summary>
    private static string NormalizePluginDirectory(string directory)
    {
        string trimmed = (directory ?? string.Empty).Trim();
        return string.Equals(trimmed, CodexTrayDefaults.PluginPathNone, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : trimmed;
    }

    /// <summary>
    /// Shows a warning message box.
    /// </summary>
    private static void ShowWarning(string message)
    {
        Forms.MessageBox.Show(message, CodexTrayDefaults.AppName, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Shows an informational message box.
    /// </summary>
    private static void ShowInformation(string message)
    {
        Forms.MessageBox.Show(message, CodexTrayDefaults.AppName, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
    }

    /// <summary>
    /// Validates the configured TrafficMonitor installation directory.
    /// </summary>
    private static bool TryValidateTrafficMonitorDirectory(string directory, out string message)
    {
        string normalized = NormalizePluginDirectory(directory);
        if (normalized.Length == 0)
        {
            message = "TrafficMonitor folder is not configured. Use Browse or Auto Detect first.";
            return false;
        }

        if (!TrafficMonitorLocator.IsTrafficMonitorDirectory(normalized))
        {
            message = $"TrafficMonitor.exe was not found in:\n{normalized}";
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
