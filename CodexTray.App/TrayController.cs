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
    private readonly GrokUsageCollector m_GrokUsageCollector;
    private readonly ApiUsageCollector m_ApiUsageCollector;
    private readonly CursorUsageCollector m_CursorUsageCollector;
    private readonly TokenCostCollector m_TokenCostCollector;
    private readonly UsageCache m_UsageCache = new();
    private readonly Forms.NotifyIcon m_NotifyIcon;
    private readonly System.Drawing.Icon m_AppIcon;
    private readonly DispatcherTimer m_RefreshTimer;
    private readonly CancellationTokenSource m_LifetimeCancellation = new();
    private readonly Lock m_TaskLock = new();
    private AppSettings m_Settings;
    private LightweightHttpServer? m_Server;
    private TrayPopupWindow? m_TrayPopupWindow;
    private TrayPopupViewModel? m_PopupViewModel;
    private Task m_RefreshTask = Task.CompletedTask;
    private Task m_StartupAutoDetectTask = Task.CompletedTask;
    private Task m_SignalListenerTask = Task.CompletedTask;
    private Task m_ServiceTransitionTask = Task.CompletedTask;
    private int m_IsExiting;
    private bool m_StartupDetectingLiteMonitor;
    private bool m_StartupDetectingTrafficMonitor;

    private bool IsExiting => Volatile.Read(ref m_IsExiting) != 0;

    private bool IsPluginServiceRequired => (m_Settings.VisiblePages & (PageItem.Codex | PageItem.Cursor)) != 0;

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
        m_GrokUsageCollector = new GrokUsageCollector();
        m_ApiUsageCollector = new ApiUsageCollector();
        m_CursorUsageCollector = new CursorUsageCollector();
        m_TokenCostCollector = new TokenCostCollector();
        m_AppIcon = LoadApplicationIcon();
        bool settingsExists = m_SettingsStore.Exists();
        m_Settings = m_SettingsStore.Load();
        m_NotifyIcon = CreateNotifyIcon();
        m_RefreshTimer = new DispatcherTimer(DispatcherPriority.Background, m_Dispatcher);
        m_RefreshTimer.Tick += async (_, _) => await RequestRefreshAsync();
        if (IsPluginServiceRequired)
        {
            QueueServiceReconcile();
        }
        ConfigureRefreshTimer();
        _ = RequestRefreshAsync();
        StartSignalListener();
        SyncStartupRegistration();
        if (!settingsExists)
        {
            m_SettingsStore.Save(m_Settings);
            m_Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsExiting)
                {
                    ShowPanel();
                }
            }));
        }

        m_StartupAutoDetectTask = AutoDetectMissingPluginPathsAsync(m_LifetimeCancellation.Token);
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
    private async Task AutoDetectMissingPluginPathsAsync(CancellationToken cancellationToken)
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

        try
        {
            (string liteResult, string trafficResult) = await Task.Run(() =>
            {
                string lite = detectLite ? LiteMonitorLocator.AutoDetect(cancellationToken: cancellationToken) : m_Settings.LiteMonitorDir;
                string traffic = detectTraffic ? TrafficMonitorLocator.AutoDetect(cancellationToken: cancellationToken) : m_Settings.TrafficMonitorDir;
                return (lite, traffic);
            }, cancellationToken).ConfigureAwait(true);

            cancellationToken.ThrowIfCancellationRequested();
            bool changed = false;
            if (detectLite && m_Settings.LiteMonitorDir.Length == 0)
            {
                m_Settings.LiteMonitorDir = string.IsNullOrWhiteSpace(liteResult) ? CodexTrayDefaults.PluginPathNone : liteResult;
                changed = true;
            }

            if (detectTraffic && m_Settings.TrafficMonitorDir.Length == 0)
            {
                m_Settings.TrafficMonitorDir = string.IsNullOrWhiteSpace(trafficResult) ? CodexTrayDefaults.PluginPathNone : trafficResult;
                changed = true;
            }

            if (changed && !IsExiting)
            {
                m_SettingsStore.Save(m_Settings);
                m_PopupViewModel?.LoadSettings(m_Settings);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            m_StartupDetectingLiteMonitor = false;
            m_StartupDetectingTrafficMonitor = false;
            if (!IsExiting)
            {
                ApplyStartupDetectingState();
            }
        }
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
        if (Interlocked.Exchange(ref m_IsExiting, 1) == 0)
        {
            m_LifetimeCancellation.Cancel();
        }

        m_RefreshTimer.Stop();
        if (m_Server != null)
        {
            m_Server.Dispose();
        }

        m_NotifyIcon.Dispose();
        m_AppIcon.Dispose();
    }

    /// <summary>
    /// Creates the tray icon and context menu.
    /// </summary>
    private Forms.NotifyIcon CreateNotifyIcon()
    {
        Drawing.Point? trayIconPosition = null;
        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("Open Panel", null, (_, _) => ShowPanel(trayIconPosition));
        menu.Items.Add("Refresh Now", null, async (_, _) => await RequestRefreshAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitApplicationAsync());

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
        if (IsExiting)
        {
            return;
        }

        try
        {
            LightweightHttpServer server = new(m_UsageCache, m_Settings.Port);
            server.Start();
            m_Server = server;
            m_NotifyIcon.Text = $"{CodexTrayDefaults.AppName} :{server.Port}";
        }
        catch (SocketException exception)
        {
            m_NotifyIcon.Text = $"{CodexTrayDefaults.AppName} service failed";
            PresentInAppDialog(new InAppDialogRequest(
                "Service unavailable",
                $"Unable to start {CodexTrayDefaults.AppName} service on port {m_Settings.Port}.\n\n{exception.Message}",
                "OK"));
        }
    }

    /// <summary>
    /// Queues one serialized reconciliation of the local HTTP service.
    /// </summary>
    private void QueueServiceReconcile()
    {
        Task previous = m_ServiceTransitionTask;
        m_ServiceTransitionTask = ReconcileServiceAsync(previous, m_LifetimeCancellation.Token);
    }

    /// <summary>
    /// Serially stops the old HTTP server and starts the currently configured service.
    /// </summary>
    private async Task ReconcileServiceAsync(Task previous, CancellationToken cancellationToken)
    {
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportBackgroundFailure("Service transition failed", exception);
        }

        LightweightHttpServer? server = m_Server;
        m_Server = null;
        if (server != null)
        {
            try
            {
                await server.StopAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ReportBackgroundFailure("Service stop failed", exception);
            }
            finally
            {
                server.Dispose();
            }
        }

        if (IsExiting || cancellationToken.IsCancellationRequested || !IsPluginServiceRequired)
        {
            m_NotifyIcon.Text = CodexTrayDefaults.AppName;
            return;
        }

        try
        {
            StartService();
            if (!IsExiting)
            {
                RefreshPopupStatus();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportBackgroundFailure("Service transition failed", exception);
        }
    }

    /// <summary>
    /// Starts a background listener for second-instance requests.
    /// </summary>
    private void StartSignalListener()
    {
        CancellationToken cancellationToken = m_LifetimeCancellation.Token;
        m_SignalListenerTask = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (m_ShowPanelEvent.WaitOne(TimeSpan.FromMilliseconds(500)))
                    {
                        await m_Dispatcher.InvokeAsync(() =>
                        {
                            if (!IsExiting)
                            {
                                ShowPanel();
                            }
                        }, DispatcherPriority.Normal, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Toggles the tray popup from the notification icon.
    /// </summary>
    private void TogglePanel(Drawing.Point? trayIconPosition)
    {
        if (IsExiting)
        {
            return;
        }

        if (m_PopupViewModel?.IsModalOpen == true)
        {
            return;
        }

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
    /// Shows the tray popup.
    /// </summary>
    private void ShowPanel(Drawing.Point? trayIconPosition = null)
    {
        if (IsExiting)
        {
            return;
        }

        EnsurePopup();
        RefreshPopupStatus();
        m_TrayPopupWindow?.ShowNearTray(trayIconPosition);
        _ = RequestRefreshAsync();
    }

    /// <summary>
    /// Shows an in-window dialog, opening the tray panel when needed.
    /// </summary>
    private void PresentInAppDialog(InAppDialogRequest request)
    {
        if (IsExiting)
        {
            return;
        }

        if (m_TrayPopupWindow?.IsVisible != true)
        {
            ShowPanel();
        }

        m_PopupViewModel?.ShowInAppDialog(request);
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

        m_PopupViewModel = new TrayPopupViewModel(m_Settings, RequestRefreshAsync);
        m_PopupViewModel.SaveSettingsRequested += (_, _) => SaveSettings();
        m_PopupViewModel.ApiMonitorsChanged += (_, _) => m_SettingsStore.Save(m_Settings);
        m_PopupViewModel.InAppDialogRequested += PresentInAppDialog;
        m_PopupViewModel.InstallLiteMonitorPluginRequested += (_, _) => InstallLiteMonitorPlugin();
        m_PopupViewModel.InstallTrafficMonitorPluginRequested += (_, _) => InstallTrafficMonitorPlugin();
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
        if (IsExiting)
        {
            return;
        }

        int previousPort = m_Settings.Port;
        bool codexWasEnabled = (m_Settings.VisiblePages & PageItem.Codex) != 0;
        bool cursorWasEnabled = (m_Settings.VisiblePages & PageItem.Cursor) != 0;
        bool serviceWasRequired = IsPluginServiceRequired;
        m_PopupViewModel?.ApplySettings();

        StartupManager.SetEnabled(Environment.ProcessPath ?? string.Empty, m_Settings.StartWithWindows);
        m_SettingsStore.Save(m_Settings);
        ConfigureRefreshTimer();
        bool codexIsEnabled = (m_Settings.VisiblePages & PageItem.Codex) != 0;
        bool cursorIsEnabled = (m_Settings.VisiblePages & PageItem.Cursor) != 0;
        if (codexWasEnabled && !codexIsEnabled)
        {
            m_UsageCache.ClearCodex();
        }

        if (cursorWasEnabled && !cursorIsEnabled)
        {
            m_UsageCache.ClearCursor();
        }

        bool serviceIsRequired = IsPluginServiceRequired;
        if (serviceWasRequired != serviceIsRequired || (serviceIsRequired && previousPort != m_Settings.Port))
        {
            QueueServiceReconcile();
        }

        RefreshPopupStatus();
        _ = RequestRefreshAsync();
    }

    /// <summary>
    /// Applies the configured settings panel refresh interval.
    /// </summary>
    private void ConfigureRefreshTimer()
    {
        m_Settings.Normalize();
        m_RefreshTimer.Stop();
        if (m_Settings.VisiblePages == PageItem.None)
        {
            return;
        }

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
            m_PopupViewModel?.ApplySettings();

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
            m_PopupViewModel?.ApplySettings();

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
    /// Returns the active refresh task or starts one refresh operation.
    /// </summary>
    private Task RequestRefreshAsync()
    {
        lock (m_TaskLock)
        {
            if (IsExiting)
            {
                return Task.CompletedTask;
            }

            if (!m_RefreshTask.IsCompleted)
            {
                return m_RefreshTask;
            }

            m_RefreshTask = RefreshUsageAsync(m_LifetimeCancellation.Token);
            return m_RefreshTask;
        }
    }

    /// <summary>
    /// Collects fresh usage data and publishes it after every child completes.
    /// </summary>
    private async Task RefreshUsageAsync(CancellationToken cancellationToken)
    {
        PageItem visiblePages = m_Settings.VisiblePages;
        if (visiblePages == PageItem.None || IsExiting)
        {
            return;
        }

        if (m_PopupViewModel != null)
        {
            m_PopupViewModel.IsRefreshing = true;
        }

        try
        {
            bool useAbsoluteResetTime = m_Settings.UseAbsoluteResetTime;
            bool showResetTimeInPlugins = m_Settings.ShowResetTimeInPlugins;
            Task<UsageResponse>? codexUsageTask = null;
            Task<TokenCostStatistics?>? tokenCostTask = null;
            Task<GrokUsageDashboard>? grokDashboardTask = null;
            Task<TokenCostStatistics?>? grokTokenCostTask = null;
            Task<CursorUsageDashboard>? cursorDashboardTask = null;
            Task<IReadOnlyList<ApiUsageResult>>? apiUsageTask = null;
            if ((visiblePages & PageItem.Codex) != 0)
            {
                codexUsageTask = m_Collector.CollectAsync(showResetTimeInPlugins, useAbsoluteResetTime, cancellationToken);
                tokenCostTask = Task.Run<TokenCostStatistics?>(() =>
                {
                    try
                    {
                        return m_TokenCostCollector.Collect(cancellationToken: cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                    {
                        return null;
                    }
                }, cancellationToken);
            }

            if ((visiblePages & PageItem.Grok) != 0)
            {
                grokDashboardTask = m_GrokUsageCollector.CollectDashboardAsync(cancellationToken);
                grokTokenCostTask = Task.Run<TokenCostStatistics?>(() =>
                {
                    try
                    {
                        return m_TokenCostCollector.CollectGrok(cancellationToken: cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or OverflowException)
                    {
                        return null;
                    }
                }, cancellationToken);
            }

            if ((visiblePages & PageItem.Cursor) != 0)
            {
                cursorDashboardTask = m_CursorUsageCollector.CollectDashboardAsync(cancellationToken: cancellationToken);
            }

            if ((visiblePages & PageItem.Apis) != 0)
            {
                ApiMonitorSettings[] apiMonitors = m_Settings.ApiMonitors.Select(CloneApiMonitor).ToArray();
                apiUsageTask = m_ApiUsageCollector.CollectAsync(apiMonitors, cancellationToken);
            }

            Task[] children = new Task?[] { codexUsageTask, tokenCostTask, grokDashboardTask, grokTokenCostTask, cursorDashboardTask, apiUsageTask }
                .Where(task => task != null)
                .Cast<Task>()
                .ToArray();
            await Task.WhenAll(children).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExiting)
            {
                return;
            }

            if (codexUsageTask != null && tokenCostTask != null)
            {
                UsageResponse response = codexUsageTask.Result;
                m_UsageCache.UpdateCodex(response);
                RefreshPopupStatus();
                m_PopupViewModel?.UpdateTokenCost(tokenCostTask.Result);
            }

            if (grokDashboardTask != null && grokTokenCostTask != null)
            {
                m_PopupViewModel?.UpdateGrokDashboard(grokDashboardTask.Result, grokTokenCostTask.Result);
            }

            if (cursorDashboardTask != null)
            {
                CursorUsageDashboard dashboard = cursorDashboardTask.Result;
                m_UsageCache.UpdateCursor(CursorUsageCollector.BuildPluginUsage(dashboard, showResetTimeInPlugins, useAbsoluteResetTime));
                RefreshPopupStatus();
                m_PopupViewModel?.UpdateCursorDashboard(dashboard);
            }

            if (apiUsageTask != null)
            {
                m_PopupViewModel?.UpdateApiUsage(apiUsageTask.Result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportBackgroundFailure("Usage refresh failed", exception);
        }
        finally
        {
            if (!IsExiting && m_PopupViewModel != null)
            {
                m_PopupViewModel.IsRefreshing = false;
            }
        }
    }

    /// <summary>
    /// Copies an API monitor before an asynchronous refresh.
    /// </summary>
    private static ApiMonitorSettings CloneApiMonitor(ApiMonitorSettings monitor)
    {
        return new ApiMonitorSettings
        {
            Id = monitor.Id,
            Name = monitor.Name,
            Provider = monitor.Provider,
            BaseUrl = monitor.BaseUrl,
            ApiKey = monitor.ApiKey,
            UserId = monitor.UserId,
        };
    }

    /// <summary>
    /// Reports one observed background failure without allowing it to escape its owner task.
    /// </summary>
    private void ReportBackgroundFailure(string title, Exception exception)
    {
        if (IsExiting)
        {
            return;
        }

        try
        {
            PresentInAppDialog(new InAppDialogRequest(title, exception.Message, "OK"));
        }
        catch (Exception)
        {
            // Background owner tasks must remain observed during dispatcher teardown.
        }
    }

    /// <summary>
    /// Cancels owned work, drains it, and exits the tray application.
    /// </summary>
    private async Task ExitApplicationAsync()
    {
        if (Interlocked.Exchange(ref m_IsExiting, 1) != 0)
        {
            return;
        }

        TrayPopupViewModel? popupViewModel = m_PopupViewModel;
        try
        {
            m_RefreshTimer.Stop();
            m_NotifyIcon.Visible = false;
            m_TrayPopupWindow?.Close();
            m_LifetimeCancellation.Cancel();
            popupViewModel?.AutoDetectLiteMonitorCommand.Cancel();
            popupViewModel?.AutoDetectTrafficMonitorCommand.Cancel();

            Task[] ownedTasks =
            [
                m_RefreshTask,
                m_StartupAutoDetectTask,
                m_SignalListenerTask,
                m_ServiceTransitionTask,
                popupViewModel?.AutoDetectLiteMonitorCommand.ExecutionTask ?? Task.CompletedTask,
                popupViewModel?.AutoDetectTrafficMonitorCommand.ExecutionTask ?? Task.CompletedTask,
            ];
            try
            {
                await Task.WhenAll(ownedTasks).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (m_LifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                // Exit must continue draining the remaining lifecycle resources.
            }

            if (m_Server != null)
            {
                await m_Server.StopAsync().ConfigureAwait(true);
                m_Server.Dispose();
                m_Server = null;
            }
        }
        catch (Exception)
        {
            // Application exit must not leak exceptions through the async event handler.
        }
        finally
        {
            m_NotifyIcon.Dispose();
            m_AppIcon.Dispose();
            m_LifetimeCancellation.Dispose();
            m_Application.Shutdown();
        }
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
    /// Shows a warning in the tray panel.
    /// </summary>
    private void ShowWarning(string message)
    {
        PresentInAppDialog(new InAppDialogRequest("Warning", message, "OK"));
    }

    /// <summary>
    /// Shows an informational message in the tray panel.
    /// </summary>
    private void ShowInformation(string message)
    {
        PresentInAppDialog(new InAppDialogRequest("Plugin installed", message, "OK"));
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
