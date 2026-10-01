using CodexTray.Core;
using System.Globalization;
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
    private readonly CodexUsageCollector m_CodexUsageCollector;
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
    private readonly RefreshRequests m_RefreshRequests = new();
    private ApiUsageSnapshots m_ApiSnapshots = new();
    private bool m_StartupDetectingLiteMonitor;
    private bool m_StartupDetectingTrafficMonitor;

    private bool IsExiting => Volatile.Read(ref m_IsExiting) != 0;

    private bool IsPluginServiceRequired => m_Settings.VisiblePages != PageItem.None;

    /// <summary>
    /// Creates the tray controller and starts background work.
    /// </summary>
    public TrayController(WpfApplication application, EventWaitHandle showPanelEvent, Dispatcher dispatcher)
    {
        m_Application = application;
        m_ShowPanelEvent = showPanelEvent;
        m_Dispatcher = dispatcher;
        m_SettingsStore = new SettingsStore();
        m_CodexUsageCollector = new CodexUsageCollector();
        m_GrokUsageCollector = new GrokUsageCollector();
        m_ApiUsageCollector = new ApiUsageCollector();
        m_CursorUsageCollector = new CursorUsageCollector();
        m_TokenCostCollector = new TokenCostCollector();
        m_AppIcon = LoadApplicationIcon();
        bool settingsExists = m_SettingsStore.Exists();
        m_Settings = m_SettingsStore.Load();
        EnsureViewModel();
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
            TryPersistSettings();
            m_Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsExiting)
                {
                    ShowPanel();
                }
            }));
        }

        if (m_SettingsStore.IsWriteBlocked)
        {
            m_Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsExiting)
                {
                    m_PopupViewModel?.ShowSettings();
                    ShowWarning(m_SettingsStore.LoadError);
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
        AppSettings settingsAtStart = m_Settings;
        if (m_SettingsStore.IsWriteBlocked)
        {
            return;
        }

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
            if (!ReferenceEquals(settingsAtStart, m_Settings))
            {
                return;
            }

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
                TryPersistSettings();
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

        EnsureViewModel();
        ApplyStartupDetectingState();
        m_TrayPopupWindow = new TrayPopupWindow(m_PopupViewModel!);
        m_TrayPopupWindow.Closed += (_, _) => m_TrayPopupWindow = null;
    }

    /// <summary>
    /// Retains collected results and success times even while the panel has never been opened.
    /// </summary>
    private void EnsureViewModel()
    {
        if (m_PopupViewModel != null)
        {
            return;
        }

        m_PopupViewModel = new TrayPopupViewModel(m_Settings, RequestRefreshAsync, (page, id) => RequestRefreshAsync(page, id));
        m_PopupViewModel.SaveSettingsRequested += (_, _) => SaveSettings();
        m_PopupViewModel.ApiMonitorsChanged += (_, _) =>
        {
            TryPersistSettings();
            PublishApiResults([], []);
        };
        m_PopupViewModel.RestoreSettingsBackupRequested += (_, _) => PresentInAppDialog(new InAppDialogRequest(
            "Restore settings", "Restore the last good configuration? The current file will be preserved as a separate copy.", "Restore", "Cancel", RestoreSettingsBackup));
        m_PopupViewModel.InAppDialogRequested += PresentInAppDialog;
        m_PopupViewModel.InstallLiteMonitorPluginRequested += (_, _) => InstallLiteMonitorPlugin();
        m_PopupViewModel.InstallTrafficMonitorPluginRequested += (_, _) => InstallTrafficMonitorPlugin();
        m_PopupViewModel.UpdateApplyRequested += (_, handoff) => m_Dispatcher.BeginInvoke(new Action(() => StartUpdateAndExit(handoff)));
        SyncSettingsRecoveryStatus();
    }

    /// <summary>
    /// Reflects configuration protection and backup availability in the settings page.
    /// </summary>
    private void SyncSettingsRecoveryStatus()
    {
        if (m_PopupViewModel != null)
        {
            m_PopupViewModel.SettingsLoadError = m_SettingsStore.LoadError;
            m_PopupViewModel.IsSettingsWriteBlocked = m_SettingsStore.IsWriteBlocked;
            m_PopupViewModel.HasSettingsBackup = m_SettingsStore.CanRestoreBackup;
        }
    }

    /// <summary>
    /// Handles persistence failures centrally so automatic and card saves cannot overwrite a damaged configuration.
    /// </summary>
    private bool TryPersistSettings(AppSettings? settings = null)
    {
        try
        {
            m_SettingsStore.Save(settings ?? m_Settings);
            SyncSettingsRecoveryStatus();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SyncSettingsRecoveryStatus();
            ShowWarning(m_SettingsStore.LoadError.Length > 0 ? m_SettingsStore.LoadError : "Settings could not be saved. Original file preserved.");
            return false;
        }
    }

    /// <summary>
    /// Restores validated settings while invalidating in-flight publications from the previous configuration.
    /// </summary>
    private void RestoreSettingsBackup()
    {
        try
        {
            m_Settings = m_SettingsStore.RestoreBackup();
            m_ApiSnapshots = new();
            m_PopupViewModel?.LoadRecoveredSettings(m_Settings);
            SyncSettingsRecoveryStatus();
            m_UsageCache.ClearCodex();
            m_UsageCache.ClearCursor();
            m_UsageCache.ClearGrok();
            m_UsageCache.ClearDeepSeek();
            ConfigureRefreshTimer();
            QueueServiceReconcile();
            StartupManager.SetEnabled(Environment.ProcessPath ?? string.Empty, m_Settings.StartWithWindows);
            RefreshPopupStatus();
            _ = RequestRefreshAsync();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SyncSettingsRecoveryStatus();
            ShowWarning("The backup could not be restored. Check that it is valid and the application folder is writable; the original file has been preserved.");
        }
    }

    /// <summary>
    /// Saves settings and applies startup registration changes.
    /// </summary>
    private bool SaveSettings()
    {
        if (IsExiting || m_SettingsStore.IsWriteBlocked)
        {
            return false;
        }

        int previousPort = m_Settings.Port;
        bool codexWasEnabled = (m_Settings.VisiblePages & PageItem.Codex) != 0;
        bool grokWasEnabled = (m_Settings.VisiblePages & PageItem.Grok) != 0;
        bool cursorWasEnabled = (m_Settings.VisiblePages & PageItem.Cursor) != 0;
        bool apisWasEnabled = (m_Settings.VisiblePages & PageItem.Apis) != 0;
        bool serviceWasRequired = IsPluginServiceRequired;
        AppSettings candidate = m_Settings.Copy();
        m_PopupViewModel?.ApplySettings(candidate, markSaved: false);

        if (!TryPersistSettings(candidate))
        {
            return false;
        }

        m_Settings = candidate;
        m_PopupViewModel?.AcceptSavedSettings(candidate);
        StartupManager.SetEnabled(Environment.ProcessPath ?? string.Empty, m_Settings.StartWithWindows);
        ConfigureRefreshTimer();
        bool codexIsEnabled = (m_Settings.VisiblePages & PageItem.Codex) != 0;
        bool grokIsEnabled = (m_Settings.VisiblePages & PageItem.Grok) != 0;
        bool cursorIsEnabled = (m_Settings.VisiblePages & PageItem.Cursor) != 0;
        bool apisIsEnabled = (m_Settings.VisiblePages & PageItem.Apis) != 0;
        if (codexWasEnabled && !codexIsEnabled)
        {
            m_UsageCache.ClearCodex();
        }

        if (grokWasEnabled && !grokIsEnabled)
        {
            m_UsageCache.ClearGrok();
        }

        if (cursorWasEnabled && !cursorIsEnabled)
        {
            m_UsageCache.ClearCursor();
        }

        if (apisWasEnabled && !apisIsEnabled)
        {
            m_UsageCache.ClearDeepSeek();
        }

        bool serviceIsRequired = IsPluginServiceRequired;
        if (serviceWasRequired != serviceIsRequired || (serviceIsRequired && previousPort != m_Settings.Port))
        {
            QueueServiceReconcile();
        }

        RefreshPopupStatus();
        _ = RequestRefreshAsync();
        return true;
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
        if (m_SettingsStore.IsWriteBlocked)
        {
            ShowWarning(m_SettingsStore.LoadError);
            return;
        }

        try
        {
            if (!SaveSettings())
            {
                return;
            }

            if (!TryValidateLiteMonitorDirectory(m_Settings.LiteMonitorDir, out string message))
            {
                ShowWarning(message);
                return;
            }

            string targetPath = LiteMonitorPluginInstaller.Install(m_Settings.LiteMonitorDir, m_Settings.Port);
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
        if (m_SettingsStore.IsWriteBlocked)
        {
            ShowWarning(m_SettingsStore.LoadError);
            return;
        }

        try
        {
            if (!SaveSettings())
            {
                return;
            }

            if (!TryValidateTrafficMonitorDirectory(m_Settings.TrafficMonitorDir, out string message))
            {
                ShowWarning(message);
                return;
            }

            string targetPath = TrafficMonitorPluginInstaller.Install(m_Settings.TrafficMonitorDir, m_Settings.Port);
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
        if (IsExiting)
        {
            return;
        }

        m_NotifyIcon.Text = BuildTrayTooltip(m_Settings.VisiblePages, m_UsageCache.Get());
        if (m_PopupViewModel == null)
        {
            return;
        }

        UsageResponse? response = m_UsageCache.GetCodex();
        m_PopupViewModel.UpdateStatus(m_Server?.IsRunning == true, m_Server?.Port ?? m_Settings.Port, response, m_Server?.LastError);
    }

    /// <summary>
    /// Formats only visible sources into a bounded, credential-free tray tooltip.
    /// </summary>
    internal static string BuildTrayTooltip(PageItem visiblePages, UsageResponse? usage)
    {
        List<string> lines = [];
        if ((visiblePages & PageItem.Codex) != 0)
        {
            lines.Add("Codex W: " + (usage?.Available == true && usage.Limits.Weekly.WindowMinutes > 0 ? Percent(usage.Limits.Weekly) : "N/A"));
        }
        if ((visiblePages & PageItem.Cursor) != 0)
        {
            lines.Add("Cursor M: " + (usage?.Display.CursorMonthly != null && usage.Display.CursorMonthly != "N/A" ? Percent(usage.Limits.CursorMonthly) : "N/A"));
        }
        if ((visiblePages & PageItem.Grok) != 0)
        {
            lines.Add("Grok W: " + (usage?.Display.GrokWeekly != null && usage.Display.GrokWeekly != "N/A" ? Percent(usage.Limits.GrokWeekly) : "N/A"));
        }
        if ((visiblePages & PageItem.Apis) != 0)
        {
            string raw = usage?.Display.DeepSeek ?? "N/A";
            string balance = raw.StartsWith('¥') && decimal.TryParse(raw.AsSpan(1), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount)
                ? $"¥{amount:0}" : "N/A";
            balance = balance.Length > 11 ? balance[..10] + "…" : balance;
            lines.Add("DeepSeek: " + balance);
        }

        return lines.Count == 0 ? CodexTrayDefaults.AppName : string.Join('\n', lines);

        /// <summary>
        /// Bounds the reported percent to a short numeric display.
        /// </summary>
        static string Percent(UsageLimit limit) => $"{Math.Clamp(limit.RemainingPercent, 0, 100)}%";
    }

    /// <summary>
    /// Returns the active refresh task or starts one refresh operation.
    /// </summary>
    private Task RequestRefreshAsync() => RequestRefreshAsync(PageItem.All);

    /// <summary>
    /// Queues selected sources or one API card behind the current refresh owner.
    /// </summary>
    private Task RequestRefreshAsync(PageItem pages, string? apiId = null)
    {
        lock (m_TaskLock)
        {
            if (IsExiting)
            {
                return Task.CompletedTask;
            }

            m_RefreshRequests.Add(pages, apiId);
            if (!m_RefreshTask.IsCompleted)
            {
                return m_RefreshTask;
            }

            m_RefreshTask = RefreshUntilIdleAsync(m_LifetimeCancellation.Token);
            return m_RefreshTask;
        }
    }

    /// <summary>
    /// Runs refresh operations until no overlapping request remains.
    /// </summary>
    private async Task RefreshUntilIdleAsync(CancellationToken cancellationToken)
    {
        // Register the owner task before a synchronously completed source can open the panel and request another refresh.
        await Task.Yield();
        while (true)
        {
            RefreshRequest? request;
            lock (m_TaskLock)
            {
                request = m_RefreshRequests.Take();
                if (IsExiting || request == null)
                {
                    m_RefreshTask = Task.CompletedTask;
                    return;
                }
            }

            await RefreshUsageAsync(request, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Collects sources concurrently and publishes each result as soon as it completes.
    /// </summary>
    private async Task RefreshUsageAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        AppSettings settingsAtStart = m_Settings;
        PageItem visiblePages = m_Settings.VisiblePages & request.Pages;
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
            List<Task> children = [];
            if ((visiblePages & PageItem.Codex) != 0)
            {
                children.Add(PublishAsync(PageItem.Codex, m_CodexUsageCollector.CollectAsync(useAbsoluteResetTime, cancellationToken), result => m_UsageCache.UpdateCodex(result)));
                children.Add(PublishAsync(PageItem.Codex, Task.Run(() => CollectTokenCostSafely(() => m_TokenCostCollector.CollectCodex(cancellationToken: cancellationToken)), cancellationToken),
                    result => m_PopupViewModel?.UpdateTokenCost(result.Statistics, result.Error), localCosts: true));
            }

            if ((visiblePages & PageItem.Grok) != 0)
            {
                children.Add(PublishAsync(PageItem.Grok, m_GrokUsageCollector.CollectDashboardAsync(cancellationToken), dashboard =>
                {
                    m_UsageCache.UpdateGrok(GrokUsageCollector.BuildPluginUsage(dashboard));
                    m_PopupViewModel?.UpdateGrokDashboard(dashboard);
                }));
                children.Add(PublishAsync(PageItem.Grok, Task.Run(() => CollectTokenCostSafely(() => m_TokenCostCollector.CollectGrok(cancellationToken: cancellationToken)), cancellationToken),
                    result => m_PopupViewModel?.UpdateGrokTokenCost(result.Statistics, result.Error), localCosts: true));
            }

            if ((visiblePages & PageItem.Cursor) != 0)
            {
                children.Add(PublishAsync(PageItem.Cursor, m_CursorUsageCollector.CollectDashboardAsync(cancellationToken: cancellationToken), dashboard =>
                {
                    m_UsageCache.UpdateCursor(CursorUsageCollector.BuildPluginUsage(dashboard));
                    m_PopupViewModel?.UpdateCursorDashboard(dashboard);
                }));
            }

            if ((visiblePages & PageItem.Apis) != 0)
            {
                ApiMonitorSettings[] apiMonitors = m_Settings.ApiMonitors.Where(card => request.AllApiCards || request.ApiIds.Contains(card.Id)).Select(CloneApiMonitor).ToArray();
                children.Add(PublishAsync(PageItem.Apis, m_ApiUsageCollector.CollectAsync(apiMonitors, cancellationToken), results => PublishApiResults(apiMonitors, results),
                    failure: _ => PublishApiResults(apiMonitors, apiMonitors.Select(card =>
                        new ApiUsageResult(card.Id, false, "N/A", "N/A", "Collection failed; retry this card", DateTimeOffset.Now, Provider: card.Provider)).ToArray())));
            }

            await Task.WhenAll(children).ConfigureAwait(true);
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

        Task PublishAsync<T>(PageItem page, Task<T> collect, Action<T> publish, bool localCosts = false, Action<Exception>? failure = null)
        {
            return PublishRefreshResultAsync(collect, result =>
            {
                publish(result);
                RefreshPopupStatus();
                ReconcileDeadPluginService();
            }, () => !IsExiting && ReferenceEquals(settingsAtStart, m_Settings) && (m_Settings.VisiblePages & page) != 0,
                exception =>
                {
                    if (failure != null)
                    {
                        failure(exception);
                        RefreshPopupStatus();
                        return;
                    }

                    string error = "Collection failed; retry this source";
                    DateTimeOffset now = DateTimeOffset.Now;
                    if (localCosts)
                    {
                        if (page == PageItem.Codex)
                        {
                            m_PopupViewModel?.UpdateTokenCost(null, error);
                        }
                        else
                        {
                            m_PopupViewModel?.UpdateGrokTokenCost(null, error);
                        }

                        return;
                    }

                    // A failed collector must not leave an apparently current plugin value behind.
                    switch (page)
                    {
                        case PageItem.Codex:
                            m_UsageCache.UpdateCodex(new UsageResponse { Error = error, UpdatedAt = now.ToString("O") });
                            break;
                        case PageItem.Cursor:
                            m_UsageCache.ClearCursor();
                            m_PopupViewModel?.UpdateCursorDashboard(new(null, null, error, error, now, GrokBotUsageError: error));
                            break;
                        case PageItem.Grok:
                            m_UsageCache.ClearGrok();
                            m_PopupViewModel?.UpdateGrokDashboard(new(null, error, now));
                            break;
                        case PageItem.Apis:
                            m_UsageCache.ClearDeepSeek();
                            m_PopupViewModel?.UpdateApiUsage(m_Settings.ApiMonitors.Select(monitor =>
                                new ApiUsageResult(monitor.Id, false, "N/A", "N/A", error, now, Provider: monitor.Provider)).ToArray());
                            break;
                    }

                    RefreshPopupStatus();
                }, cancellationToken);
        }
    }

    /// <summary>
    /// Updates selected cards and recomputes the first DeepSeek plugin value in the current order.
    /// </summary>
    private void PublishApiResults(IReadOnlyList<ApiMonitorSettings> queried, IReadOnlyList<ApiUsageResult> results)
    {
        IReadOnlyList<ApiUsageResult> merged = m_ApiSnapshots.Merge(m_Settings.ApiMonitors, queried, results);
        m_PopupViewModel?.UpdateApiUsage(merged);
        if ((m_Settings.VisiblePages & PageItem.Apis) != 0)
        {
            m_UsageCache.UpdateDeepSeek(ApiUsageCollector.BuildDeepSeekPluginUsage(merged));
        }
        else
        {
            m_UsageCache.ClearDeepSeek();
        }
        RefreshPopupStatus();
    }

    /// <summary>
    /// Observes one collection independently and guards publication against hidden pages and shutdown.
    /// </summary>
    internal static async Task PublishRefreshResultAsync<T>(Task<T> collect, Action<T> publish, Func<bool> canPublish, Action<Exception> reportFailure, CancellationToken cancellationToken)
    {
        try
        {
            T result = await collect.ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (canPublish())
            {
                publish(result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested && canPublish())
            {
                reportFailure(exception);
            }
        }
    }

    /// <summary>
    /// Restarts the plugin HTTP server after an accept-loop failure.
    /// </summary>
    private void ReconcileDeadPluginService()
    {
        if (IsPluginServiceRequired && m_Server is { IsRunning: false, LastError: not null })
        {
            QueueServiceReconcile();
        }
    }

    /// <summary>
    /// Collects token cost without failing sibling quota updates.
    /// </summary>
    private static (TokenCostStatistics? Statistics, string Error) CollectTokenCostSafely(Func<TokenCostStatistics?> collect)
    {
        try
        {
            return (collect(), string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or OverflowException or KeyNotFoundException)
        {
            return (null, exception is UnauthorizedAccessException ? "Local session or pricing file access denied" : "Local session or pricing data could not be read");
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
            PresentInAppDialog(new InAppDialogRequest(title, UsageDiagnostics.Error(exception), "OK"));
        }
        catch (Exception)
        {
            // Background owner tasks must remain observed during dispatcher teardown.
        }
    }

    /// <summary>
    /// Starts the temporary updater and then exits this process.
    /// </summary>
    private void StartUpdateAndExit(AppUpdateHandoff handoff)
    {
        if (IsExiting)
        {
            return;
        }

        try
        {
            AppUpdateProcess.Start(handoff, Environment.ProcessId);
        }
        catch (Exception exception)
        {
            PresentInAppDialog(new InAppDialogRequest("Update failed", exception.Message, "OK"));
            return;
        }

        _ = ExitApplicationAsync();
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
            popupViewModel?.RunUpdateCommand.Cancel();
            popupViewModel?.InstallUpdateCommand.Cancel();

            Task[] ownedTasks =
            [
                m_RefreshTask,
                m_StartupAutoDetectTask,
                m_SignalListenerTask,
                m_ServiceTransitionTask,
                popupViewModel?.AutoDetectLiteMonitorCommand.ExecutionTask ?? Task.CompletedTask,
                popupViewModel?.AutoDetectTrafficMonitorCommand.ExecutionTask ?? Task.CompletedTask,
                popupViewModel?.RunUpdateCommand.ExecutionTask ?? Task.CompletedTask,
                popupViewModel?.InstallUpdateCommand.ExecutionTask ?? Task.CompletedTask,
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
        string normalized = TrayPopupViewModel.PluginPathOrEmpty(directory);
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
        string normalized = TrayPopupViewModel.PluginPathOrEmpty(directory);
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
