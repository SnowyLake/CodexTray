using CodexTray.App;
using CodexTray.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace CodexTray.Tests;

internal static class Program
{
    private static int s_Failures;

    /// <summary>
    /// Runs deterministic checks or the opt-in redacted Grok live probe.
    /// </summary>
    private static async Task<int> Main(string[] args)
    {
        if (args.SequenceEqual(["--grok-live"], StringComparer.Ordinal))
        {
            return await RunGrokLiveAsync();
        }

        if (args.SequenceEqual(["--render-showcase"], StringComparer.Ordinal))
        {
            return RenderShowcase();
        }

        await RunAsync("creates the fixed-size popup", TestPopupFixedSizeAsync);
        await RunAsync("collects limits and display labels", TestCollectsLimitsAndDisplayLabelsAsync);
        await RunAsync("uses countdown label for same-day weekly reset", TestWeeklyCountdownLabelAsync);
        await RunAsync("uses countdown label for next-day weekly reset", TestNextDayWeeklyCountdownLabelAsync);
        await RunAsync("returns unavailable response without OAuth credentials", TestEmptyResponseAsync);
        await RunAsync("collects official Codex quota", TestOfficialQuotaAsync);
        await RunAsync("classifies a lone weekly quota by window duration", TestLoneWeeklyQuotaAsync);
        await RunAsync("collects Codex reset credits", TestResetCreditsAsync);
        await RunAsync("applies quota and reset credit color states", TestQuotaAndResetCreditColorStatesAsync);
        await RunAsync("formats Codex subscription badges", TestCodexPlanDisplayAsync);
        await RunAsync("keeps plugin displays percentage-only", TestPluginDisplayPercentOnlyAsync);
        await RunAsync("uses absolute reset time when enabled", TestAbsoluteResetTimeAsync);
        await RunAsync("serves health and usage over HTTP", TestHttpServerAsync);
        await RunAsync("merges and clears plugin usage sources", TestUsageCacheSourcesAsync);
        await RunAsync("drains active HTTP clients on stop", TestHttpServerActiveClientStopAsync);
        await RunAsync("recovers HTTP server after bind failure", TestHttpServerBindFailureRecoveryAsync);
        await RunAsync("supports idempotent stop and restart", TestHttpServerStopRestartAsync);
        await RunAsync("propagates collector cancellation", TestCollectorCancellationAsync);
        await RunAsync("distinguishes API cancellation from timeout", TestApiUsageCancellationAsync);
        await RunAsync("bounds transient retries and respects Retry-After", RetryTests.HttpRetriesAsync);
        await RunAsync("merges retry targets and preserves independent API snapshots", RetryTests.TargetsAndSnapshotsAsync);
        await RunAsync("redacts response content and copied diagnostics", RetryTests.DiagnosticsAsync);
        await RunAsync("does not replay Grok credits after a terminal rate limit", RetryTests.GrokRateLimitAsync);
        await RunAsync("installs LiteMonitor plugin config", TestPluginInstallAsync);
        await RunAsync("installs TrafficMonitor plugin", TestTrafficMonitorPluginInstallAsync);
        await RunAsync("stores settings beside the executable", TestSettingsStorePathAsync);
        await RunAsync("repairs missing settings fields", TestSettingsStoreRepairsMissingFieldsAsync);
        await RunAsync("repairs null and malformed settings", TestSettingsStoreRepairsNullAndMalformedValuesAsync);
        await RunAsync("preserves damaged settings and restores validated backups", TestSettingsRecoveryAsync);
        await RunAsync("retains valid settings after backup or normalization write failures", TestSettingsMaintenanceFailureAsync);
        await RunAsync("normalizes settings refresh interval", TestSettingsNormalizeAsync);
        await RunAsync("formats compact token units", TestTokenUnitFormattingAsync);
        await RunAsync("persists API monitor settings", TestApiMonitorSettingsAsync);
        await RunAsync("collects DeepSeek and NewAPI balances", TestApiUsageCollectorAsync);
        await RunAsync("maps the first DeepSeek card to plugin usage", TestDeepSeekPluginUsageAsync);
        await RunAsync("parses Grok billing responses", TestGrokUsageCollectorAsync);
        await RunAsync("refreshes expired Grok Build OAuth", TestGrokBuildOAuthRefreshAsync);
        await RunAsync("prefers Grok settings display name", TestGrokSettingsSubscriptionTierAsync);
        await RunAsync("ignores OpenCode OAuth for Grok", TestGrokIgnoresOpenCodeOAuthAsync);
        await RunAsync("includes published model pricing", TestPublishedModelPricingAsync);
        await RunAsync("summarizes API refresh statuses", TestApiUsageSummaryAsync);
        await RunAsync("publishes completed refresh sources independently", TestIndependentRefreshAsync);
        await RunAsync("invalidates session costs when pricing contents change", TestPricingReloadAsync);
        await RunAsync("exposes successful timestamps and incomplete costs", TestDataFreshnessAsync);
        await RunAsync("tracks asynchronous refresh commands", TestRefreshCommandAsync);
        await RunAsync("formats bounded visible-source tray summaries", TestTrayTooltipAsync);
        await RunAsync("builds rolling token cost chart", TestTokenCostChartViewModelAsync);
        await RunAsync("pages session log rows for the current harness", TestSessionLogPagingAsync);
        await RunAsync("fits session log model names and keeps the notes glyph", TestSessionLogLayoutAsync);
        await RunAsync("keeps per-call session log rows from Codex and Grok collects", TestSessionLogEntriesAsync);
        await RunAsync("tracks migrated dirty properties", TestMigratedDirtyPropertiesAsync);
        await RunAsync("raises migrated tray notifications", TestMigratedTrayNotificationsAsync);
        await RunAsync("updates API monitor command states", TestApiMonitorCommandStatesAsync);
        await RunAsync("raises dependent API monitor notifications", TestApiMonitorNotificationsAsync);
        await RunAsync("computes cache hit percent", TestCacheHitPercentAsync);
        await RunAsync("computes paired model output speed", TestModelOutputSpeedAsync);
        await RunAsync("estimates Codex output speed without tool waits or replay", TestCodexOutputSpeedAsync);
        await RunAsync("applies Codex estimated speed floors and usage-record ends", TestCodexSpeedWindowEdgesAsync);
        await RunAsync("stitches Codex desktop pages and skips unread parent forks", TestCodexForkAndPaginationAsync);
        await RunAsync("collects exact Codex token cost", TestCodexTokenCostCollectorAsync);
        await RunAsync("prefers Codex last_token_usage over cumulative totals", TestCodexLastTokenUsageAsync);
        await RunAsync("deduplicates archived Codex rollouts by thread id", TestCodexArchivedRolloutDedupAsync);
        await RunAsync("reads Codex token totals from zstd compressed history", TestCodexCompressedHistoryAsync);
        await RunAsync("applies model alias pricing", TestModelAliasPricingAsync);
        await RunAsync("applies DeepSeek peak and off-peak pricing", TestDeepSeekPeakPricingAsync);
        await RunAsync("collects local Grok Build token statistics", TestGrokTokenCostCollectorAsync);
        await RunAsync("keeps Grok ticks and prices normalized unknown-safe models", TestGrokBillingEdgesAsync);
        await RunAsync("ignores OpenCode token cost for Codex", TestOpenCodeTokenCostIgnoredAsync);
        await RunAsync("counts live subagent usage without replaying parent history", TestSubagentTokenCostAsync);
        s_Failures += await AppUpdateTests.RunAllAsync();
        Console.WriteLine(s_Failures == 0 ? "All C# tests passed." : $"C# tests failed: {s_Failures}");
        return s_Failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Tests visible-source tooltip selection, failures, and maximum numeric lengths without creating a tray icon.
    /// </summary>
    private static Task TestTrayTooltipAsync()
    {
        UsageResponse usage = new()
        {
            Available = true,
            Error = "must-not-appear",
            Limits = new UsageLimits
            {
                Weekly = new UsageLimit { RemainingPercent = 100, WindowMinutes = 10_080 },
                GrokWeekly = new UsageLimit { RemainingPercent = 0 },
            },
            Display = new UsageDisplay { GrokWeekly = "0%", DeepSeek = "¥123" },
        };
        AssertEqual("Codex W: 100%\nGrok W: 0%\nDeepSeek: ¥123", TrayController.BuildTrayTooltip(PageItem.All, usage), "all visible source summary");
        AssertEqual("Grok W: 0%", TrayController.BuildTrayTooltip(PageItem.Grok, usage), "hidden sources are absent");
        AssertEqual(CodexTrayDefaults.AppName, TrayController.BuildTrayTooltip((PageItem)2, usage), "retired page bits add no tray quota line");
        usage.Limits.Weekly.WindowMinutes = 0;
        AssertEqual("Codex W: N/A", TrayController.BuildTrayTooltip(PageItem.Codex, usage), "a session-only Codex result cannot invent a weekly quota");
        usage.Limits.Weekly.WindowMinutes = 10_080;
        AssertEqual(CodexTrayDefaults.AppName, TrayController.BuildTrayTooltip(PageItem.None, usage), "no visible sources");
        AssertEqual("Codex W: N/A\nGrok W: N/A\nDeepSeek: N/A", TrayController.BuildTrayTooltip(PageItem.All, null), "initial unavailable summary");
        usage.Available = false;
        usage.Display.GrokWeekly = "N/A";
        AssertTrue(TrayController.BuildTrayTooltip(PageItem.All, usage).StartsWith("Codex W: N/A\nGrok W: N/A"), "failed sources do not show old percentages");
        usage.Available = true;
        usage.Limits.Weekly.RemainingPercent = 999;
        usage.Limits.GrokWeekly.RemainingPercent = 100;
        usage.Display.GrokWeekly = "100%";
        usage.Display.DeepSeek = "¥79228162514264337593543950335";
        string bounded = TrayController.BuildTrayTooltip(PageItem.All, usage);
        AssertTrue(bounded.Length <= 63 && bounded.EndsWith('…') && !bounded.Contains(usage.Error!), "tray text is bounded and excludes errors");
        usage.Display.DeepSeek = "credential-secret";
        AssertEqual("DeepSeek: N/A", TrayController.BuildTrayTooltip(PageItem.Apis, usage), "only numeric CNY balances are accepted");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests loading the popup XAML with the fixed window dimensions.
    /// </summary>
    private static Task TestPopupFixedSizeAsync()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                AppSettings settings = new AppSettings
                {
                    ApiMonitors = [new ApiMonitorSettings()],
                };
                TrayPopupViewModel viewModel = new(settings, () => Task.CompletedTask);
                AssertEqual("Check for updates", viewModel.UpdateActionText, "update action");
                AssertEqual("Weekly", viewModel.CodexWeeklyQuota.Title, "Codex weekly quota title");
                AssertEqual("Session", viewModel.CodexSessionQuota.Title, "Codex session quota title");
                AssertEqual("Weekly", viewModel.GrokWeeklyQuota.Title, "Grok weekly quota title");
                viewModel.UpdateStatus(
                    isRunning: true,
                    CodexTrayDefaults.Port,
                    new UsageResponse
                    {
                        Available = true,
                        Limits = new UsageLimits
                        {
                            Weekly = new UsageLimit { WindowMinutes = 10_080, RemainingPercent = 98 },
                        },
                        ResetCredits = new ResetCredits { Available = true, AvailableCount = 1 },
                    },
                    error: null);
                viewModel.UpdateTokenCost(CreateTokenCostStatistics(10));
                TrayPopupWindow window = new(viewModel);
                AssertTrue(!viewModel.IsPanelPinned, "panel starts unpinned");
                viewModel.TogglePanelPinnedCommand.Execute(null);
                AssertEqual("Unpin panel", viewModel.PanelPinTooltip, "pin toggle action");
                typeof(TrayPopupWindow).GetMethod("OnDeactivated", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [EventArgs.Empty]);
                AssertEqual(DateTime.MinValue, window.LastDeactivatedHideUtc, "pinned panel must ignore focus loss");
                viewModel.TogglePanelPinnedCommand.Execute(null);
                typeof(TrayPopupWindow).GetMethod("OnDeactivated", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [EventArgs.Empty]);
                AssertTrue(window.LastDeactivatedHideUtc != DateTime.MinValue, "unpinned panel preserves focus-loss hiding");
                AssertTrue(!new TrayPopupViewModel(settings, () => Task.CompletedTask).IsPanelPinned, "pin state is not persisted in settings");
                ApiMonitorViewModel credentialCard = new(new ApiMonitorSettings { ApiKey = "test-credential" });
                CredentialInput credential = new();
                credential.SetBinding(CredentialInput.TextProperty, new System.Windows.Data.Binding(nameof(ApiMonitorViewModel.ApiKey))
                {
                    Source = credentialCard, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
                });
                System.Windows.Controls.PasswordBox masked = (System.Windows.Controls.PasswordBox)credential.FindName("MaskedInput");
                System.Windows.Controls.TextBox revealed = (System.Windows.Controls.TextBox)credential.FindName("RevealedInput");
                System.Windows.Controls.Button revealButton = (System.Windows.Controls.Button)credential.FindName("RevealButton");
                System.Windows.Shapes.Path hiddenEyeSlash = (System.Windows.Shapes.Path)credential.FindName("HiddenEyeSlash");
                AssertEqual(System.Windows.Visibility.Collapsed, revealed.Visibility, "credential must begin masked");
                AssertEqual("test-credential", masked.Password, "masked field must load the saved credential");
                masked.Password = "edited-credential";
                AssertEqual("edited-credential", credentialCard.ApiKey, "masked editing must update the card binding");
                revealButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                AssertEqual(System.Windows.Visibility.Visible, revealed.Visibility, "explicit reveal must show the editable text");
                AssertEqual(System.Windows.Visibility.Visible, hiddenEyeSlash.Visibility, "revealed credential must offer the crossed eye icon");
                AssertEqual("Hide credential", System.Windows.Automation.AutomationProperties.GetName(revealButton), "reveal action must remain accessible");
                revealed.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "revealed-edit");
                AssertEqual("revealed-edit", masked.Password, "revealed editing must synchronize the masked field");
                AssertEqual("revealed-edit", credentialCard.ApiKey, "revealed editing must update the persisted card source");
                revealButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                masked.Password = "masked-again";
                AssertEqual("masked-again", credentialCard.ApiKey, "editing after hiding must still update the source");
                revealButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                credentialCard.ApiKey = "external-edit";
                AssertEqual("external-edit", masked.Password, "card changes must synchronize the masked field");
                credential.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.UnloadedEvent));
                AssertEqual(System.Windows.Visibility.Collapsed, revealed.Visibility, "ending editing must clear reveal state");
                AssertEqual(System.Windows.Visibility.Collapsed, hiddenEyeSlash.Visibility, "hidden credential must offer the open eye icon");
                AssertEqual("Show credential", System.Windows.Automation.AutomationProperties.GetName(revealButton), "hidden credential must expose the reveal action");
                AssertEqual(CodexTrayDefaults.PopupWindowWidth, window.Width, "fixed popup width");
                AssertEqual(CodexTrayDefaults.PopupWindowHeight, window.Height, "fixed popup height");
                AssertEqual(window.Width, window.MinWidth, "fixed popup minimum width");
                AssertEqual(window.Width, window.MaxWidth, "fixed popup maximum width");
                AssertEqual(window.Height, window.MinHeight, "fixed popup minimum height");
                AssertEqual(window.Height, window.MaxHeight, "fixed popup maximum height");

                System.Windows.FrameworkElement content = (System.Windows.FrameworkElement)window.Content;
                content.Measure(new System.Windows.Size(window.Width, window.Height));
                content.Arrange(new System.Windows.Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                System.Windows.Controls.ScrollViewer codexQuotaScrollViewer = (System.Windows.Controls.ScrollViewer)window.FindName("CodexQuotaScrollViewer");
                AssertTrue(codexQuotaScrollViewer.ScrollableHeight == 0, $"default Codex quota cards should not scroll, actual {codexQuotaScrollViewer.ScrollableHeight}");
                System.Windows.Controls.Border codexPrimaryCard = (System.Windows.Controls.Border)window.FindName("CodexPrimaryCard");
                System.Windows.Controls.Border codexResetCreditsCard = (System.Windows.Controls.Border)window.FindName("CodexResetCreditsCard");
                AssertEqual(91d, codexPrimaryCard.ActualHeight, "Codex primary card height");
                AssertEqual(68d, codexResetCreditsCard.ActualHeight, "Codex small card height");
                AssertEqual(codexPrimaryCard.ActualWidth, codexResetCreditsCard.ActualWidth, "Codex Resets should use the full card width");
                System.Windows.Controls.ContentControl codexFirstQuotaContent = FindNamedDescendant<System.Windows.Controls.ContentControl>(codexPrimaryCard, "FirstQuotaContent");
                System.Windows.Controls.ContentControl codexSecondQuotaContent = FindNamedDescendant<System.Windows.Controls.ContentControl>(codexPrimaryCard, "SecondQuotaContent");
                System.Windows.Controls.StackPanel codexPageIndicators = FindNamedDescendant<System.Windows.Controls.StackPanel>(codexPrimaryCard, "PageIndicators");
                System.Windows.Controls.Button codexFirstPageButton = FindNamedDescendant<System.Windows.Controls.Button>(codexPrimaryCard, "FirstPageButton");
                System.Windows.Controls.Button codexSecondPageButton = FindNamedDescendant<System.Windows.Controls.Button>(codexPrimaryCard, "SecondPageButton");
                System.Windows.Shapes.Ellipse codexFirstPageIndicator = FindNamedDescendant<System.Windows.Shapes.Ellipse>(codexFirstPageButton, "PageIndicator");
                System.Windows.Shapes.Ellipse codexSecondPageIndicator = FindNamedDescendant<System.Windows.Shapes.Ellipse>(codexSecondPageButton, "PageIndicator");
                AssertTrue(ReferenceEquals(codexFirstQuotaContent.Content, viewModel.CodexSessionQuota), "Codex Session should be the first large-card page");
                AssertTrue(ReferenceEquals(codexSecondQuotaContent.Content, viewModel.CodexWeeklyQuota), "Codex Weekly should be the second large-card page");
                AssertTrue(!viewModel.CodexSessionQuota.IsVisible, "invalid Codex Session should be hidden");
                AssertEqual(System.Windows.Visibility.Collapsed, codexFirstQuotaContent.Visibility, "invalid Codex Session content should be hidden");
                AssertEqual(System.Windows.Visibility.Visible, codexSecondQuotaContent.Visibility, "Codex Weekly should replace an invalid Session");
                AssertEqual(System.Windows.Visibility.Collapsed, codexPageIndicators.Visibility, "single Codex quota page should hide page indicators");
                System.Windows.Input.MouseWheelEventArgs singlePageWheelArgs = new(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    -120)
                {
                    RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent,
                };
                codexPrimaryCard.RaiseEvent(singlePageWheelArgs);
                AssertTrue(!singlePageWheelArgs.Handled, "single Codex quota page should ignore mouse wheel switching");
                AssertEqual(30, viewModel.CodexTokenCost.ChartDays.Count, "Codex token cost chart day count");
                AssertTrue(viewModel.CodexTokenCost.DonutSegments.Count > 0, "Codex model donut should have segments");
                System.Windows.Controls.ContentControl codexTokenCostDashboard = (System.Windows.Controls.ContentControl)window.FindName("CodexTokenCostDashboard");
                System.Windows.Controls.Grid codexTokenCostChartFrame = FindNamedDescendant<System.Windows.Controls.Grid>(codexTokenCostDashboard, "TokenCostChartFrame");
                System.Windows.Controls.Grid codexTokenCostChartPlot = FindNamedDescendant<System.Windows.Controls.Grid>(codexTokenCostDashboard, "TokenCostChartPlot");
                System.Windows.Controls.Grid codexPageGrid = (System.Windows.Controls.Grid)window.FindName("CodexPageGrid");
                System.Windows.Controls.Grid codexTokenCostOverview = FindNamedDescendant<System.Windows.Controls.Grid>(codexTokenCostDashboard, "TokenCostOverview");
                System.Windows.Controls.Grid codexTokenCostDonutArea = FindNamedDescendant<System.Windows.Controls.Grid>(codexTokenCostDashboard, "TokenCostDonutArea");
                System.Windows.Controls.Button codexTokenCostChartPeriodButton = FindNamedDescendant<System.Windows.Controls.Button>(codexTokenCostDashboard, "TokenCostChartPeriodButton");
                double[] codexSectionHeights = codexPageGrid.RowDefinitions.Select(row => row.ActualHeight).ToArray();
                AssertEqual(160d, codexTokenCostOverview.ActualHeight, "Codex token cost overview height");
                AssertEqual(86d, codexTokenCostChartFrame.ActualHeight, "Codex token cost chart frame height");
                AssertEqual(69d, codexTokenCostChartPlot.ActualHeight, "Codex token cost chart plot height");
                AssertEqual(22d, codexTokenCostChartPeriodButton.ActualHeight, "Codex token cost chart period button height");
                AssertEqual("\uE8AB", codexTokenCostChartPeriodButton.Content, "Codex token cost chart period button glyph");
                AssertTrue(codexTokenCostChartPeriodButton.ToolTip is null, "Codex token cost chart period button should not show a tooltip");
                AssertTrue(codexTokenCostOverview.ToolTip is null, "token cost overview should not show a tooltip");
                System.Windows.Controls.ItemsControl codexTokenCostPeriodList = FindNamedDescendant<System.Windows.Controls.ItemsControl>(codexTokenCostDashboard, "TokenCostPeriodList");
                AssertTrue(codexTokenCostPeriodList.ToolTip is null, "period list should not show a tooltip");
                System.Windows.Controls.TextBlock codexTokenCostCenterCost = FindNamedDescendant<System.Windows.Controls.TextBlock>(codexTokenCostDashboard, "TokenCostCenterCost");
                AssertTrue(codexTokenCostCenterCost.ToolTip is null, "donut center should not show a tooltip");
                System.Windows.Controls.TextBlock codexUpdatedAtText = (System.Windows.Controls.TextBlock)window.FindName("CodexUpdatedAtText");
                AssertTrue(codexUpdatedAtText.ToolTip is null, "Codex updated time should not show a tooltip");
                System.Windows.Controls.StackPanel grokUpdatedStatus = (System.Windows.Controls.StackPanel)window.FindName("GrokUpdatedStatus");
                AssertTrue(grokUpdatedStatus.ToolTip is null, "Grok updated time should not show a tooltip");
                AssertTrue(
                    ReferenceEquals(System.Windows.Media.VisualTreeHelper.GetParent(codexTokenCostChartPeriodButton), codexTokenCostDonutArea),
                    "Codex token cost chart period button should be hosted by the donut area");

                viewModel.UpdateGrokDashboard(new GrokUsageDashboard(
                    new GrokUsageSnapshot(5, DateTimeOffset.Now.AddDays(7).ToUnixTimeSeconds(), "SuperGrok Heavy")
                    {
                        ProductUsage =
                        [
                            new GrokProductUsage("GrokChat", 3),
                            new GrokProductUsage("PRODUCT_GROK_BUILD", 2),
                            new GrokProductUsage("GrokTasks", 1),
                            new GrokProductUsage("GrokVoice", 0.5),
                            new GrokProductUsage("GrokImagine", 0),
                        ],
                    },
                    string.Empty,
                    DateTimeOffset.Now),
                    CreateTokenCostStatistics(5));
                viewModel.UpdateStatus(isRunning: false, CodexTrayDefaults.Port, response: null, error: "expected test error");
                viewModel.ShowGrok();
                content.InvalidateMeasure();
                content.Measure(new System.Windows.Size(window.Width, window.Height));
                content.Arrange(new System.Windows.Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                System.Windows.Controls.ScrollViewer grokQuotaScrollViewer = (System.Windows.Controls.ScrollViewer)window.FindName("GrokQuotaScrollViewer");
                AssertTrue(grokQuotaScrollViewer.ScrollableHeight == 0, $"Grok quota cards should not scroll, actual {grokQuotaScrollViewer.ScrollableHeight}");
                System.Windows.Controls.StackPanel grokQuotaCardPanel = (System.Windows.Controls.StackPanel)window.FindName("GrokQuotaCardPanel");
                AssertEqual(2, grokQuotaCardPanel.Children.Count, "Grok quota card count");
                System.Windows.Controls.Border grokWeeklyCard = (System.Windows.Controls.Border)window.FindName("GrokWeeklyCard");
                System.Windows.Controls.Border grokProductUsageCard = (System.Windows.Controls.Border)window.FindName("GrokProductUsageCard");
                System.Windows.Controls.Border grokProductUsageBar = (System.Windows.Controls.Border)window.FindName("GrokProductUsageBar");
                System.Windows.Controls.ItemsControl grokProductUsageBarItems = (System.Windows.Controls.ItemsControl)window.FindName("GrokProductUsageBarItems");
                System.Windows.Controls.ItemsControl grokProductUsageLegend = (System.Windows.Controls.ItemsControl)window.FindName("GrokProductUsageLegend");
                System.Windows.Controls.Border grokProductUsageBarSegment = FindItemsControlTemplateChild<System.Windows.Controls.Border>(grokProductUsageBarItems, 0, "GrokProductUsageBarSegment");
                System.Windows.Shapes.Ellipse grokProductUsageLegendDot = FindItemsControlTemplateChild<System.Windows.Shapes.Ellipse>(grokProductUsageLegend, 0, "GrokProductUsageLegendDot");
                System.Windows.Controls.TextBlock grokProductUsageNameText = FindItemsControlTemplateChild<System.Windows.Controls.TextBlock>(grokProductUsageLegend, 0, "GrokProductUsageNameText");
                System.Windows.Controls.TextBlock grokProductUsagePercentText = FindItemsControlTemplateChild<System.Windows.Controls.TextBlock>(grokProductUsageLegend, 0, "GrokProductUsagePercentText");
                AssertEqual(codexPrimaryCard.ActualHeight, grokWeeklyCard.ActualHeight, "Codex and Grok Weekly card heights");
                AssertEqual(codexResetCreditsCard.ActualHeight, grokProductUsageCard.ActualHeight, "Codex and Grok small card heights");
                AssertEqual(0d, grokQuotaScrollViewer.ViewportHeight - grokQuotaScrollViewer.ExtentHeight, "Grok quota cards should fill the reserved region");
                AssertEqual(4, viewModel.GrokTokenCost.Rows.Count, "Grok token cost row count");
                AssertEqual(30, viewModel.GrokTokenCost.ChartDays.Count, "Grok token cost chart day count");
                AssertEqual("SUPERGROK HEAVY", viewModel.GrokPlanDisplay, "Grok subscription badge");
                AssertTrue(window.FindName("GrokProductUsageTitle") is null, "Grok product usage card should not show a title");
                AssertEqual("Chat 3%\nBuild 2%\nTasks 1%\nVoice 0.5%\nImagine 0%", viewModel.GrokProductUsageBarTooltip, "Grok product usage bar tooltip lists every product");
                AssertEqual(3, viewModel.GrokProductUsageItems.Count, "Grok product usage legend item count");
                AssertEqual(4, viewModel.GrokProductUsageBarItems.Count, "Grok product usage bar item count");
                AssertEqual("Chat", viewModel.GrokProductUsageItems[0].Name, "Grok product usage first name");
                AssertEqual("3%", viewModel.GrokProductUsageItems[0].PercentText, "Grok product usage first percent");
                AssertEqual(3d, viewModel.GrokProductUsageItems[0].UsedPercent, "Grok product usage first used percent");
                AssertEqual("Build", viewModel.GrokProductUsageItems[1].Name, "Grok product usage second name");
                AssertEqual("Tasks", viewModel.GrokProductUsageItems[2].Name, "Grok product usage third name");
                AssertEqual("1%", viewModel.GrokProductUsageItems[2].PercentText, "Grok product usage third percent");
                AssertEqual("Others", viewModel.GrokProductUsageBarItems[3].Name, "Grok product usage others bar name");
                AssertEqual("0.5%", viewModel.GrokProductUsageBarItems[3].PercentText, "Grok product usage others percent");
                System.Windows.Controls.ProgressBar grokWeeklyProgressBar = FindTypedDescendant<System.Windows.Controls.ProgressBar>(grokWeeklyCard);
                AssertEqual(grokWeeklyProgressBar.ActualHeight, grokProductUsageBar.ActualHeight, "Grok product usage bar should match quota bar height");
                AssertEqual(grokWeeklyProgressBar.ActualHeight, grokProductUsageBarSegment.ActualHeight, "Grok product usage segments should match quota bar height");
                AssertEqual(4, grokProductUsageBarItems.Items.Count, "Grok product usage bar segment count");
                AssertEqual(3, grokProductUsageLegend.Items.Count, "Grok product usage legend count");
                AssertTrue(FindTypedDescendant<System.Windows.Controls.Primitives.UniformGrid>(grokProductUsageLegend) is not null, "Grok product usage legend should keep three items on one row");
                AssertEqual("Chat", grokProductUsageNameText.Text, "Grok product usage legend name");
                AssertEqual("3%", grokProductUsagePercentText.Text, "Grok product usage legend percent");
                System.Windows.Controls.TextBlock codexResetCreditsExpiryDatesText = (System.Windows.Controls.TextBlock)window.FindName("CodexResetCreditsExpiryDatesText");
                System.Windows.Style usageCardCaptionTextStyle = (System.Windows.Style)window.FindResource("UsageCardCaptionTextStyle");
                AssertTrue(ReferenceEquals(grokProductUsageNameText.Style, usageCardCaptionTextStyle), "Grok product usage name should use the reset-credit caption style");
                AssertTrue(ReferenceEquals(grokProductUsagePercentText.Style, usageCardCaptionTextStyle), "Grok product usage percent should use the reset-credit caption style");
                AssertEqual(codexResetCreditsExpiryDatesText.FontSize, grokProductUsageNameText.FontSize, "Grok product usage name font size");
                AssertEqual(codexResetCreditsExpiryDatesText.FontSize, grokProductUsagePercentText.FontSize, "Grok product usage percent font size");
                AssertEqual(codexResetCreditsExpiryDatesText.FontWeight, grokProductUsageNameText.FontWeight, "Grok product usage name font weight");
                AssertEqual(codexResetCreditsExpiryDatesText.FontWeight, grokProductUsagePercentText.FontWeight, "Grok product usage percent font weight");
                AssertEqual(
                    ((System.Windows.Media.SolidColorBrush)codexResetCreditsExpiryDatesText.Foreground).Color,
                    ((System.Windows.Media.SolidColorBrush)grokProductUsageNameText.Foreground).Color,
                    "Grok product usage name color");
                AssertEqual(
                    ((System.Windows.Media.SolidColorBrush)codexResetCreditsExpiryDatesText.Foreground).Color,
                    ((System.Windows.Media.SolidColorBrush)grokProductUsagePercentText.Foreground).Color,
                    "Grok product usage percent color");
                AssertEqual(System.Windows.TextWrapping.NoWrap, grokProductUsageNameText.TextWrapping, "Grok product usage name should not wrap inside an item");
                AssertEqual(System.Windows.TextWrapping.NoWrap, grokProductUsagePercentText.TextWrapping, "Grok product usage percent should not wrap inside an item");
                System.Windows.Controls.TextBlock grokProductUsageThirdNameText = FindItemsControlTemplateChild<System.Windows.Controls.TextBlock>(grokProductUsageLegend, 2, "GrokProductUsageNameText");
                AssertEqual("Tasks", grokProductUsageThirdNameText.Text, "Grok product usage third legend name");
                AssertEqual(
                    grokProductUsageNameText.TranslatePoint(new System.Windows.Point(), window).Y,
                    grokProductUsagePercentText.TranslatePoint(new System.Windows.Point(), window).Y,
                    "Grok product usage legend item should stay on one line");
                AssertEqual(
                    grokProductUsageNameText.TranslatePoint(new System.Windows.Point(), window).Y,
                    grokProductUsageThirdNameText.TranslatePoint(new System.Windows.Point(), window).Y,
                    "Grok product usage legend items should share one row");
                System.Windows.Controls.StackPanel grokProductUsageChatLegendItem = FindItemsControlTemplateChild<System.Windows.Controls.StackPanel>(grokProductUsageLegend, 0, "GrokProductUsageLegendItem");
                System.Windows.Controls.StackPanel grokProductUsageThirdLegendItem = FindItemsControlTemplateChild<System.Windows.Controls.StackPanel>(grokProductUsageLegend, 2, "GrokProductUsageLegendItem");
                AssertTrue(grokProductUsageLegend.ToolTip is null, "Grok product usage legend should not show a shared tooltip");
                AssertTrue(grokProductUsageChatLegendItem.ToolTip is null, "Grok product usage chat item should not show a tooltip");
                AssertTrue(grokProductUsageThirdLegendItem.ToolTip is null, "Grok product usage legend items should not show a tooltip");
                AssertEqual("Chat 3%\nBuild 2%\nTasks 1%\nVoice 0.5%\nImagine 0%", grokProductUsageBar.ToolTip, "Grok product usage bar should list every product");
                AssertEqual(
                    System.Windows.Media.Color.FromRgb(26, 188, 137),
                    ((System.Windows.Media.SolidColorBrush)viewModel.GrokProductUsageItems[0].Brush).Color,
                    "Grok product usage first color");
                AssertEqual(
                    System.Windows.Media.Color.FromRgb(82, 193, 181),
                    ((System.Windows.Media.SolidColorBrush)viewModel.GrokProductUsageItems[1].Brush).Color,
                    "Grok product usage second color");
                AssertEqual(
                    System.Windows.Media.Color.FromRgb(92, 143, 202),
                    ((System.Windows.Media.SolidColorBrush)viewModel.GrokProductUsageItems[2].Brush).Color,
                    "Grok product usage third color");
                AssertEqual(
                    System.Windows.Media.Color.FromRgb(166, 173, 179),
                    ((System.Windows.Media.SolidColorBrush)viewModel.GrokProductUsageBarItems[3].Brush).Color,
                    "Grok product usage others color");
                AssertEqual(
                    ((System.Windows.Media.SolidColorBrush)viewModel.GrokTokenCost.DonutSegments[0].Brush).Color,
                    ((System.Windows.Media.SolidColorBrush)viewModel.GrokProductUsageItems[0].Brush).Color,
                    "Grok product usage first color should match the donut");
                AssertEqual(
                    ((System.Windows.Media.SolidColorBrush)viewModel.GrokTokenCost.DonutSegments[1].Brush).Color,
                    ((System.Windows.Media.SolidColorBrush)viewModel.GrokProductUsageItems[1].Brush).Color,
                    "Grok product usage second color should match the donut");
                AssertEqual(
                    ((System.Windows.Media.SolidColorBrush)viewModel.GrokProductUsageItems[0].Brush).Color,
                    ((System.Windows.Media.SolidColorBrush)grokProductUsageBarSegment.Background).Color,
                    "Grok product usage bar segment color");
                AssertEqual(
                    ((System.Windows.Media.SolidColorBrush)viewModel.GrokProductUsageItems[0].Brush).Color,
                    ((System.Windows.Media.SolidColorBrush)grokProductUsageLegendDot.Fill).Color,
                    "Grok product usage legend dot color");
                System.Windows.Controls.Grid grokPageGrid = (System.Windows.Controls.Grid)window.FindName("GrokPageGrid");
                System.Windows.Controls.ContentControl grokTokenCostDashboard = (System.Windows.Controls.ContentControl)window.FindName("GrokTokenCostDashboard");
                AssertTrue(ReferenceEquals(codexTokenCostDashboard.ContentTemplate, grokTokenCostDashboard.ContentTemplate), "Codex and Grok should share the token cost template");
                AssertEqual(codexPageGrid.RowDefinitions.Count, grokPageGrid.RowDefinitions.Count, "Codex and Grok section row count");
                AssertTrue(codexSectionHeights.SequenceEqual(grokPageGrid.RowDefinitions.Select(row => row.ActualHeight)), "Codex and Grok section heights");
                System.Windows.Controls.ItemsControl grokChartItemsControl = FindNamedDescendant<System.Windows.Controls.ItemsControl>(grokTokenCostDashboard, "TokenCostChartItemsControl");
                System.Windows.Controls.ContentPresenter grokChartPresenter = (System.Windows.Controls.ContentPresenter)grokChartItemsControl.ItemContainerGenerator.ContainerFromIndex(29);
                System.Windows.Controls.Border grokChartBar = (System.Windows.Controls.Border)grokChartItemsControl.ItemTemplate.FindName("TokenCostChartBar", grokChartPresenter);
                System.Windows.Shapes.Path grokPeriodSelectionIndicator = FindNamedDescendant<System.Windows.Shapes.Path>(grokTokenCostDashboard, "SelectionIndicator");
                System.Windows.Media.Color tokenCostAccentColor = System.Windows.Media.Color.FromRgb(26, 188, 137);
                AssertEqual(tokenCostAccentColor, ((System.Windows.Media.SolidColorBrush)grokChartBar.Background).Color, "Grok token chart should keep the green accent");
                AssertEqual(tokenCostAccentColor, ((System.Windows.Media.SolidColorBrush)grokPeriodSelectionIndicator.Fill).Color, "Grok period selection indicator should keep the green accent");
                AssertEqual(tokenCostAccentColor, ((System.Windows.Media.SolidColorBrush)viewModel.GrokTokenCost.AccentBrush).Color, "Grok token cost accent should stay green");
                AssertEqual(
                    System.Windows.Media.Color.FromRgb(224, 91, 77),
                    ((System.Windows.Media.SolidColorBrush)viewModel.CodexStatusDotBrush).Color,
                    "Codex status should stay independent from token cost accent");
                System.Windows.Controls.Button grokTabButton = (System.Windows.Controls.Button)window.FindName("GrokTabButton");
                System.Windows.Controls.StackPanel tabPanel = (System.Windows.Controls.StackPanel)grokTabButton.Parent;
                int grokTabIndex = tabPanel.Children.IndexOf(grokTabButton);
                AssertEqual("Codex", ((System.Windows.Controls.Button)tabPanel.Children[grokTabIndex - 1]).ToolTip, "Grok tab predecessor");
                AssertEqual("APIs", ((System.Windows.Controls.Button)tabPanel.Children[grokTabIndex + 1]).ToolTip, "Grok tab successor");
                viewModel.UpdateGrokDashboard(new GrokUsageDashboard(
                    new GrokUsageSnapshot(5, DateTimeOffset.Now.AddDays(7).ToUnixTimeSeconds(), "SuperGrok Heavy")
                    {
                        ProductUsage =
                        [
                            new GrokProductUsage("GrokChat", 3),
                            new GrokProductUsage("PRODUCT_GROK_BUILD", 2),
                        ],
                    },
                    string.Empty,
                    DateTimeOffset.Now),
                    CreateTokenCostStatistics(5));
                AssertEqual("Chat 3%\nBuild 2%", viewModel.GrokProductUsageBarTooltip, "Grok product usage bar tooltip with two items");
                AssertEqual(3, viewModel.GrokProductUsageItems.Count, "Grok product usage item count with two items");
                AssertEqual(3, viewModel.GrokProductUsageBarItems.Count, "Grok product usage bar item count with two items");
                AssertEqual("Chat", viewModel.GrokProductUsageItems[0].Name, "Grok product usage first name with two items");
                AssertEqual("Build", viewModel.GrokProductUsageItems[1].Name, "Grok product usage second name with two items");
                AssertEqual("Bot", viewModel.GrokProductUsageItems[2].Name, "Grok product usage third name with two items");
                AssertEqual("0%", viewModel.GrokProductUsageItems[2].PercentText, "Grok product usage bot percent with two items");
                viewModel.UpdateGrokDashboard(new GrokUsageDashboard(
                    new GrokUsageSnapshot(0, DateTimeOffset.Now.AddDays(7).ToUnixTimeSeconds(), "SuperGrok Heavy")
                    {
                        ProductUsage =
                        [
                            new GrokProductUsage("GrokChat", 0),
                            new GrokProductUsage("PRODUCT_GROK_BUILD", 0),
                            new GrokProductUsage("GrokTasks", 0),
                        ],
                    },
                    string.Empty,
                    DateTimeOffset.Now),
                    CreateTokenCostStatistics(5));
                AssertEqual("Build", viewModel.GrokProductUsageItems[0].Name, "Grok product usage first name when all zero");
                AssertEqual("Bot", viewModel.GrokProductUsageItems[1].Name, "Grok product usage second name when all zero");
                AssertEqual("Cursor", viewModel.GrokProductUsageItems[2].Name, "Grok product usage third name when all zero");
                AssertEqual("0%", viewModel.GrokProductUsageItems[0].PercentText, "Grok product usage build percent when all zero");
                AssertEqual("0%", viewModel.GrokProductUsageItems[1].PercentText, "Grok product usage bot percent when all zero");
                AssertEqual("0%", viewModel.GrokProductUsageItems[2].PercentText, "Grok product usage cursor percent when all zero");
                AssertEqual("Build 0%\nChat 0%\nTasks 0%", viewModel.GrokProductUsageBarTooltip, "Grok product usage bar tooltip when all zero");
                AssertEqual(3, viewModel.GrokProductUsageBarItems.Count, "Grok product usage bar item count when all zero");
                viewModel.UpdateGrokDashboard(new GrokUsageDashboard(
                    new GrokUsageSnapshot(5, DateTimeOffset.Now.AddDays(7).ToUnixTimeSeconds(), "SuperGrok Heavy")
                    {
                        ProductUsage =
                        [
                            new GrokProductUsage("GrokBuild", 5),
                            new GrokProductUsage("GrokChat", 0),
                        ],
                    },
                    string.Empty,
                    DateTimeOffset.Now),
                    CreateTokenCostStatistics(5));
                AssertEqual("Build", viewModel.GrokProductUsageItems[0].Name, "Grok product usage first name when only build is used");
                AssertEqual("5%", viewModel.GrokProductUsageItems[0].PercentText, "Grok product usage build percent when only build is used");
                AssertEqual("Bot", viewModel.GrokProductUsageItems[1].Name, "Grok product usage second name when only build is used");
                AssertEqual("0%", viewModel.GrokProductUsageItems[1].PercentText, "Grok product usage bot percent when only build is used");
                AssertEqual("Cursor", viewModel.GrokProductUsageItems[2].Name, "Grok product usage third name when only build is used");
                AssertEqual("0%", viewModel.GrokProductUsageItems[2].PercentText, "Grok product usage cursor percent when only build is used");
                AssertEqual("Build 5%\nChat 0%", viewModel.GrokProductUsageBarTooltip, "Grok product usage bar tooltip when only build is used");
                viewModel.UpdateGrokDashboard(new GrokUsageDashboard(
                    new GrokUsageSnapshot(26, DateTimeOffset.Now.AddDays(7).ToUnixTimeSeconds(), "SuperGrok Heavy")
                    {
                        ProductUsage =
                        [
                            new GrokProductUsage("GrokBuild", 25),
                            new GrokProductUsage("GrokBot", 1),
                        ],
                    },
                    string.Empty,
                    DateTimeOffset.Now),
                    CreateTokenCostStatistics(5));
                AssertEqual("Build", viewModel.GrokProductUsageItems[0].Name, "Grok product usage first name with build and bot");
                AssertEqual("25%", viewModel.GrokProductUsageItems[0].PercentText, "Grok product usage build percent with build and bot");
                AssertEqual("Bot", viewModel.GrokProductUsageItems[1].Name, "Grok product usage second name with build and bot");
                AssertEqual("1%", viewModel.GrokProductUsageItems[1].PercentText, "Grok product usage bot percent with build and bot");
                AssertEqual("Cursor", viewModel.GrokProductUsageItems[2].Name, "Grok product usage third name with build and bot");
                AssertEqual("0%", viewModel.GrokProductUsageItems[2].PercentText, "Grok product usage cursor percent with build and bot");
                AssertEqual("Build 25%\nBot 1%", viewModel.GrokProductUsageBarTooltip, "Grok product usage bar tooltip with build and bot");
                viewModel.UpdateGrokDashboard(new GrokUsageDashboard(
                    new GrokUsageSnapshot(7, DateTimeOffset.Now.AddDays(7).ToUnixTimeSeconds(), "SuperGrok Heavy")
                    {
                        ProductUsage =
                        [
                            new GrokProductUsage("GrokImagine", 7),
                            new GrokProductUsage("PRODUCT_GROK_BUILD", 0),
                            new GrokProductUsage("GrokChat", 0),
                        ],
                    },
                    string.Empty,
                    DateTimeOffset.Now),
                    CreateTokenCostStatistics(5));
                AssertEqual("Imagine", viewModel.GrokProductUsageItems[0].Name, "Grok product usage first name with one non-zero item");
                AssertEqual("7%", viewModel.GrokProductUsageItems[0].PercentText, "Grok product usage first percent with one non-zero item");
                AssertEqual("Build", viewModel.GrokProductUsageItems[1].Name, "Grok product usage build name with one non-zero item");
                AssertEqual("0%", viewModel.GrokProductUsageItems[1].PercentText, "Grok product usage build percent with one non-zero item");
                AssertEqual("Bot", viewModel.GrokProductUsageItems[2].Name, "Grok product usage bot name with one non-zero item");
                AssertEqual("0%", viewModel.GrokProductUsageItems[2].PercentText, "Grok product usage bot percent with one non-zero item");
                AssertEqual("Imagine 7%\nBuild 0%\nChat 0%", viewModel.GrokProductUsageBarTooltip, "Grok product usage bar tooltip with one non-zero item");

                AssertEqual("SUPERGROK HEAVY", viewModel.GrokPlanDisplay, "Grok badge uses the billing subscription tier");
                AssertEqual("93%", viewModel.GrokWeeklyQuota.PercentText, "billing weekly remaining percent");
                viewModel.UpdateGrokDashboard(new GrokUsageDashboard(
                    new GrokUsageSnapshot(37, DateTimeOffset.Now.AddDays(7).ToUnixTimeSeconds(), "SuperGrok Heavy")
                    {
                        ProductUsage =
                        [
                            new GrokProductUsage("GrokBuild", 29),
                            new GrokProductUsage("", 5),
                            new GrokProductUsage("GrokBot", 2),
                            new GrokProductUsage("GrokImagine", 1),
                        ],
                    },
                    string.Empty,
                    DateTimeOffset.Now),
                    CreateTokenCostStatistics(5));
                AssertEqual("Build", viewModel.GrokProductUsageItems[0].Name, "Grok product usage first name with unnamed cursor");
                AssertEqual("29%", viewModel.GrokProductUsageItems[0].PercentText, "Grok product usage build percent with unnamed cursor");
                AssertEqual("Cursor", viewModel.GrokProductUsageItems[1].Name, "Grok product usage cursor name with unnamed product");
                AssertEqual("5%", viewModel.GrokProductUsageItems[1].PercentText, "Grok product usage cursor percent with unnamed product");
                AssertEqual("Bot", viewModel.GrokProductUsageItems[2].Name, "Grok product usage bot name with unnamed cursor");
                AssertEqual("2%", viewModel.GrokProductUsageItems[2].PercentText, "Grok product usage bot percent with unnamed cursor");
                AssertEqual(4, viewModel.GrokProductUsageBarItems.Count, "Grok product usage bar item count with unnamed cursor");
                AssertEqual("Others", viewModel.GrokProductUsageBarItems[3].Name, "Grok product usage others name with unnamed cursor");
                AssertEqual("1%", viewModel.GrokProductUsageBarItems[3].PercentText, "Grok product usage others percent with unnamed cursor");
                AssertEqual("Build 29%\nCursor 5%\nBot 2%\nImagine 1%", viewModel.GrokProductUsageBarTooltip, "Grok product usage bar tooltip with unnamed cursor");
                viewModel.UpdateGrokDashboard(new GrokUsageDashboard(
                    new GrokUsageSnapshot(51, DateTimeOffset.Now.AddDays(7).ToUnixTimeSeconds(), "SuperGrok Heavy")
                    {
                        ProductUsage =
                        [
                            new GrokProductUsage("PRODUCT_GROK_BUILD", 32),
                            new GrokProductUsage("PRODUCT_CURSOR", 12),
                            new GrokProductUsage("PRODUCT_GROK_BOT", 6),
                            new GrokProductUsage("PRODUCT_GROK_IMAGINE", 1),
                        ],
                    },
                    string.Empty,
                    DateTimeOffset.Now),
                    CreateTokenCostStatistics(5));
                AssertEqual("Build", viewModel.GrokProductUsageItems[0].Name, "prefixed product usage first name");
                AssertEqual("32%", viewModel.GrokProductUsageItems[0].PercentText, "prefixed product usage build percent");
                AssertEqual("Cursor", viewModel.GrokProductUsageItems[1].Name, "prefixed product usage cursor name");
                AssertEqual("12%", viewModel.GrokProductUsageItems[1].PercentText, "prefixed product usage cursor percent");
                AssertEqual("Bot", viewModel.GrokProductUsageItems[2].Name, "prefixed product usage bot name");
                AssertEqual("6%", viewModel.GrokProductUsageItems[2].PercentText, "prefixed product usage bot percent");
                AssertEqual(4, viewModel.GrokProductUsageBarItems.Count, "prefixed product usage bar item count");
                AssertEqual("Others", viewModel.GrokProductUsageBarItems[3].Name, "prefixed product usage others name");
                AssertEqual("1%", viewModel.GrokProductUsageBarItems[3].PercentText, "prefixed product usage others percent");
                AssertEqual("Build 32%\nCursor 12%\nBot 6%\nImagine 1%", viewModel.GrokProductUsageBarTooltip, "prefixed product usage bar tooltip");
                AssertEqual(
                    System.Windows.Media.Color.FromRgb(166, 173, 179),
                    ((System.Windows.Media.SolidColorBrush)viewModel.GrokProductUsageBarItems[3].Brush).Color,
                    "Grok product usage others color with unnamed cursor");
                AssertTrue(window.FindName("CursorPageGrid") == null, "Cursor page is not in the panel");
                AssertTrue(window.FindName("CursorTokenCostDashboard") == null, "Cursor token cost is not in the panel");
                long weeklyResetAt = DateTimeOffset.Now.AddDays(7).ToUnixTimeSeconds();
                viewModel.UpdateGrokDashboard(new GrokUsageDashboard(
                    new GrokUsageSnapshot(5, weeklyResetAt, "SuperGrok"),
                    string.Empty,
                    DateTimeOffset.Now),
                    CreateTokenCostStatistics(5));
                AssertEqual("SUPERGROK", viewModel.GrokPlanDisplay, "later billing replaces the subscription tier");
                AssertEqual("95%", viewModel.GrokWeeklyQuota.PercentText, "later billing replaces weekly remaining percent");

                viewModel.ShowApi();
                content.InvalidateMeasure();
                content.Measure(new System.Windows.Size(window.Width, window.Height));
                content.Arrange(new System.Windows.Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                System.Windows.Controls.ItemsControl apiMonitorItemsControl = (System.Windows.Controls.ItemsControl)window.FindName("ApiMonitorItemsControl");
                System.Windows.Controls.ContentPresenter apiMonitorPresenter = (System.Windows.Controls.ContentPresenter)apiMonitorItemsControl.ItemContainerGenerator.ContainerFromIndex(0);
                System.Windows.DataTemplate apiMonitorTemplate = apiMonitorPresenter.ContentTemplate;
                System.Windows.Controls.Border apiMonitorCard = (System.Windows.Controls.Border)apiMonitorTemplate.FindName("ApiMonitorCard", apiMonitorPresenter);
                AssertEqual(apiMonitorCard.ActualHeight, codexPrimaryCard.ActualHeight, "API monitor and quota large card heights");
                AssertEqual(apiMonitorCard.Margin.Bottom, codexPrimaryCard.Margin.Bottom, "API monitor and Codex second card spacing");

                viewModel.UpdateStatus(
                    isRunning: true,
                    CodexTrayDefaults.Port,
                    new UsageResponse
                    {
                        Available = true,
                        Limits = new UsageLimits
                        {
                            Session = new UsageLimit { WindowMinutes = 300, RemainingPercent = 40, ResetLabel = "1h15m" },
                            Weekly = new UsageLimit { WindowMinutes = 10_080, RemainingPercent = 80, ResetLabel = "3d04h" },
                        },
                        ResetCredits = new ResetCredits { Available = true, AvailableCount = 1 },
                    },
                    error: null);
                viewModel.ShowCodex();
                content.InvalidateMeasure();
                content.Measure(new System.Windows.Size(window.Width, window.Height));
                content.Arrange(new System.Windows.Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                AssertTrue(viewModel.CodexSessionQuota.IsVisible, "valid Codex Session should be visible");
                AssertEqual("40%", viewModel.CodexSessionQuota.PercentText, "valid Session remains on the first large-card page");
                AssertEqual("80%", viewModel.CodexWeeklyQuota.PercentText, "Weekly remains on the second large-card page");
                AssertEqual(System.Windows.Visibility.Visible, codexFirstQuotaContent.Visibility, "valid Codex Session should show by default");
                AssertEqual(System.Windows.Visibility.Collapsed, codexSecondQuotaContent.Visibility, "Codex Weekly should start on page two");
                AssertEqual(System.Windows.Visibility.Visible, codexPageIndicators.Visibility, "two Codex quota pages should show page indicators");
                AssertEqual(codexFirstPageIndicator.ActualWidth, codexSecondPageIndicator.ActualWidth, "Codex quota page indicator widths");
                AssertEqual(codexFirstPageIndicator.ActualHeight, codexSecondPageIndicator.ActualHeight, "Codex quota page indicator heights");
                AssertEqual(
                    System.Windows.Media.Color.FromRgb(26, 188, 137),
                    ((System.Windows.Media.SolidColorBrush)codexFirstPageIndicator.Fill).Color,
                    "selected Codex Session page indicator color");
                codexSecondPageButton.Command.Execute(codexSecondPageButton.CommandParameter);
                content.UpdateLayout();
                AssertTrue(viewModel.CodexQuotaPager.IsSecondSelected, "Codex Weekly page button should select its quota");
                AssertEqual(System.Windows.Visibility.Visible, codexSecondQuotaContent.Visibility, "Codex Weekly should show on page two");
                AssertEqual(
                    System.Windows.Media.Color.FromRgb(26, 188, 137),
                    ((System.Windows.Media.SolidColorBrush)codexSecondPageIndicator.Fill).Color,
                    "selected Codex Weekly page indicator color");
                codexFirstPageButton.Command.Execute(codexFirstPageButton.CommandParameter);
                AssertTrue(!viewModel.CodexQuotaPager.IsSecondSelected, "Codex Session page button should select its quota");
                codexPrimaryCard.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    -120)
                {
                    RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent,
                });
                AssertTrue(viewModel.CodexQuotaPager.IsSecondSelected, "mouse wheel down should select Codex Weekly quota");
                codexPrimaryCard.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    120)
                {
                    RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent,
                });
                AssertTrue(!viewModel.CodexQuotaPager.IsSecondSelected, "mouse wheel up should select Codex Session quota");
                AssertEqual(91d, codexPrimaryCard.ActualHeight, "Codex primary card height after Session update");
                AssertEqual(68d, codexResetCreditsCard.ActualHeight, "Codex Resets height after Session update");
                AssertTrue(codexQuotaScrollViewer.ScrollableHeight == 0, $"updated Codex quota cards should not scroll, actual {codexQuotaScrollViewer.ScrollableHeight}");
                PropertyInfo themeModeProperty = typeof(System.Windows.Window).GetProperty("ThemeMode")!;
                object? closedThemeMode = themeModeProperty.GetValue(window);
                bool dialogConfirmed = false;
                viewModel.ShowInAppDialog(new InAppDialogRequest("Confirm", "Review fixture", "OK", PrimaryAction: () => dialogConfirmed = true));
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                window.Close();
                AssertTrue(!viewModel.IsModalOpen, "closing a dialog window must release the tray toggle guard");
                viewModel.ConfirmInAppDialogCommand.Execute(null);
                AssertTrue(!dialogConfirmed, "closing a window must discard its pending confirmation action");
                TrayPopupWindow reopenedWindow = new(viewModel);
                viewModel.ThemeMode = viewModel.ThemeMode == AppSettings.ThemeModeDark ? AppSettings.ThemeModeLight : AppSettings.ThemeModeDark;
                AssertEqual(closedThemeMode, themeModeProperty.GetValue(window), "closed windows must stop receiving view model theme changes");
                AssertEqual(viewModel.ThemeMode, themeModeProperty.GetValue(reopenedWindow)?.ToString(), "reopened windows must receive view model theme changes");
                reopenedWindow.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw new InvalidOperationException(failure.ToString(), failure);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Renders the Codex, Grok, and APIs pages into the README showcase image.
    /// </summary>
    private static int RenderShowcase()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                AppSettings settings = new AppSettings
                {
                    ThemeMode = AppSettings.ThemeModeDark,
                    MicaEnabled = false,
                    ApiMonitors =
                    [
                        new ApiMonitorSettings { Id = "deepseek", Name = "DeepSeek" },
                        new ApiMonitorSettings { Id = "newapi", Name = "NewAPI", Provider = ApiMonitorSettings.NewApiProvider },
                        new ApiMonitorSettings { Id = "openrouter", Name = "OpenRouter", Provider = ApiMonitorSettings.OpenRouterProvider },
                    ],
                }.Normalize();
                DateTimeOffset now = new(2026, 8, 12, 14, 30, 0, TimeSpan.FromHours(8));
                TrayPopupViewModel viewModel = new(settings, () => Task.CompletedTask);
                viewModel.UpdateStatus(
                    isRunning: true,
                    CodexTrayDefaults.Port,
                    new UsageResponse
                    {
                        Available = true,
                        PlanType = "plus",
                        UpdatedAt = now.ToString("O", CultureInfo.InvariantCulture),
                        Limits = new UsageLimits
                        {
                            Session = new UsageLimit { WindowMinutes = 300, RemainingPercent = 82, ResetLabel = "resets 18:30" },
                            Weekly = new UsageLimit { WindowMinutes = 10_080, RemainingPercent = 69, ResetLabel = "resets 08-16" },
                        },
                        ResetCredits = new ResetCredits
                        {
                            Available = true,
                            AvailableCount = 3,
                            NearestExpiryLocal = "2026-08-20 08:00",
                        },
                    },
                    error: null);
                viewModel.UpdateTokenCost(new TokenCostStatistics
                {
                    LastTwentyFourHours = new TokenCostSummary { TotalTokens = 20_740_000, CostUsd = 13.40m },
                    Today = new TokenCostSummary { TotalTokens = 20_740_000, CostUsd = 13.40m },
                    LastSevenDays = new TokenCostSummary { TotalTokens = 130_100_000, CostUsd = 97.15m },
                    LastThirtyDays = new TokenCostSummary { TotalTokens = 511_420_000, CostUsd = 398.48m },
                    CurrentWeek = new TokenCostSummary { TotalTokens = 68_120_000, CostUsd = 48.72m },
                    CurrentMonth = new TokenCostSummary { TotalTokens = 221_570_000, CostUsd = 166.84m },
                    Lifetime = new TokenCostSummary { TotalTokens = 917_780_000, CostUsd = 624.73m },
                    LastThirtyDaysDaily = Enumerable.Range(0, 30)
                        .Select(index => new TokenCostDailySummary
                        {
                            Date = new DateTime(2026, 8, 1).AddDays(index),
                            Summary = new TokenCostSummary { TotalTokens = (index + 2) * 900_000, CostUsd = (index + 2) * 0.82m },
                        })
                        .ToArray(),
                    CurrentMonthDaily = Enumerable.Range(0, 12)
                        .Select(index => new TokenCostDailySummary
                        {
                            Date = new DateTime(2026, 8, 1).AddDays(index),
                            Summary = new TokenCostSummary { TotalTokens = (index + 2) * 900_000, CostUsd = (index + 2) * 0.82m },
                        })
                        .ToArray(),
                    LastSevenDaysDaily = Enumerable.Range(0, 7)
                        .Select(index => new TokenCostDailySummary
                        {
                            Date = now.Date.AddDays(index - 6),
                            Summary = new TokenCostSummary { TotalTokens = (index + 4) * 1_000_000, CostUsd = index + 4 },
                        })
                        .ToArray(),
                    Models =
                    [
                        new TokenCostModelStatistics
                        {
                            Model = "gpt-5.6-sol",
                            LastTwentyFourHours = new TokenCostSummary { TotalTokens = 9_500_000, CostUsd = 5.72m },
                            Today = new TokenCostSummary { TotalTokens = 9_500_000, CostUsd = 5.72m },
                            CurrentWeek = new TokenCostSummary { TotalTokens = 31_000_000 },
                            CurrentMonth = new TokenCostSummary { TotalTokens = 101_000_000 },
                        },
                        new TokenCostModelStatistics
                        {
                            Model = "gpt-5.4",
                            LastTwentyFourHours = new TokenCostSummary { TotalTokens = 5_800_000, CostUsd = 3.48m },
                            Today = new TokenCostSummary { TotalTokens = 5_800_000, CostUsd = 3.48m },
                            CurrentWeek = new TokenCostSummary { TotalTokens = 20_000_000 },
                            CurrentMonth = new TokenCostSummary { TotalTokens = 67_000_000 },
                        },
                        new TokenCostModelStatistics
                        {
                            Model = "o3",
                            LastTwentyFourHours = new TokenCostSummary { TotalTokens = 3_100_000, CostUsd = 2.10m },
                            Today = new TokenCostSummary { TotalTokens = 3_100_000, CostUsd = 2.10m },
                            CurrentWeek = new TokenCostSummary { TotalTokens = 10_000_000 },
                            CurrentMonth = new TokenCostSummary { TotalTokens = 33_000_000 },
                        },
                        new TokenCostModelStatistics
                        {
                            Model = "other",
                            LastTwentyFourHours = new TokenCostSummary { TotalTokens = 2_340_000, CostUsd = 2.10m },
                            Today = new TokenCostSummary { TotalTokens = 2_340_000, CostUsd = 2.10m },
                            CurrentWeek = new TokenCostSummary { TotalTokens = 7_120_000 },
                            CurrentMonth = new TokenCostSummary { TotalTokens = 20_570_000 },
                        },
                    ],
                });
                viewModel.UpdateGrokDashboard(new GrokUsageDashboard(
                    new GrokUsageSnapshot(20, now.AddDays(5).ToUnixTimeSeconds(), "X Premium")
                    {
                        ProductUsage =
                        [
                            new GrokProductUsage("GrokChat", 12),
                            new GrokProductUsage("GrokBuild", 8),
                            new GrokProductUsage("GrokTasks", 3),
                            new GrokProductUsage("GrokVoice", 1),
                        ],
                    },
                    string.Empty,
                    now),
                    new TokenCostStatistics
                    {
                        LastTwentyFourHours = new TokenCostSummary { TotalTokens = 8_210_000, CostUsd = 5.72m },
                        Today = new TokenCostSummary { TotalTokens = 8_210_000, CostUsd = 5.72m },
                        LastSevenDays = new TokenCostSummary { TotalTokens = 45_360_000, CostUsd = 31.84m },
                        LastThirtyDays = new TokenCostSummary { TotalTokens = 174_820_000, CostUsd = 118.26m },
                        Lifetime = new TokenCostSummary { TotalTokens = 294_170_000, CostUsd = 201.49m },
                        LastThirtyDaysDaily = Enumerable.Range(0, 30)
                            .Select(index => new TokenCostDailySummary
                            {
                                Date = now.Date.AddDays(index - 29),
                                Summary = new TokenCostSummary { TotalTokens = (index + 2) * 900_000, CostUsd = (index + 2) * 0.64m },
                            })
                            .ToArray(),
                        Models =
                        [
                            new TokenCostModelStatistics
                            {
                                Model = "grok-4.6",
                                LastTwentyFourHours = new TokenCostSummary { TotalTokens = 5_200_000 },
                                Today = new TokenCostSummary { TotalTokens = 5_200_000 },
                            },
                            new TokenCostModelStatistics
                            {
                                Model = "grok-4.1-fast",
                                LastTwentyFourHours = new TokenCostSummary { TotalTokens = 3_010_000 },
                                Today = new TokenCostSummary { TotalTokens = 3_010_000 },
                            },
                        ],
                    });
                viewModel.UpdateApiUsage(
                [
                    new ApiUsageResult("deepseek", true, "¥276.21", string.Empty, string.Empty, now, Provider: ApiMonitorSettings.DeepSeekProvider),
                    new ApiUsageResult("newapi", true, "$1.91", "$188.09", string.Empty, now, Provider: ApiMonitorSettings.NewApiProvider),
                    new ApiUsageResult("openrouter", true, "$74.75", "$25.75", string.Empty, now, Provider: ApiMonitorSettings.OpenRouterProvider),
                ]);

                TrayPopupWindow window = new(viewModel);
                System.Windows.Media.Imaging.RenderTargetBitmap[] pages =
                [
                    CaptureShowcasePage(window, viewModel.ShowCodex),
                    CaptureShowcasePage(window, viewModel.ShowGrok),
                    CaptureShowcasePage(window, viewModel.ShowApi),
                ];
                const int padding = 20;
                const int gap = 20;
                int pageWidth = (int)CodexTrayDefaults.PopupWindowWidth;
                int pageHeight = (int)CodexTrayDefaults.PopupWindowHeight;
                int width = padding * 2 + pages.Length * pageWidth + (pages.Length - 1) * gap;
                int height = padding * 2 + pageHeight;
                System.Windows.Media.DrawingVisual visual = new();
                using (System.Windows.Media.DrawingContext context = visual.RenderOpen())
                {
                    context.DrawRectangle(
                        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 27, 31)),
                        null,
                        new System.Windows.Rect(0, 0, width, height));
                    for (int index = 0; index < pages.Length; index++)
                    {
                        double left = padding + index * (pageWidth + gap);
                        context.DrawImage(pages[index], new System.Windows.Rect(left, padding, pageWidth, pageHeight));
                    }
                }

                System.Windows.Media.Imaging.RenderTargetBitmap showcase = new(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                showcase.Render(visual);
                System.Windows.Media.Imaging.PngBitmapEncoder encoder = new();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(showcase));
                string outputPath = Path.GetFullPath(Path.Combine("Docs", "showcase.png"));
                using FileStream stream = File.Create(outputPath);
                encoder.Save(stream);
                window.Close();
                Console.WriteLine($"Rendered showcase: {outputPath}");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Captures one popup page at the fixed application dimensions.
    /// </summary>
    private static System.Windows.Media.Imaging.RenderTargetBitmap CaptureShowcasePage(TrayPopupWindow window, Action showPage)
    {
        showPage();
        System.Windows.FrameworkElement content = (System.Windows.FrameworkElement)window.Content;
        content.InvalidateMeasure();
        content.Measure(new System.Windows.Size(CodexTrayDefaults.PopupWindowWidth, CodexTrayDefaults.PopupWindowHeight));
        content.Arrange(new System.Windows.Rect(0, 0, CodexTrayDefaults.PopupWindowWidth, CodexTrayDefaults.PopupWindowHeight));
        content.UpdateLayout();
        System.Windows.Media.Imaging.RenderTargetBitmap bitmap = new(
            (int)CodexTrayDefaults.PopupWindowWidth,
            (int)CodexTrayDefaults.PopupWindowHeight,
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(content);
        return bitmap;
    }

    /// <summary>
    /// Runs a redacted live Grok billing probe for local verification.
    /// </summary>
    private static async Task<int> RunGrokLiveAsync()
    {
        try
        {
            GrokUsageSnapshot snapshot = await new GrokUsageCollector().CollectAsync();
            Console.WriteLine($"LIVE Grok billing: tier={snapshot.SubscriptionTier}, usedPercent={snapshot.UsedPercent:0.##}, resetsAt={snapshot.ResetsAt}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"LIVE Grok billing failed: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Runs one test and records failures.
    /// </summary>
    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            s_Failures++;
            Console.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Tests standard limit parsing and display formatting.
    /// </summary>
    private static async Task TestCollectsLimitsAndDisplayLabelsAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long sessionResetAt = now.AddHours(2).AddMinutes(5).ToUnixTimeSeconds();
        long weeklyResetAt = now.AddDays(3).AddHours(4).ToUnixTimeSeconds();
        CodexUsageCollector collector = CreateCodexUsageCollector(temp.Path, now, sessionResetAt, weeklyResetAt, 12.0, 34.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = await collector.CollectAsync(temp.Path);

        AssertTrue(response.Available, "response should be available");
        AssertEqual(12, response.Limits.Session.UsedPercent, "session used percent");
        AssertEqual(88, response.Limits.Session.RemainingPercent, "session remaining percent");
        AssertEqual(34, response.Limits.Weekly.UsedPercent, "weekly used percent");
        AssertEqual(66, response.Limits.Weekly.RemainingPercent, "weekly remaining percent");
        AssertEqual("pro", response.PlanType, "plan type");
        AssertEqual("2h05m", response.Limits.Session.ResetLabel, "session reset label");
        AssertEqual("3d04h", response.Limits.Weekly.ResetLabel, "weekly reset label");
        AssertEqual("66%", response.Display.Weekly, "weekly plugin display");
    }

    /// <summary>
    /// Tests weekly countdown labels on the current day.
    /// </summary>
    private static async Task TestWeeklyCountdownLabelAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long sessionResetAt = now.AddHours(1).ToUnixTimeSeconds();
        long weeklyResetAt = now.AddHours(3).ToUnixTimeSeconds();
        CodexUsageCollector collector = CreateCodexUsageCollector(temp.Path, now, sessionResetAt, weeklyResetAt, 20.0, 40.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = await collector.CollectAsync(temp.Path);

        AssertEqual("0d03h", response.Limits.Weekly.ResetLabel, "weekly countdown label");
    }

    /// <summary>
    /// Tests weekly countdown labels on the next day even when below twenty four hours.
    /// </summary>
    private static async Task TestNextDayWeeklyCountdownLabelAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 23, 0, 0, TimeSpan.FromHours(8));
        long sessionResetAt = now.AddHours(1).ToUnixTimeSeconds();
        DateTimeOffset weeklyResetAt = new(2026, 7, 2, 2, 0, 0, TimeSpan.FromHours(8));
        CodexUsageCollector collector = CreateCodexUsageCollector(temp.Path, now, sessionResetAt, weeklyResetAt.ToUnixTimeSeconds(), 20.0, 40.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = await collector.CollectAsync(temp.Path);

        AssertEqual("0d03h", response.Limits.Weekly.ResetLabel, "weekly next-day countdown label");
    }

    /// <summary>
    /// Tests the unavailable response when OAuth credentials are missing.
    /// </summary>
    private static async Task TestEmptyResponseAsync()
    {
        using TempDirectory temp = new();
        CodexUsageCollector collector = new(() => new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8)));
        UsageResponse response = await collector.CollectAsync(temp.Path);

        AssertTrue(!response.Available, "response should be unavailable");
        AssertEqual("N/A", response.Display.Weekly, "weekly unavailable display");
        AssertEqual("N/A", response.Display.Summary, "summary unavailable display");
    }

    /// <summary>
    /// Tests official ChatGPT quota parsing for Codex OAuth accounts.
    /// </summary>
    private static async Task TestOfficialQuotaAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        File.WriteAllText(Path.Combine(temp.Path, "auth.json"), JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt",
            tokens = new
            {
                access_token = "test-token",
                account_id = "account-123",
            },
        }));

        string body = JsonSerializer.Serialize(new
        {
            rate_limit = new
            {
                primary_window = new
                {
                    used_percent = 25.0,
                    limit_window_seconds = 18000,
                    reset_at = now.AddHours(1).AddMinutes(15).ToUnixTimeSeconds(),
                },
                secondary_window = new
                {
                    used_percent = 40.0,
                    limit_window_seconds = 604800,
                    reset_at = now.AddDays(2).AddHours(12).ToUnixTimeSeconds(),
                },
            },
        });
        using HttpClient client = new(new FakeHttpMessageHandler(body));
        CodexUsageCollector collector = new(() => now, client);

        UsageResponse response = await collector.CollectAsync(temp.Path);

        AssertTrue(response.Available, "official response should be available");
        AssertEqual("official_api", response.Source, "official source");
        AssertEqual("unknown", response.PlanType, "missing plan type");
        AssertEqual(75, response.Limits.Session.RemainingPercent, "official session remaining percent");
        AssertEqual(60, response.Limits.Weekly.RemainingPercent, "official weekly remaining percent");
        AssertEqual("60%", response.Display.Weekly, "official weekly plugin display");
    }

    /// <summary>
    /// Tests that plugin display values never include reset suffixes.
    /// </summary>
    private static async Task TestPluginDisplayPercentOnlyAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long sessionResetAt = now.AddHours(1).AddMinutes(15).ToUnixTimeSeconds();
        long weeklyResetAt = now.AddDays(2).AddHours(12).ToUnixTimeSeconds();
        CodexUsageCollector collector = CreateCodexUsageCollector(temp.Path, now, sessionResetAt, weeklyResetAt, 25.0, 40.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = await collector.CollectAsync(temp.Path);

        AssertEqual("60%", response.Display.Weekly, "weekly percentage-only plugin display");
        AssertEqual("1h15m", response.Limits.Session.ResetLabel, "session reset label remains available internally");
    }

    /// <summary>
    /// Tests that reset labels use absolute clock and date when the option is enabled.
    /// </summary>
    private static async Task TestAbsoluteResetTimeAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long sessionResetAt = now.AddHours(1).AddMinutes(15).ToUnixTimeSeconds();
        long weeklyResetAt = now.AddDays(2).AddHours(12).ToUnixTimeSeconds();
        CodexUsageCollector collector = CreateCodexUsageCollector(temp.Path, now, sessionResetAt, weeklyResetAt, 25.0, 40.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = await collector.CollectAsync(temp.Path, useAbsoluteResetTime: true);

        AssertEqual("13:15", response.Limits.Session.ResetLabel, "session absolute reset clock");
        AssertEqual("07-04", response.Limits.Weekly.ResetLabel, "weekly absolute reset date");
        AssertEqual("60%", response.Display.Weekly, "weekly plugin display should omit absolute reset");
    }

    /// <summary>
    /// Tests the lightweight HTTP server endpoints.
    /// </summary>
    private static async Task TestHttpServerAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        CodexUsageCollector collector = CreateCodexUsageCollector(temp.Path, now, now.AddHours(1).ToUnixTimeSeconds(), now.AddDays(2).ToUnixTimeSeconds(), 10.0, 20.0, out HttpClient collectorClient);
        using HttpClient _ = collectorClient;
        UsageCache usageCache = new();
        usageCache.UpdateCodex(await collector.CollectAsync(temp.Path));
        usageCache.UpdateGrok(GrokUsageCollector.BuildPluginUsage(new GrokUsageDashboard(
            new GrokUsageSnapshot(5, now.AddDays(7).ToUnixTimeSeconds()),
            string.Empty,
            now)));
        usageCache.UpdateDeepSeek(new DeepSeekPluginUsage(
            new UsageLimit { Name = "deepseek", RemainingPercent = 100 },
            "¥110"));
        using LightweightHttpServer server = new(usageCache, 0);
        server.Start();

        using HttpClient client = new();
        string health = await client.GetStringAsync($"http://{CodexTrayDefaults.Host}:{server.Port}{CodexTrayDefaults.HealthEndpointPath}");
        AssertTrue(health.Contains("\"ok\":true", StringComparison.Ordinal), "health response");
        using HttpResponseMessage unknown = await client.GetAsync($"http://{CodexTrayDefaults.Host}:{server.Port}/healthz");
        AssertEqual(HttpStatusCode.NotFound, unknown.StatusCode, "unknown HTTP paths should 404");

        string usageJson = await client.GetStringAsync($"http://{CodexTrayDefaults.Host}:{server.Port}{CodexTrayDefaults.UsageEndpointPath}");
        using JsonDocument document = JsonDocument.Parse(usageJson);
        JsonElement display = document.RootElement.GetProperty("display");
        JsonElement limits = document.RootElement.GetProperty("limits");
        AssertTrue(!display.TryGetProperty("session", out JsonElement sessionDisplay), "HTTP plugin response should omit Codex Session display");
        AssertTrue(!limits.TryGetProperty("session", out JsonElement sessionLimit), "HTTP plugin response should omit Codex Session limit");
        AssertEqual("80%", display.GetProperty("weekly").GetString(), "HTTP plugin Codex display");
        AssertTrue(!display.TryGetProperty("cursor_monthly", out JsonElement cursorDisplay), "HTTP plugin response should omit Cursor Monthly display");
        AssertTrue(!limits.TryGetProperty("cursor_monthly", out JsonElement cursorLimit), "HTTP plugin response should omit Cursor Monthly limit");
        AssertEqual("95%", display.GetProperty("grok_weekly").GetString(), "HTTP plugin Grok display");
        AssertEqual("¥110", display.GetProperty("deepseek").GetString(), "HTTP plugin DeepSeek display");
        AssertEqual(95, limits.GetProperty("grok_weekly").GetProperty("remaining_percent").GetInt32(), "HTTP plugin Grok remaining percent");
        AssertEqual(100, limits.GetProperty("deepseek").GetProperty("remaining_percent").GetInt32(), "HTTP plugin DeepSeek remaining percent");
        string[] displayNames = [.. display.EnumerateObject().Select(property => property.Name)];
        string[] limitNames = [.. limits.EnumerateObject().Select(property => property.Name)];
        AssertTrue(Array.IndexOf(displayNames, "grok_weekly") < Array.IndexOf(displayNames, "deepseek"), "HTTP plugin Grok display should precede DeepSeek");
        AssertTrue(Array.IndexOf(limitNames, "grok_weekly") < Array.IndexOf(limitNames, "deepseek"), "HTTP plugin Grok limit should precede DeepSeek");
        AssertTrue(!display.TryGetProperty("codex_5h", out JsonElement legacySession), "legacy session plugin field should be removed");
        AssertTrue(!display.TryGetProperty("codex_7d", out JsonElement legacyWeekly), "legacy weekly plugin field should be removed");

        string usageText = await client.GetStringAsync($"http://{CodexTrayDefaults.Host}:{server.Port}{CodexTrayDefaults.UsageTextEndpointPath}");
        string[] usageLines = usageText.Split(Environment.NewLine);
        AssertEqual(3, usageLines.Length, "text endpoint line count");
        AssertEqual("80%", usageLines[0], "text endpoint Codex display");
        AssertEqual("95%", usageLines[1], "text endpoint Grok display");
        AssertEqual("¥110", usageLines[2], "text endpoint DeepSeek display");
        await server.StopAsync();
    }

    /// <summary>
    /// Tests independent merging and clearing of Codex, Grok, and DeepSeek plugin values.
    /// </summary>
    private static Task TestUsageCacheSourcesAsync()
    {
        UsageCache usageCache = new();
        usageCache.UpdateCodex(new UsageResponse
        {
            Available = true,
            Limits = new UsageLimits
            {
                Session = new UsageLimit { Name = "session", RemainingPercent = 90 },
                Weekly = new UsageLimit { Name = "weekly", RemainingPercent = 80 },
            },
            Display = new UsageDisplay
            {
                Weekly = "80%",
                Summary = "Codex: 80%",
            },
        });
        usageCache.UpdateGrok(new GrokPluginUsage(
            new UsageLimit { Name = "weekly", RemainingPercent = 60 },
            "60%"));
        usageCache.UpdateDeepSeek(new DeepSeekPluginUsage(
            new UsageLimit { Name = "deepseek", RemainingPercent = 100 },
            "¥110"));

        UsageResponse merged = usageCache.Get() ?? throw new InvalidOperationException("merged usage should be available");
        AssertEqual(true, usageCache.GetCodex()?.Available, "Codex snapshot should stay available");
        AssertEqual("80%", merged.Display.Weekly, "merged Codex display");
        AssertEqual("60%", merged.Display.GrokWeekly, "merged Grok display");
        AssertEqual("¥110", merged.Display.DeepSeek, "merged DeepSeek display");
        AssertEqual(100, merged.Limits.DeepSeek.RemainingPercent, "merged DeepSeek remaining percent");
        AssertEqual("Codex: 80% | Grok: 60% | DeepSeek: ¥110", merged.Display.Summary, "merged plugin summary order");

        usageCache.ClearCodex();
        AssertTrue(usageCache.GetCodex() == null, "cleared Codex snapshot should be empty");
        UsageResponse grokAndDeepSeek = usageCache.Get() ?? throw new InvalidOperationException("Grok and DeepSeek usage should be available");
        AssertEqual("N/A", grokAndDeepSeek.Display.Weekly, "cleared Codex display");
        AssertEqual("60%", grokAndDeepSeek.Display.GrokWeekly, "preserved Grok display");
        AssertEqual("¥110", grokAndDeepSeek.Display.DeepSeek, "preserved DeepSeek display");

        usageCache.ClearGrok();
        UsageResponse deepSeekOnly = usageCache.Get() ?? throw new InvalidOperationException("DeepSeek-only usage should be available");
        AssertEqual("N/A", deepSeekOnly.Display.GrokWeekly, "cleared Grok display");
        AssertEqual("¥110", deepSeekOnly.Display.DeepSeek, "preserved DeepSeek display after other sources clear");

        usageCache.ClearDeepSeek();
        AssertTrue(usageCache.Get() == null, "cleared plugin usage should be empty");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests that stopping the HTTP server cancels and drains a partial-header client.
    /// </summary>
    private static async Task TestHttpServerActiveClientStopAsync()
    {
        using LightweightHttpServer server = new(new UsageCache(), 0);
        server.Start();
        using TcpClient client = new();
        await client.ConnectAsync(CodexTrayDefaults.Host, server.Port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /health HTTP/1.1\r\nHost: localhost\r\n"));
        await WaitForTrackedClientAsync(server);

        Task firstStop = server.StopAsync();
        Task secondStop = server.StopAsync();
        AssertTrue(ReferenceEquals(firstStop, secondStop), "concurrent stop calls should share one task");
        await firstStop.WaitAsync(TimeSpan.FromSeconds(2));
        AssertTrue(!server.IsRunning, "server should stop after draining the partial request");
    }

    /// <summary>
    /// Waits until the HTTP server has accepted and tracked a test client.
    /// </summary>
    private static async Task WaitForTrackedClientAsync(LightweightHttpServer server)
    {
        PropertyInfo property = typeof(LightweightHttpServer).GetProperty("ActiveClientCount", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("active client count property was not found");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while ((int)(property.GetValue(server) ?? 0) == 0)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>
    /// Tests that a failed bind leaves the same HTTP server instance restartable.
    /// </summary>
    private static async Task TestHttpServerBindFailureRecoveryAsync()
    {
        TcpListener blocker = new(IPAddress.Loopback, 0);
        blocker.Start();
        int port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        using LightweightHttpServer server = new(new UsageCache(), port);
        bool failed = false;
        try
        {
            server.Start();
        }
        catch (SocketException)
        {
            failed = true;
        }

        AssertTrue(failed, "occupied port should reject the first bind");
        blocker.Stop();
        server.Start();
        AssertTrue(server.IsRunning, "same server instance should start after bind cleanup");
        await server.StopAsync();
    }

    /// <summary>
    /// Tests stop idempotence, the in-progress restart guard, and restart after completion.
    /// </summary>
    private static async Task TestHttpServerStopRestartAsync()
    {
        using LightweightHttpServer server = new(new UsageCache(), 0);
        server.Start();
        Task stop = server.StopAsync();
        bool restartGuarded = false;
        try
        {
            server.Start();
        }
        catch (InvalidOperationException)
        {
            restartGuarded = true;
        }

        AssertTrue(restartGuarded, "start should reject an in-progress stop");
        AssertTrue(ReferenceEquals(stop, server.StopAsync()), "stop should be idempotent while active");
        await stop;
        await server.StopAsync();
        server.Start();
        AssertTrue(server.IsRunning, "server should restart after stop completion");
        await server.StopAsync();
    }

    /// <summary>
    /// Tests pre-canceled Codex, token-cost, and monitor locator operations.
    /// </summary>
    private static async Task TestCollectorCancellationAsync()
    {
        using TempDirectory temp = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await AssertCanceledAsync(() => new CodexUsageCollector().CollectAsync(temp.Path, cancellationToken: cancellation.Token));
        await AssertCanceledAsync(() => Task.Run(() => new TokenCostCollector().CollectCodex(temp.Path, cancellationToken: cancellation.Token)));
        await AssertCanceledAsync(() => Task.Run(() => new TokenCostCollector().CollectGrok(temp.Path, cancellationToken: cancellation.Token)));
        await AssertCanceledAsync(() => Task.Run(() => LiteMonitorLocator.AutoDetect(cancellationToken: cancellation.Token)));
    }

    /// <summary>
    /// Tests caller cancellation propagation and non-caller timeout mapping for API monitors.
    /// </summary>
    private static async Task TestApiUsageCancellationAsync()
    {
        ApiMonitorSettings monitor = new()
        {
            Provider = ApiMonitorSettings.DeepSeekProvider,
            ApiKey = "test-key",
            BaseUrl = "https://example.test",
        };
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await AssertCanceledAsync(() => new ApiUsageCollector().CollectAsync([monitor], cancellationToken: cancellation.Token));

        using HttpClient client = new(new CanceledHttpMessageHandler());
        IReadOnlyList<ApiUsageResult> results = await new ApiUsageCollector(client).CollectAsync([monitor]);
        AssertEqual("Request timed out", results[0].Error, "non-caller TaskCanceledException should remain a timeout");
    }

    /// <summary>
    /// Tests installing the plugin configuration file.
    /// </summary>
    private static Task TestPluginInstallAsync()
    {
        using TempDirectory temp = new();
        File.WriteAllText(Path.Combine(temp.Path, "LiteMonitor.exe"), string.Empty);
        Directory.CreateDirectory(Path.Combine(temp.Path, "resources", "plugins"));

        string targetPath = LiteMonitorPluginInstaller.Install(temp.Path, 17998);
        AssertTrue(File.Exists(targetPath), "plugin file should exist");
        string content = File.ReadAllText(targetPath);
        using JsonDocument pluginDocument = JsonDocument.Parse(content);
        string expectedVersion = typeof(TrayPopupViewModel).Assembly.GetName().Version?.ToString(3) ?? string.Empty;
        AssertEqual(expectedVersion, pluginDocument.RootElement.GetProperty("meta").GetProperty("version").GetString(), "plugin version should match the app version");
        string trafficMonitorSource = File.ReadAllText(FindRepositoryFile(Path.Combine("Plugins", "TrafficMonitor", "TrafficMonitorPlugin.cpp")));
        int versionIndex = trafficMonitorSource.IndexOf("case TMI_VERSION:", StringComparison.Ordinal);
        AssertTrue(versionIndex >= 0, "TrafficMonitor plugin should declare TMI_VERSION");
        int returnIndex = trafficMonitorSource.IndexOf("return L\"", versionIndex, StringComparison.Ordinal);
        int quoteIndex = trafficMonitorSource.IndexOf('"', returnIndex + "return L\"".Length);
        string trafficMonitorVersion = trafficMonitorSource[(returnIndex + "return L\"".Length)..quoteIndex];
        AssertEqual(expectedVersion, trafficMonitorVersion, "TrafficMonitor version should match the app version");
        AssertTrue(content.Contains("\"short_label\": \"Codex\"", StringComparison.Ordinal), "plugin content should include Codex item");
        AssertTrue(!content.Contains("\"short_label\": \"Cursor\"", StringComparison.Ordinal), "plugin content should omit Cursor");
        AssertTrue(!content.Contains("cursor_monthly", StringComparison.Ordinal), "plugin content should omit Cursor Monthly");
        AssertTrue(content.Contains("\"short_label\": \"Grok\"", StringComparison.Ordinal), "plugin content should include Grok item");
        AssertTrue(content.Contains("\"short_label\": \"DeepSeek\"", StringComparison.Ordinal), "plugin content should include DeepSeek item");
        AssertTrue(content.Contains("\"format_val\": \"{{grok_display}}\"", StringComparison.Ordinal), "plugin content should include Grok value");
        AssertTrue(content.Contains("\"format_val\": \"{{deepseek_display}}\"", StringComparison.Ordinal), "plugin content should include DeepSeek value");
        AssertTrue(
            content.IndexOf("\"short_label\": \"Grok\"", StringComparison.Ordinal) < content.IndexOf("\"short_label\": \"DeepSeek\"", StringComparison.Ordinal),
            "plugin Grok item should precede DeepSeek");
        AssertTrue(content.Contains($"http://{CodexTrayDefaults.Host}:17998{CodexTrayDefaults.UsageEndpointPath}", StringComparison.Ordinal), "plugin content should include bridge URL");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests installing the TrafficMonitor plugin files.
    /// </summary>
    private static Task TestTrafficMonitorPluginInstallAsync()
    {
        using TempDirectory temp = new();
        File.WriteAllText(Path.Combine(temp.Path, "TrafficMonitor.exe"), string.Empty);
        string sourcePath = Path.Combine(temp.Path, CodexTrayDefaults.TrafficMonitorPluginFileName);
        File.WriteAllText(sourcePath, "fake dll");

        string targetPath = TrafficMonitorPluginInstaller.Install(temp.Path, 17999, sourcePath);
        AssertTrue(File.Exists(targetPath), "plugin dll should exist");
        string configPath = Path.Combine(temp.Path, "plugins", CodexTrayDefaults.TrafficMonitorPluginConfigFileName);
        AssertTrue(File.Exists(configPath), "plugin config should exist");
        string content = File.ReadAllText(configPath);
        AssertTrue(content.Contains($"http://{CodexTrayDefaults.Host}:17999{CodexTrayDefaults.UsageTextEndpointPath}", StringComparison.Ordinal), "plugin config should include bridge URL");
        AssertTrue(!content.Contains("{{", StringComparison.Ordinal), "plugin config should not include template placeholders");
        AssertTrue(!content.Contains("RequestIntervalSeconds", StringComparison.Ordinal), "plugin config should not include request interval");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests storing settings in the application directory.
    /// </summary>
    private static Task TestSettingsStorePathAsync()
    {
        using TempDirectory temp = new();
        SettingsStore store = new(temp.Path);
        AppSettings settings = new()
        {
            Port = 17997,
            RefreshIntervalMinutes = 7,
            VisiblePages = PageItem.Grok | PageItem.Apis,
        };

        store.Save(settings);

        string expectedPath = Path.Combine(temp.Path, CodexTrayDefaults.SettingsFileName);
        AssertEqual(expectedPath, store.SettingsPath, "settings path");
        AssertTrue(File.Exists(expectedPath), "settings file should exist beside executable");
        AssertTrue(!Directory.Exists(Path.Combine(temp.Path, "CodexTray")), "settings directory should not exist");
        AssertEqual(17997, store.Load().Port, "saved settings port");
        AssertEqual(PageItem.Grok | PageItem.Apis, store.Load().VisiblePages, "saved visible pages");
        DateTime writeTime = File.GetLastWriteTimeUtc(expectedPath);
        AssertEqual(17997, store.Load().Port, "unchanged settings should not rewrite the file");
        AssertEqual(writeTime, File.GetLastWriteTimeUtc(expectedPath), "normalized settings load should keep the file timestamp");
        settings.VisiblePages = PageItem.Codex | PageItem.Apis;
        store.Save(settings);
        AssertEqual(PageItem.Codex | PageItem.Apis, store.Load().VisiblePages, "saved settings should keep Grok hidden");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests loading partial settings and saving default fields back.
    /// </summary>
    private static Task TestSettingsStoreRepairsMissingFieldsAsync()
    {
        using TempDirectory temp = new();
        SettingsStore store = new(temp.Path);
        File.WriteAllText(store.SettingsPath, "{\"Port\":17996,\"HideInvalidProgressBars\":false,\"ShowResetTimeInPlugins\":false,\"TokenUnit\":\"Chinese unit\"}");

        AppSettings settings = store.Load();

        AssertEqual(17996, settings.Port, "existing port");
        AssertEqual(string.Empty, settings.LiteMonitorDir, "default LiteMonitor path");
        AssertEqual(string.Empty, settings.TrafficMonitorDir, "default TrafficMonitor path");
        AssertEqual(CodexTrayDefaults.RefreshIntervalMinutes, settings.RefreshIntervalMinutes, "default refresh interval");
        AssertEqual(PageItem.All, settings.VisiblePages, "default visible pages");
        AssertTrue(!settings.StartWithWindows, "startup should be disabled by default");
        AssertEqual(AppSettings.ThemeModeSystem, settings.ThemeMode, "default theme mode");
        AssertTrue(settings.MicaEnabled, "Mica should be enabled by default");
        AssertTrue(settings.UseAbsoluteResetTime, "absolute reset time should be enabled by default");
        AssertEqual(0, settings.ApiMonitors.Count, "default API monitors");

        string repairedJson = File.ReadAllText(store.SettingsPath);
        using JsonDocument document = JsonDocument.Parse(repairedJson);
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.LiteMonitorDir), out _), "repaired settings should include LiteMonitor path");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.TrafficMonitorDir), out _), "repaired settings should include TrafficMonitor path");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.RefreshIntervalMinutes), out _), "repaired settings should include refresh interval");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.VisiblePages), out _), "repaired settings should include visible pages");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.SettingsSchemaVersion), out _), "repaired settings should include schema version");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.ThemeMode), out _), "repaired settings should include theme mode");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.MicaEnabled), out _), "repaired settings should include Mica toggle");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.ApiMonitors), out _), "repaired settings should include API monitors");
        AssertTrue(!document.RootElement.TryGetProperty("HideInvalidProgressBars", out _), "repaired settings should omit the retired progress bar toggle");
        AssertTrue(!document.RootElement.TryGetProperty("ShowResetTimeInPlugins", out _), "repaired settings should omit the retired plugin reset-time toggle");
        AssertTrue(!document.RootElement.TryGetProperty("TokenUnit", out _), "repaired settings should omit the retired token unit");
        AssertTrue(!document.RootElement.TryGetProperty("FirstRunCompleted", out _), "repaired settings should not include first-run flag");

        using TempDirectory legacyTemp = new();
        SettingsStore legacyStore = new(legacyTemp.Path);
        File.WriteAllText(legacyStore.SettingsPath, "{\"VisiblePages\":7,\"BackdropMode\":\"Mica\",\"AcrylicEnabled\":true,\"AcrylicOpacityPercent\":80,\"TokenCostItems\":63,\"WindowWidth\":800,\"WindowHeight\":1200}");
        AppSettings legacySettings = legacyStore.Load();
        AssertEqual(PageItem.All, legacySettings.VisiblePages, "legacy all pages should include Grok");
        AssertTrue(legacySettings.MicaEnabled, "legacy Mica mode");
        using JsonDocument migratedDocument = JsonDocument.Parse(File.ReadAllText(legacyStore.SettingsPath));
        AssertEqual(AppSettings.CurrentSettingsSchemaVersion, migratedDocument.RootElement.GetProperty(nameof(AppSettings.SettingsSchemaVersion)).GetInt32(), "migrated schema version");
        AssertTrue(migratedDocument.RootElement.TryGetProperty(nameof(AppSettings.MicaEnabled), out _), "migrated settings should include Mica toggle");
        AssertTrue(!migratedDocument.RootElement.TryGetProperty(nameof(AppSettings.BackdropMode), out _), "migrated settings should omit backdrop mode");
        AssertTrue(!migratedDocument.RootElement.TryGetProperty("AcrylicEnabled", out _), "migrated settings should omit legacy acrylic toggle");
        AssertTrue(!migratedDocument.RootElement.TryGetProperty("AcrylicOpacityPercent", out _), "migrated settings should omit acrylic opacity");
        AssertTrue(!migratedDocument.RootElement.TryGetProperty("TokenCostItems", out _), "migrated settings should omit token cost selection");
        AssertTrue(!migratedDocument.RootElement.TryGetProperty("WindowWidth", out _), "migrated settings should omit window width");
        AssertTrue(!migratedDocument.RootElement.TryGetProperty("WindowHeight", out _), "migrated settings should omit window height");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests settings repair for null values, null monitor entries, and malformed JSON.
    /// </summary>
    private static Task TestSettingsStoreRepairsNullAndMalformedValuesAsync()
    {
        using TempDirectory temp = new();
        SettingsStore store = new(temp.Path);
        File.WriteAllText(store.SettingsPath, """
        {
          "LiteMonitorDir": null,
          "TrafficMonitorDir": null,
          "ApiMonitors": [
            null,
            {
              "Id": null,
              "Name": null,
              "Provider": null,
              "BaseUrl": null,
              "ApiKey": null,
              "UserId": null
            },
            {
              "Provider": "Cursor"
            }
          ]
        }
        """);

        AppSettings settings = store.Load();

        AssertEqual(string.Empty, settings.LiteMonitorDir, "null LiteMonitor path");
        AssertEqual(string.Empty, settings.TrafficMonitorDir, "null TrafficMonitor path");
        AssertEqual(1, settings.ApiMonitors.Count, "null and Cursor monitors should be removed");
        ApiMonitorSettings monitor = settings.ApiMonitors.Single();
        AssertTrue(!string.IsNullOrWhiteSpace(monitor.Id), "null monitor id should be repaired");
        AssertEqual(ApiMonitorSettings.DeepSeekProvider, monitor.Provider, "null monitor provider should be repaired");
        AssertEqual(ApiMonitorSettings.DeepSeekProvider, monitor.Name, "null monitor name should be repaired");
        AssertEqual(string.Empty, monitor.BaseUrl, "null monitor base URL should be repaired");
        AssertEqual(string.Empty, monitor.ApiKey, "null monitor API key should be repaired");
        AssertEqual(string.Empty, monitor.UserId, "null monitor user id should be repaired");

        string repairedJson = File.ReadAllText(store.SettingsPath);
        using JsonDocument document = JsonDocument.Parse(repairedJson);
        AssertEqual(JsonValueKind.String, document.RootElement.GetProperty(nameof(AppSettings.LiteMonitorDir)).ValueKind, "repaired LiteMonitor path JSON type");
        AssertEqual(1, document.RootElement.GetProperty(nameof(AppSettings.ApiMonitors)).GetArrayLength(), "repaired API monitor JSON count");
        AssertTrue(!Directory.EnumerateFiles(temp.Path, "*.tmp", SearchOption.TopDirectoryOnly).Any(), "atomic settings write should not leave temporary files");

        using TempDirectory malformedTemp = new();
        SettingsStore malformedStore = new(malformedTemp.Path);
        File.WriteAllText(malformedStore.SettingsPath, "{");
        AppSettings fallback = malformedStore.Load();
        AssertEqual(CodexTrayDefaults.Port, fallback.Port, "malformed settings should fall back to defaults");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies damaged configuration protection, backup validation, and recovery of editable cards.
    /// </summary>
    private static Task TestSettingsRecoveryAsync()
    {
        using TempDirectory temp = new();
        SettingsStore store = new(temp.Path);
        AppSettings initial = new() { Port = 18000, ApiMonitors = [new() { ApiKey = "test-secret", UserId = "test-user" }] };
        store.Save(initial);
        store.Save(new AppSettings { Port = 18001 });
        string backup = File.ReadAllText(store.BackupPath);
        AssertTrue(backup.Contains("test-secret"), "backup must use the same credential representation as the main file");
        foreach (string damaged in new[] { "{", "null", "[]", "{\"Port\":\"invalid\"}" })
        {
            File.WriteAllText(store.SettingsPath, damaged);
            store.Load();
            AssertTrue(store.IsWriteBlocked && store.LoadError.Length > 0, "damaged configuration must report an error and block saves");
            bool blocked = false;
            try
            {
                store.Save(new AppSettings());
            }
            catch (InvalidOperationException)
            {
                blocked = true;
            }

            AssertTrue(blocked, "automatic saves must not overwrite temporary defaults");
            AssertEqual(damaged, File.ReadAllText(store.SettingsPath), "damaged bytes must remain intact");
            AssertEqual(backup, File.ReadAllText(store.BackupPath), "valid backup must survive failed loads and saves");
        }

        AppSettings restored = store.RestoreBackup();
        AssertEqual(18000, restored.Port, "previous valid port must be restored");
        AssertEqual("test-secret", restored.ApiMonitors[0].ApiKey, "credentials must survive backup restoration");
        AssertTrue(!store.IsWriteBlocked, "successful restoration must unblock writes");
        AssertEqual("{\"Port\":\"invalid\"}", File.ReadAllText(store.PreservedSettingsPath!), "restore must preserve the damaged original");
        string validMain = File.ReadAllText(store.SettingsPath);
        File.WriteAllText(store.BackupPath, "{");
        bool invalidBackup = false;
        try
        {
            store.RestoreBackup();
        }
        catch (JsonException)
        {
            invalidBackup = true;
        }

        AssertTrue(invalidBackup, "invalid backup must be rejected before writing");
        AssertEqual(validMain, File.ReadAllText(store.SettingsPath), "invalid backup must not replace the main file");
        File.WriteAllText(store.SettingsPath, "{");
        bool externalCorruptionBlocked = false;
        try
        {
            store.Save(restored);
        }
        catch (InvalidOperationException)
        {
            externalCorruptionBlocked = true;
        }

        AssertTrue(externalCorruptionBlocked && store.IsWriteBlocked, "corruption after a successful load must also block saves");
        TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
        viewModel.PortText = "18005";
        viewModel.IsSettingsWriteBlocked = true;
        AssertTrue(!viewModel.SaveSettingsCommand.CanExecute(null), "protected settings must disable UI saves");
        viewModel.LoadRecoveredSettings(restored);
        AssertEqual("18000", viewModel.PortText, "recovery must replace edited temporary settings");
        AssertEqual("test-secret", viewModel.ApiMonitors[0].ApiKey, "recovery must repopulate API cards");
        viewModel.VisiblePages = PageItem.None;
        AppSettings candidate = restored.Copy();
        viewModel.ApplySettings(candidate, markSaved: false);
        AssertEqual(PageItem.All, restored.VisiblePages, "candidate creation must not mutate active page visibility before persistence");
        AssertEqual(PageItem.None, candidate.VisiblePages, "candidate must contain requested page changes");
        AssertEqual(SettingsStatus.Unsaved, viewModel.SettingsStatus, "unsaved candidate must not claim success");
        candidate.ApiMonitors[0].ApiKey = "different-test-key";
        AssertEqual("test-secret", restored.ApiMonitors[0].ApiKey, "candidate must not share mutable credential cards");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies valid configuration remains active when backup creation or normalization cannot write files.
    /// </summary>
    private static Task TestSettingsMaintenanceFailureAsync()
    {
        foreach (bool failBackup in new[] { true, false })
        {
            using TempDirectory temp = new();
            SettingsStore store = new(temp.Path);
            AppSettings initial = new() { Port = 18042, VisiblePages = PageItem.Codex, ApiMonitors = [new() { ApiKey = "test-key" }] };
            store.Save(initial);
            if (failBackup)
            {
                File.Delete(store.BackupPath);
                Directory.CreateDirectory(store.BackupPath);
            }
            else
            {
                File.AppendAllText(store.SettingsPath, "\n");
            }

            string original = File.ReadAllText(store.SettingsPath);
            using FileStream? locked = failBackup ? null : new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            AppSettings loaded = store.Load();
            AssertEqual(initial.Port, loaded.Port, "maintenance write failures must preserve the validated port");
            AssertEqual(initial.VisiblePages, loaded.VisiblePages, "maintenance write failures must preserve page visibility");
            AssertEqual("test-key", loaded.ApiMonitors[0].ApiKey, "maintenance write failures must preserve API cards");
            AssertTrue(store.IsWriteBlocked, "maintenance write failures must report an error and block persistence");
            AssertEqual(original, File.ReadAllText(store.SettingsPath), "failed maintenance must leave the original configuration intact");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests settings value normalization.
    /// </summary>
    private static Task TestSettingsNormalizeAsync()
    {
        AppSettings settings = new()
        {
            Port = -1,
            RefreshIntervalMinutes = 0,
            ThemeMode = "unexpected",
            VisiblePages = PageItem.Grok | (PageItem)(1 << 10),
            BackdropMode = "unexpected",
        };

        settings.Normalize();

        AssertEqual(CodexTrayDefaults.Port, settings.Port, "default port");
        AssertEqual(CodexTrayDefaults.RefreshIntervalMinutes, settings.RefreshIntervalMinutes, "default refresh interval");
        AssertEqual(AppSettings.ThemeModeSystem, settings.ThemeMode, "default theme mode");
        AssertEqual(PageItem.Grok, settings.VisiblePages, "supported visible pages");
        AssertTrue(!settings.MicaEnabled, "unexpected backdrop should disable Mica");
        AssertEqual<string?>(null, settings.BackdropMode, "legacy backdrop should be cleared");
        settings.VisiblePages = PageItem.Codex | PageItem.Apis;
        settings.Normalize();
        AssertEqual(PageItem.Codex | PageItem.Apis, settings.VisiblePages, "normalized settings should keep Grok hidden");
        settings.VisiblePages = PageItem.Codex | (PageItem)2;
        settings.Normalize();
        AssertEqual(PageItem.Codex, settings.VisiblePages, "retired page bit is dropped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests compact token unit thresholds.
    /// </summary>
    private static Task TestTokenUnitFormattingAsync()
    {
        AssertEqual("999.00K", AppSettings.FormatTokenCount(999_000), "K unit");
        AssertEqual("1.00M", AppSettings.FormatTokenCount(1_000_000), "M lower boundary");
        AssertEqual("999.00M", AppSettings.FormatTokenCount(999_000_000), "M upper range");
        AssertEqual("1.00B", AppSettings.FormatTokenCount(1_000_000_000), "B boundary");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests API monitor persistence in the shared settings file.
    /// </summary>
    private static Task TestApiMonitorSettingsAsync()
    {
        using TempDirectory temp = new();
        SettingsStore store = new(temp.Path);
        AppSettings settings = new()
        {
            VisiblePages = PageItem.Codex | PageItem.Apis,
            ApiMonitors =
            [
                new ApiMonitorSettings
                {
                    Name = "Personal DeepSeek",
                    ApiKey = "deepseek-secret-token",
                },
                new ApiMonitorSettings
                {
                    Name = "Legacy Grok",
                    Provider = "Grok",
                },
                new ApiMonitorSettings
                {
                    Name = "OpenRouter Credits",
                    Provider = "openrouter",
                    BaseUrl = "https://openrouter.example/",
                    ApiKey = "openrouter-management-key",
                },
                new ApiMonitorSettings
                {
                    Name = "NanoGPT Balance",
                    Provider = "nanogpt",
                    BaseUrl = "https://nano-gpt.example/",
                    ApiKey = "nanogpt-key",
                },
                new ApiMonitorSettings
                {
                    Name = "Vercel Credits",
                    Provider = "vercel",
                    BaseUrl = "https://ai-gateway.example/",
                    ApiKey = "vercel-gateway-key",
                },
                new ApiMonitorSettings
                {
                    Name = "Local Cursor",
                    Provider = ApiMonitorSettings.CursorProvider,
                    BaseUrl = "https://should-clear.example",
                    ApiKey = "cursor-should-not-persist",
                },
            ],
        };

        store.Save(settings);

        string json = File.ReadAllText(store.SettingsPath);
        AssertTrue(json.Contains("deepseek-secret-token", StringComparison.Ordinal), "settings should contain the API key");
        AssertTrue(!json.Contains("cursor-should-not-persist", StringComparison.Ordinal), "settings should not contain Cursor tokens");
        AssertTrue(!json.Contains("Legacy Grok", StringComparison.Ordinal), "settings should remove legacy Grok API monitors");
        AppSettings loaded = store.Load();
        ApiMonitorSettings deepSeek = loaded.ApiMonitors.Single(monitor => monitor.Provider == ApiMonitorSettings.DeepSeekProvider);
        ApiMonitorSettings openRouter = loaded.ApiMonitors.Single(monitor => monitor.Provider == ApiMonitorSettings.OpenRouterProvider);
        ApiMonitorSettings nanoGpt = loaded.ApiMonitors.Single(monitor => monitor.Provider == ApiMonitorSettings.NanoGptProvider);
        ApiMonitorSettings vercel = loaded.ApiMonitors.Single(monitor => monitor.Provider == ApiMonitorSettings.VercelProvider);
        AssertEqual("deepseek-secret-token", deepSeek.ApiKey, "saved API key");
        AssertEqual("Personal DeepSeek", deepSeek.Name, "API monitor name");
        AssertEqual("https://openrouter.example", openRouter.BaseUrl, "saved OpenRouter base URL");
        AssertEqual("openrouter-management-key", openRouter.ApiKey, "saved OpenRouter management key");
        AssertEqual("https://nano-gpt.example", nanoGpt.BaseUrl, "saved NanoGPT base URL");
        AssertEqual("nanogpt-key", nanoGpt.ApiKey, "saved NanoGPT API key");
        AssertEqual("https://ai-gateway.example", vercel.BaseUrl, "saved Vercel base URL");
        AssertEqual("vercel-gateway-key", vercel.ApiKey, "saved Vercel AI Gateway API key");
        AssertEqual(4, loaded.ApiMonitors.Count, "Cursor and legacy Grok API monitors should be removed");
        AssertTrue((loaded.VisiblePages & PageItem.Grok) != 0, "legacy Grok API monitor should enable the Grok page");
        AssertTrue(!json.Contains("Local Cursor", StringComparison.Ordinal), "settings should remove Cursor API monitors");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests supported API provider requests and balance parsing.
    /// </summary>
    private static async Task TestApiUsageCollectorAsync()
    {
        using HttpClient client = new(new ApiUsageHttpMessageHandler());
        ApiUsageCollector collector = new(client);
        ApiMonitorSettings deepSeek = new()
        {
            Id = "deepseek",
            BaseUrl = "https://deepseek.example/v1",
            ApiKey = "deepseek-key",
        };
        ApiMonitorSettings newApi = new()
        {
            Id = "newapi",
            Provider = ApiMonitorSettings.NewApiProvider,
            BaseUrl = "https://newapi.example",
            ApiKey = "newapi-token",
            UserId = "42",
        };
        ApiMonitorSettings openRouter = new()
        {
            Id = "openrouter",
            Provider = ApiMonitorSettings.OpenRouterProvider,
            BaseUrl = "https://openrouter.example/api/v1",
            ApiKey = "openrouter-management-key",
        };
        ApiMonitorSettings openRouterRoot = new()
        {
            Id = "openrouter-root",
            Provider = ApiMonitorSettings.OpenRouterProvider,
            BaseUrl = "https://openrouter.example",
            ApiKey = "openrouter-management-key",
        };
        ApiMonitorSettings nanoGpt = new()
        {
            Id = "nanogpt",
            Provider = ApiMonitorSettings.NanoGptProvider,
            BaseUrl = "https://nano-gpt.example/api/v1",
            ApiKey = "nanogpt-key",
        };
        ApiMonitorSettings nanoGptUsageFailure = new()
        {
            Id = "nanogpt-failure",
            Provider = ApiMonitorSettings.NanoGptProvider,
            BaseUrl = "https://nano-gpt-failure.example",
            ApiKey = "nanogpt-failure-key",
        };
        ApiMonitorSettings vercel = new()
        {
            Id = "vercel",
            Provider = ApiMonitorSettings.VercelProvider,
            BaseUrl = "https://ai-gateway.example/v1",
            ApiKey = "vercel-gateway-key",
        };
        ApiMonitorSettings vercelRoot = new()
        {
            Id = "vercel-root",
            Provider = ApiMonitorSettings.VercelProvider,
            BaseUrl = "https://ai-gateway.example",
            ApiKey = "vercel-gateway-key",
        };

        IReadOnlyList<ApiUsageResult> results = await collector.CollectAsync(
            [deepSeek, newApi, openRouter, openRouterRoot, nanoGpt, nanoGptUsageFailure, vercel, vercelRoot]);

        AssertEqual("¥110.00", results[0].BalanceDisplay, "DeepSeek CNY balance");
        AssertEqual("$10.00", results[1].BalanceDisplay, "NewAPI remaining USD quota");
        AssertEqual("$5.00", results[1].UsedDisplay, "NewAPI used USD quota");
        AssertEqual("$74.75", results[2].BalanceDisplay, "OpenRouter remaining credits");
        AssertEqual("$25.75", results[2].UsedDisplay, "OpenRouter used credits");
        AssertEqual("$74.75", results[3].BalanceDisplay, "OpenRouter root base URL credits");
        AssertEqual("$12.35", results[4].BalanceDisplay, "NanoGPT USD balance");
        AssertEqual("$3.00", results[4].UsedDisplay, "NanoGPT 30-day usage");
        AssertTrue(results[5].Available, "NanoGPT balance should remain available when usage fails");
        AssertEqual("$12.35", results[5].BalanceDisplay, "NanoGPT balance after usage failure");
        AssertEqual("N/A", results[5].UsedDisplay, "NanoGPT usage failure display");
        AssertTrue(results[5].UsedError.Length > 0, "NanoGPT usage failure must carry a reason");
        AssertEqual("$95.50", results[6].BalanceDisplay, "Vercel remaining credits");
        AssertEqual("$4.50", results[6].UsedDisplay, "Vercel used credits");
        AssertEqual("$95.50", results[7].BalanceDisplay, "Vercel root base URL credits");
    }

    /// <summary>
    /// Tests that plugin usage uses the first DeepSeek card and rounds CNY balance to a whole yuan.
    /// </summary>
    private static Task TestDeepSeekPluginUsageAsync()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        DeepSeekPluginUsage mixed = ApiUsageCollector.BuildDeepSeekPluginUsage(
        [
            new ApiUsageResult("openrouter", true, "$74.75", "$25.75", string.Empty, now, Provider: ApiMonitorSettings.OpenRouterProvider),
            new ApiUsageResult("deepseek", true, "¥110.00", string.Empty, string.Empty, now, Provider: ApiMonitorSettings.DeepSeekProvider),
            new ApiUsageResult("deepseek-2", true, "¥9.00", string.Empty, string.Empty, now, Provider: ApiMonitorSettings.DeepSeekProvider),
        ]);
        AssertEqual("¥110", mixed.Display, "first DeepSeek card should be used");
        AssertEqual(100, mixed.Limit.RemainingPercent, "available DeepSeek remaining percent");

        DeepSeekPluginUsage roundedDown = ApiUsageCollector.BuildDeepSeekPluginUsage(
        [
            new ApiUsageResult("deepseek", true, "¥276.21", string.Empty, string.Empty, now, Provider: ApiMonitorSettings.DeepSeekProvider),
        ]);
        AssertEqual("¥276", roundedDown.Display, "DeepSeek plugin balance should round 0.21 down");

        DeepSeekPluginUsage roundedUp = ApiUsageCollector.BuildDeepSeekPluginUsage(
        [
            new ApiUsageResult("deepseek", true, "¥110.50", string.Empty, string.Empty, now, Provider: ApiMonitorSettings.DeepSeekProvider),
        ]);
        AssertEqual("¥111", roundedUp.Display, "DeepSeek plugin balance should round 0.50 away from zero");

        DeepSeekPluginUsage halfDown = ApiUsageCollector.BuildDeepSeekPluginUsage(
        [
            new ApiUsageResult("deepseek", true, "¥0.49", string.Empty, string.Empty, now, Provider: ApiMonitorSettings.DeepSeekProvider),
        ]);
        AssertEqual("¥0", halfDown.Display, "DeepSeek plugin balance should round 0.49 down to zero");

        DeepSeekPluginUsage unavailableFirst = ApiUsageCollector.BuildDeepSeekPluginUsage(
        [
            new ApiUsageResult("deepseek", false, "N/A", "N/A", "Enter an API key", now, Provider: ApiMonitorSettings.DeepSeekProvider),
            new ApiUsageResult("deepseek-2", true, "¥9.00", string.Empty, string.Empty, now, Provider: ApiMonitorSettings.DeepSeekProvider),
        ]);
        AssertEqual(CodexTrayDefaults.UnavailableDisplay, unavailableFirst.Display, "unavailable first DeepSeek card should not fall through");
        AssertEqual(0, unavailableFirst.Limit.RemainingPercent, "unavailable DeepSeek remaining percent");

        DeepSeekPluginUsage noDeepSeek = ApiUsageCollector.BuildDeepSeekPluginUsage(
        [
            new ApiUsageResult("openrouter", true, "$74.75", "$25.75", string.Empty, now, Provider: ApiMonitorSettings.OpenRouterProvider),
        ]);
        AssertEqual(CodexTrayDefaults.UnavailableDisplay, noDeepSeek.Display, "missing DeepSeek card should be unavailable");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests JSON parsing for Grok credits usage.
    /// </summary>
    private static Task TestGrokUsageCollectorAsync()
    {
        const long resetAt = 1_802_592_000;
        string creditsResponse = JsonSerializer.Serialize(new
        {
            config = new
            {
                currentPeriod = new
                {
                    type = "USAGE_PERIOD_TYPE_WEEKLY",
                    start = DateTimeOffset.FromUnixTimeSeconds(resetAt).AddDays(-7).ToString("O", CultureInfo.InvariantCulture),
                    end = DateTimeOffset.FromUnixTimeSeconds(resetAt).ToString("O", CultureInfo.InvariantCulture),
                },
                creditUsagePercent = 5.0,
                productUsage = new object[]
                {
                    new { product = "GrokChat", usagePercent = 3.0 },
                    new { product = "PRODUCT_GROK_BUILD", usagePercent = 2.0 },
                    new { product = "GrokTasks" },
                    new { usagePercent = 99.0 },
                },
            },
            subscriptionTier = "SuperGrok",
        });
        GrokUsageSnapshot creditsSnapshot = GrokUsageCollector.ParseCreditsResponse(creditsResponse);

        AssertEqual(5.0, creditsSnapshot.UsedPercent, "Grok credits used percentage");
        AssertEqual(resetAt, creditsSnapshot.ResetsAt, "Grok credits reset timestamp");
        AssertEqual("SuperGrok", creditsSnapshot.SubscriptionTier, "Grok credits subscription tier");
        AssertEqual("GrokChat|PRODUCT_GROK_BUILD|GrokTasks|Cursor", string.Join('|', creditsSnapshot.ProductUsage.Select(item => item.Product)), "Grok product identifiers");
        AssertEqual("3|2|0|99", string.Join('|', creditsSnapshot.ProductUsage.Select(item => item.UsedPercent.ToString("0.##", CultureInfo.InvariantCulture))), "Grok product usage percentages");

        string proxyResponse = JsonSerializer.Serialize(new
        {
            config = new
            {
                currentPeriod = new
                {
                    type = "USAGE_PERIOD_TYPE_WEEKLY",
                    start = DateTimeOffset.FromUnixTimeSeconds(resetAt).AddDays(-7).ToString("O", CultureInfo.InvariantCulture),
                    end = DateTimeOffset.FromUnixTimeSeconds(resetAt).ToString("O", CultureInfo.InvariantCulture),
                },
                creditUsagePercent = 64.0,
                productUsage = new[]
                {
                    new { product = "GrokBuild", usagePercent = 38.0 },
                    new { product = "Cursor", usagePercent = 16.0 },
                    new { product = "GrokBot", usagePercent = 9.0 },
                    new { product = "GrokImagine", usagePercent = 1.0 },
                },
            },
        });
        GrokUsageSnapshot proxySnapshot = GrokUsageCollector.ParseCreditsResponse(proxyResponse);
        AssertEqual(64.0, proxySnapshot.UsedPercent, "CLI proxy credits used percentage");
        AssertEqual(resetAt, proxySnapshot.ResetsAt, "CLI proxy credits reset timestamp");
        AssertEqual(string.Empty, proxySnapshot.SubscriptionTier, "CLI proxy credits omit subscription tier");
        AssertEqual("GrokBuild|Cursor|GrokBot|GrokImagine", string.Join('|', proxySnapshot.ProductUsage.Select(item => item.Product)), "CLI proxy product identifiers");
        AssertEqual("38|16|9|1", string.Join('|', proxySnapshot.ProductUsage.Select(item => item.UsedPercent.ToString("0.##", CultureInfo.InvariantCulture))), "CLI proxy product usage percentages");
        GrokPluginUsage rounded = GrokUsageCollector.BuildPluginUsage(new GrokUsageDashboard(
            new GrokUsageSnapshot(14.5, resetAt),
            string.Empty,
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)));
        AssertEqual(15, rounded.Weekly.UsedPercent, "billing weekly used percent rounds 0.5 away from zero");
        AssertEqual(85, rounded.Weekly.RemainingPercent, "billing weekly remaining follows the rounded used percent");
        AssertEqual("85%", rounded.Display, "billing weekly plugin display");
        AssertEqual(
            "SuperGrok",
            GrokUsageCollector.ParseSettingsSubscriptionTier("{\"subscription_tier_display\":\"SuperGrok\",\"subscription_tier\":\"GrokPro\"}"),
            "settings display name wins over subscription_tier");
        AssertEqual(
            "X Premium",
            GrokUsageCollector.ParseSettingsSubscriptionTier("{\"subscription_tier_display\":null,\"subscription_tier\":\" X Premium \"}"),
            "empty settings display falls back to subscription_tier");
        AssertEqual(string.Empty, GrokUsageCollector.ParseSettingsSubscriptionTier("{}"), "missing settings tier stays empty");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests that an expired Grok Build token is refreshed and written back to auth.json.
    /// </summary>
    private static async Task TestGrokBuildOAuthRefreshAsync()
    {
        using TempDirectory temp = new();
        string previousGrokHome = Environment.GetEnvironmentVariable("GROK_HOME") ?? string.Empty;
        Environment.SetEnvironmentVariable("GROK_HOME", temp.Path);
        try
        {
            const string entryKey = "https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828";
            string authPath = Path.Combine(temp.Path, "auth.json");
            File.WriteAllText(authPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [entryKey] = new
                {
                    key = "old-grok-access",
                    refresh_token = "grok-refresh",
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("o", CultureInfo.InvariantCulture),
                    user_id = "grok-user-123",
                    subscriptionTier = "Free",
                    email = "keep-me@example.com",
                },
                ["https://example.test/sign-in"] = new
                {
                    key = "other-grok-access",
                    subscriptionTier = "X Premium",
                },
            }));
            string logsDirectory = Path.Combine(temp.Path, "logs");
            Directory.CreateDirectory(logsDirectory);
            string logPath = Path.Combine(logsDirectory, "unified.jsonl");
            File.WriteAllLines(logPath,
            [
                "{malformed",
                "{\"msg\":\"billing: fetched credits config\",\"ctx\":{\"subscriptionTier\":\"SuperGrok\"}}",
                "{\"msg\":\"billing: fetched credits config\",\"ctx\":{\"subscriptionTier\":\"SuperGrok Heavy\"}}",
            ]);

            using HttpClient client = new(new GrokOAuthRefreshHttpMessageHandler(
                "old-grok-access",
                "new-grok-access",
                "grok-refresh",
                "rotated-grok-refresh",
                CreateGrokCreditsResponse(12.5, 1_802_592_000)));
            GrokUsageCollector collector = new(client);
            GrokUsageSnapshot snapshot = await collector.CollectAsync();

            AssertEqual(12.5, snapshot.UsedPercent, "refreshed Grok Build used percentage");
            AssertEqual("GrokBuild", snapshot.ProductUsage.Single().Product, "refreshed Grok Build product usage");
            AssertEqual("SuperGrok Heavy", snapshot.SubscriptionTier, "latest Grok Build cached subscription tier");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(authPath));
            JsonElement entry = document.RootElement.GetProperty(entryKey);
            AssertEqual("new-grok-access", entry.GetProperty("key").GetString(), "Grok Build access token write-back");
            AssertEqual("rotated-grok-refresh", entry.GetProperty("refresh_token").GetString(), "Grok Build refresh token write-back");
            AssertEqual("keep-me@example.com", entry.GetProperty("email").GetString(), "Grok Build unrelated field preservation");
            AssertTrue(
                DateTimeOffset.Parse(entry.GetProperty("expires_at").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) >
                DateTimeOffset.UtcNow,
                "Grok Build expires_at should be in the future");

            File.Delete(logPath);
            GrokUsageSnapshot authSnapshot = await collector.CollectAsync();
            AssertEqual("Free", authSnapshot.SubscriptionTier, "active Grok Build auth entry subscription tier");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", string.IsNullOrEmpty(previousGrokHome) ? null : previousGrokHome);
        }
    }

    /// <summary>
    /// Tests that the settings display name overrides local logs, and failures keep the local backfill.
    /// </summary>
    private static async Task TestGrokSettingsSubscriptionTierAsync()
    {
        using TempDirectory temp = new();
        string previousGrokHome = Environment.GetEnvironmentVariable("GROK_HOME") ?? string.Empty;
        Environment.SetEnvironmentVariable("GROK_HOME", temp.Path);
        try
        {
            const string entryKey = "https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828";
            File.WriteAllText(Path.Combine(temp.Path, "auth.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [entryKey] = new
                {
                    key = "new-grok-access",
                    refresh_token = "grok-refresh",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToString("o", CultureInfo.InvariantCulture),
                    user_id = "grok-user-123",
                    subscriptionTier = "Free",
                },
            }));
            string logsDirectory = Path.Combine(temp.Path, "logs");
            Directory.CreateDirectory(logsDirectory);
            File.WriteAllText(
                Path.Combine(logsDirectory, "unified.jsonl"),
                "{\"msg\":\"billing: fetched credits config\",\"ctx\":{\"subscriptionTier\":\"SuperGrok Heavy\"}}");

            GrokUsageSnapshot display = await CollectGrokSettingsAsync(
                "{\"subscription_tier_display\":\"SuperGrok\",\"subscription_tier\":\"GrokPro\"}");
            AssertEqual(12.5, display.UsedPercent, "settings display keeps billing usage");
            AssertEqual("SuperGrok", display.SubscriptionTier, "settings display overrides the local log and subscription_tier");

            GrokUsageSnapshot tier = await CollectGrokSettingsAsync("{\"subscription_tier_display\":\"\",\"subscription_tier\":\"X Premium\"}");
            AssertEqual("X Premium", tier.SubscriptionTier, "blank settings display uses subscription_tier before the local log");

            GrokUsageSnapshot failed = await CollectGrokSettingsAsync("not-json", HttpStatusCode.NotFound);
            AssertEqual(12.5, failed.UsedPercent, "failed settings request keeps billing usage");
            AssertEqual("SuperGrok Heavy", failed.SubscriptionTier, "failed settings request uses the local log");

            GrokUsageSnapshot invalid = await CollectGrokSettingsAsync("not-json");
            AssertEqual("SuperGrok Heavy", invalid.SubscriptionTier, "invalid settings JSON uses the local log");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", string.IsNullOrEmpty(previousGrokHome) ? null : previousGrokHome);
        }
    }

    /// <summary>
    /// Collects Grok usage against a fixed settings response.
    /// </summary>
    private static async Task<GrokUsageSnapshot> CollectGrokSettingsAsync(string settingsBody, HttpStatusCode settingsStatus = HttpStatusCode.OK)
    {
        using HttpClient client = new(new GrokOAuthRefreshHttpMessageHandler(
            "old-grok-access",
            "new-grok-access",
            "grok-refresh",
            "rotated-grok-refresh",
            CreateGrokCreditsResponse(12.5, 1_802_592_000),
            settingsBody,
            settingsStatus));
        return await new GrokUsageCollector(client).CollectAsync();
    }

    /// <summary>
    /// Tests that Grok ignores an available OpenCode OAuth session when Grok Build is not logged in.
    /// </summary>
    private static async Task TestGrokIgnoresOpenCodeOAuthAsync()
    {
        using TempDirectory temp = new();
        string previousGrokHome = Environment.GetEnvironmentVariable("GROK_HOME") ?? string.Empty;
        string previousXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? string.Empty;
        string grokHome = Path.Combine(temp.Path, "grok");
        string openCodeDirectory = Path.Combine(temp.Path, "data", "opencode");
        Directory.CreateDirectory(grokHome);
        Directory.CreateDirectory(openCodeDirectory);
        File.WriteAllText(Path.Combine(openCodeDirectory, "auth.json"), JsonSerializer.Serialize(new
        {
            xai = new
            {
                type = "oauth",
                access = "opencode-access-that-must-not-be-used",
            },
        }));
        Environment.SetEnvironmentVariable("GROK_HOME", grokHome);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(temp.Path, "data"));
        try
        {
            GrokUsageDashboard dashboard = await new GrokUsageCollector().CollectDashboardAsync();

            AssertEqual<GrokUsageSnapshot?>(null, dashboard.Usage, "OpenCode OAuth should not provide Grok usage");
            AssertTrue(dashboard.Error.Contains("Grok Build OAuth file was not found", StringComparison.Ordinal), "missing Grok Build login error");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", string.IsNullOrEmpty(previousGrokHome) ? null : previousGrokHome);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", string.IsNullOrEmpty(previousXdg) ? null : previousXdg);
        }
    }

    /// <summary>
    /// Tests that the local pricing resource includes supported Codex and Grok models.
    /// </summary>
    private static Task TestPublishedModelPricingAsync()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Resources", "model-pricing.json")));
        JsonElement pricing = document.RootElement.GetProperty("gpt-5.3-codex");
        AssertEqual(1.75m, pricing.GetProperty("input").GetDecimal(), "gpt-5.3-codex input price");
        AssertEqual(0.175m, pricing.GetProperty("cachedInput").GetDecimal(), "gpt-5.3-codex cached input price");
        AssertEqual(14.0m, pricing.GetProperty("output").GetDecimal(), "gpt-5.3-codex output price");
        JsonElement lunaPricing = document.RootElement.GetProperty("gpt-5.6-luna");
        AssertEqual(0.2m, lunaPricing.GetProperty("input").GetDecimal(), "gpt-5.6-luna input price");
        AssertEqual(0.02m, lunaPricing.GetProperty("cachedInput").GetDecimal(), "gpt-5.6-luna cached input price");
        AssertEqual(1.2m, lunaPricing.GetProperty("output").GetDecimal(), "gpt-5.6-luna output price");
        AssertEqual("codex-auto-review", string.Join('|', lunaPricing.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString())), "gpt-5.6-luna aliases");
        JsonElement astraPricing = document.RootElement.GetProperty("gpt-6-astra");
        AssertEqual(10.0m, astraPricing.GetProperty("input").GetDecimal(), "gpt-6-astra input price");
        AssertEqual(1.0m, astraPricing.GetProperty("cachedInput").GetDecimal(), "gpt-6-astra cached input price");
        AssertEqual(50.0m, astraPricing.GetProperty("output").GetDecimal(), "gpt-6-astra output price");
        JsonElement solPricing = document.RootElement.GetProperty("gpt-6-sol");
        AssertEqual(2.0m, solPricing.GetProperty("input").GetDecimal(), "gpt-6-sol input price");
        AssertEqual(0.2m, solPricing.GetProperty("cachedInput").GetDecimal(), "gpt-6-sol cached input price");
        AssertEqual(10.0m, solPricing.GetProperty("output").GetDecimal(), "gpt-6-sol output price");
        JsonElement gpt6LunaPricing = document.RootElement.GetProperty("gpt-6-luna");
        AssertEqual(0.1m, gpt6LunaPricing.GetProperty("input").GetDecimal(), "gpt-6-luna input price");
        AssertEqual(0.01m, gpt6LunaPricing.GetProperty("cachedInput").GetDecimal(), "gpt-6-luna cached input price");
        AssertEqual(0.5m, gpt6LunaPricing.GetProperty("output").GetDecimal(), "gpt-6-luna output price");
        JsonElement gpt61SolPricing = document.RootElement.GetProperty("gpt-6.1-sol");
        AssertEqual(2.0m, gpt61SolPricing.GetProperty("input").GetDecimal(), "gpt-6.1-sol input price");
        AssertEqual(0.1m, gpt61SolPricing.GetProperty("cachedInput").GetDecimal(), "gpt-6.1-sol cached input price");
        AssertEqual(10.0m, gpt61SolPricing.GetProperty("output").GetDecimal(), "gpt-6.1-sol output price");
        JsonElement gpt61TerraPricing = document.RootElement.GetProperty("gpt-6.1-terra");
        AssertEqual(1.0m, gpt61TerraPricing.GetProperty("input").GetDecimal(), "gpt-6.1-terra input price");
        AssertEqual(0.05m, gpt61TerraPricing.GetProperty("cachedInput").GetDecimal(), "gpt-6.1-terra cached input price");
        AssertEqual(5.0m, gpt61TerraPricing.GetProperty("output").GetDecimal(), "gpt-6.1-terra output price");
        JsonElement grokPricing = document.RootElement.GetProperty("grok-4.5");
        AssertEqual(2.0m, grokPricing.GetProperty("input").GetDecimal(), "grok-4.5 input price");
        AssertEqual(0.3m, grokPricing.GetProperty("cachedInput").GetDecimal(), "grok-4.5 cached input price");
        AssertEqual(6.0m, grokPricing.GetProperty("output").GetDecimal(), "grok-4.5 output price");
        AssertEqual("grok-4.5-latest|grok-4.5-build", string.Join('|', grokPricing.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString())), "grok-4.5 aliases");
        JsonElement grok46Pricing = document.RootElement.GetProperty("grok-4.6");
        AssertEqual(2.0m, grok46Pricing.GetProperty("input").GetDecimal(), "grok-4.6 input price");
        AssertEqual(0.5m, grok46Pricing.GetProperty("cachedInput").GetDecimal(), "grok-4.6 cached input price");
        AssertEqual(6.0m, grok46Pricing.GetProperty("output").GetDecimal(), "grok-4.6 output price");
        AssertEqual("grok-4.6-latest|grok-4.6-build", string.Join('|', grok46Pricing.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString())), "grok-4.6 aliases");
        JsonElement grok47Pricing = document.RootElement.GetProperty("grok-4.7");
        AssertEqual(2.0m, grok47Pricing.GetProperty("input").GetDecimal(), "grok-4.7 input price");
        AssertEqual(0.5m, grok47Pricing.GetProperty("cachedInput").GetDecimal(), "grok-4.7 cached input price");
        AssertEqual(6.0m, grok47Pricing.GetProperty("output").GetDecimal(), "grok-4.7 output price");
        AssertEqual("grok-4.7-latest|grok-4.7-build", string.Join('|', grok47Pricing.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString())), "grok-4.7 aliases");
        JsonElement deepSeekFlashPricing = document.RootElement.GetProperty("deepseek-v4-flash");
        AssertEqual(0.15m, deepSeekFlashPricing.GetProperty("input").GetDecimal(), "deepseek-v4-flash off-peak input price");
        AssertEqual(0.003m, deepSeekFlashPricing.GetProperty("cachedInput").GetDecimal(), "deepseek-v4-flash off-peak cached input price");
        AssertEqual(0.6m, deepSeekFlashPricing.GetProperty("output").GetDecimal(), "deepseek-v4-flash off-peak output price");
        JsonElement[] flashPeriods = [.. deepSeekFlashPricing.GetProperty("periods").EnumerateArray()];
        AssertEqual(2, flashPeriods.Length, "deepseek-v4-flash period count");
        AssertPricingPeriod(flashPeriods[0], 0.3m, 0.006m, 1.2m, "1,2,3,4,5", "01:00", "04:00", "deepseek-v4-flash morning peak");
        AssertPricingPeriod(flashPeriods[1], 0.3m, 0.006m, 1.2m, "1,2,3,4,5", "06:00", "10:00", "deepseek-v4-flash afternoon peak");
        AssertEqual("deepseek-flash", string.Join('|', deepSeekFlashPricing.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString())), "deepseek-v4-flash aliases");
        JsonElement deepSeekPricing = document.RootElement.GetProperty("deepseek-v4-pro");
        AssertEqual(0.66m, deepSeekPricing.GetProperty("input").GetDecimal(), "deepseek-v4-pro off-peak input price");
        AssertEqual(0.022m, deepSeekPricing.GetProperty("cachedInput").GetDecimal(), "deepseek-v4-pro off-peak cached input price");
        AssertEqual(1.98m, deepSeekPricing.GetProperty("output").GetDecimal(), "deepseek-v4-pro off-peak output price");
        JsonElement[] proPeriods = [.. deepSeekPricing.GetProperty("periods").EnumerateArray()];
        AssertEqual(2, proPeriods.Length, "deepseek-v4-pro period count");
        AssertPricingPeriod(proPeriods[0], 1.32m, 0.044m, 3.96m, "1,2,3,4,5", "01:00", "04:00", "deepseek-v4-pro morning peak");
        AssertPricingPeriod(proPeriods[1], 1.32m, 0.044m, 3.96m, "1,2,3,4,5", "06:00", "10:00", "deepseek-v4-pro afternoon peak");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds a minimal JSON credits response for Grok collector tests.
    /// </summary>
    private static string CreateGrokCreditsResponse(double usedPercent, long resetAt)
    {
        return JsonSerializer.Serialize(new
        {
            config = new
            {
                currentPeriod = new
                {
                    type = "USAGE_PERIOD_TYPE_WEEKLY",
                    start = DateTimeOffset.FromUnixTimeSeconds(resetAt).AddDays(-7).ToString("O", CultureInfo.InvariantCulture),
                    end = DateTimeOffset.FromUnixTimeSeconds(resetAt).ToString("O", CultureInfo.InvariantCulture),
                },
                creditUsagePercent = usedPercent,
                productUsage = new[]
                {
                    new { product = "GrokBuild", usagePercent = usedPercent },
                },
            },
        });
    }

    /// <summary>
    /// Creates a compact token-statistics fixture for popup layout checks.
    /// </summary>
    private static TokenCostStatistics CreateTokenCostStatistics(long multiplier)
    {
        DateTime today = DateTime.Today;
        return new TokenCostStatistics
        {
            LastTwentyFourHours = new TokenCostSummary { TotalTokens = multiplier * 100 },
            Today = new TokenCostSummary { TotalTokens = multiplier * 100 },
            LastSevenDays = new TokenCostSummary { TotalTokens = multiplier * 700 },
            LastThirtyDays = new TokenCostSummary { TotalTokens = multiplier * 3_000 },
            CurrentWeek = new TokenCostSummary { TotalTokens = multiplier * 500 },
            CurrentMonth = new TokenCostSummary { TotalTokens = multiplier * 2_000 },
            Lifetime = new TokenCostSummary { TotalTokens = multiplier * 10_000 },
            LastThirtyDaysDaily = Enumerable.Range(0, 30)
                .Select(index => new TokenCostDailySummary
                {
                    Date = today.AddDays(index - 29),
                    Summary = new TokenCostSummary { TotalTokens = multiplier * (index + 1) },
                })
                .ToArray(),
            CurrentMonthDaily = Enumerable.Range(0, today.Day)
                .Select(index => new TokenCostDailySummary
                {
                    Date = new DateTime(today.Year, today.Month, 1).AddDays(index),
                    Summary = new TokenCostSummary { TotalTokens = multiplier * (index + 1) },
                })
                .ToArray(),
            LastSevenDaysDaily = Enumerable.Range(0, 7)
                .Select(index => new TokenCostDailySummary
                {
                    Date = today.AddDays(index - 6),
                    Summary = new TokenCostSummary { TotalTokens = multiplier * (index + 1) },
                })
                .ToArray(),
            Models =
            [
                new TokenCostModelStatistics
                {
                    Model = "gpt-5.6-sol",
                    LastTwentyFourHours = new TokenCostSummary { TotalTokens = multiplier * 60 },
                    Today = new TokenCostSummary { TotalTokens = multiplier * 60 },
                    CurrentWeek = new TokenCostSummary { TotalTokens = multiplier * 300 },
                    CurrentMonth = new TokenCostSummary { TotalTokens = multiplier * 1_200 },
                },
                new TokenCostModelStatistics
                {
                    Model = "gpt-5.4",
                    LastTwentyFourHours = new TokenCostSummary { TotalTokens = multiplier * 40 },
                    Today = new TokenCostSummary { TotalTokens = multiplier * 40 },
                    CurrentWeek = new TokenCostSummary { TotalTokens = multiplier * 200 },
                    CurrentMonth = new TokenCostSummary { TotalTokens = multiplier * 800 },
                },
            ],
        };
    }

    /// <summary>
    /// Pages Codex and Grok session-log rows without mixing the two harnesses.
    /// </summary>
    private static Task TestSessionLogPagingAsync()
    {
        TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
        DateTimeOffset newest = new(2026, 10, 27, 21, 54, 49, TimeSpan.FromHours(8));
        SessionLogEntry[] entries = Enumerable.Range(0, 21)
            .Select(index => new SessionLogEntry
            {
                Timestamp = newest.AddMinutes(-index),
                Model = index == 0 ? "gpt-6-astra" : "gpt-test",
                TimedOutputTokens = index == 3 ? 0 : 220,
                TimedDurationMilliseconds = index == 3 ? 0 : 10_000,
            })
            .ToArray();
        viewModel.UpdateTokenCost(new TokenCostStatistics { IsSpeedEstimated = true, SessionLogs = entries });
        SessionLogListViewModel logs = viewModel.CodexSessionLogs;
        AssertEqual(false, logs.IsOpen, "session logs start on the usage panel");
        AssertEqual("\uE8FD", SessionLogListViewModel.NotesGlyph, "session logs uses BulletedList");
        AssertEqual(SessionLogListViewModel.NotesGlyph, logs.ToggleGlyph, "session logs uses the list glyph");
        logs.ToggleCommand.Execute(null);
        AssertEqual(true, logs.IsOpen, "the same button opens session logs");
        AssertEqual(SessionLogListViewModel.NotesGlyph, logs.ToggleGlyph, "opening session logs keeps the notes glyph");
        AssertEqual("Back to usage", logs.ToggleTooltip, "open session logs explains how to return");
        AssertEqual(SessionLogListViewModel.PageSize, logs.Rows.Count, "first page has 20 rows");
        AssertEqual("21 records", logs.Summary, "record count");
        AssertEqual("1 / 2", logs.PageLabel, "first page label");
        AssertEqual(true, logs.CanGoNext, "first page can advance");
        AssertEqual(false, logs.CanGoPrevious, "first page cannot go back");
        AssertEqual(newest.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture), logs.Rows[0].Time, "newest row time");
        AssertEqual("gpt-6-astra", logs.Rows[0].Model, "newest row model");
        AssertEqual("≈22 tok/s", logs.Rows[0].Speed, "estimated Codex speed keeps the unit");
        AssertEqual("—", logs.Rows[3].Speed, "missing speed stays blank");
        logs.NextPageCommand.Execute(null);
        AssertEqual(1, logs.Rows.Count, "second page has the remainder");
        AssertEqual("2 / 2", logs.PageLabel, "second page label");
        AssertEqual(false, logs.CanGoNext, "last page cannot advance");
        logs.NextPageCommand.Execute(null);
        AssertEqual("2 / 2", logs.PageLabel, "next on the last page stays");
        logs.PreviousPageCommand.Execute(null);
        AssertEqual(SessionLogListViewModel.PageSize, logs.Rows.Count, "previous page restores 20 rows");
        viewModel.UpdateTokenCost(new TokenCostStatistics { IsSpeedEstimated = true, SessionLogs = entries.Take(1).ToArray() });
        AssertEqual("1 / 1", logs.PageLabel, "page clamps after the list shrinks");
        AssertEqual(1, logs.Rows.Count, "clamped page shows the remaining row");
        AssertEqual(true, logs.IsOpen, "a refresh keeps the open list");
        viewModel.UpdateTokenCost(null, "pricing unavailable");
        AssertEqual(0, logs.Rows.Count, "a failed collect clears rows");
        AssertEqual(true, logs.IsEmpty, "a failed collect is empty");
        AssertEqual("N/A", logs.EmptyText, "a failed collect shows N/A");
        AssertEqual(true, logs.IsOpen, "a failed collect leaves the list open");
        viewModel.UpdateGrokTokenCost(new TokenCostStatistics
        {
            IsSpeedEstimated = false,
            SessionLogs = [entries[0]],
        });
        AssertEqual("22 tok/s", viewModel.GrokSessionLogs.Rows[0].Speed, "measured Grok speed keeps the unit");
        AssertEqual(0, logs.Rows.Count, "Grok rows do not refill Codex");
        AssertEqual(false, viewModel.GrokSessionLogs.IsOpen, "Grok session logs stay closed");
        logs.ToggleCommand.Execute(null);
        AssertEqual(false, logs.IsOpen, "the same button returns to usage");
        AssertEqual(SessionLogListViewModel.NotesGlyph, logs.ToggleGlyph, "closing session logs keeps the notes glyph");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Common model names stay fully visible, and the notes button only changes color.
    /// </summary>
    private static Task TestSessionLogLayoutAsync()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
                string[] models = ["deepseek-v4-flash", "grok-4.5-build", "gpt-5.3-codex", "gpt-6.1-terra"];
                SessionLogEntry[] entries = Enumerable.Range(0, 21)
                    .Select(index => new SessionLogEntry
                    {
                        Timestamp = new DateTimeOffset(2026, 10, 27, 21, 0, 0, TimeSpan.FromHours(8)).AddMinutes(-index),
                        Model = models[index % models.Length],
                        TimedOutputTokens = 2_200,
                        TimedDurationMilliseconds = 10_000,
                    })
                    .ToArray();
                viewModel.UpdateTokenCost(new TokenCostStatistics { IsSpeedEstimated = true, SessionLogs = entries });
                viewModel.CodexSessionLogs.ToggleCommand.Execute(null);
                TrayPopupWindow window = new(viewModel);
                System.Windows.FrameworkElement content = (System.Windows.FrameworkElement)window.Content;
                void Layout()
                {
                    content.Measure(new System.Windows.Size(window.Width, window.Height));
                    content.Arrange(new System.Windows.Rect(0, 0, window.Width, window.Height));
                    content.UpdateLayout();
                }

                Layout();
                Layout();
                List<System.Windows.Controls.TextBlock> blocks = [];
                CollectTextBlocks(content, blocks);
                System.Windows.Controls.Button notesButton = FindAncestorButton(blocks.First(block => block.Text == SessionLogListViewModel.NotesGlyph));
                AssertEqual(SessionLogListViewModel.NotesGlyph, notesButton.Content, "notes button content");
                AssertEqual(System.Windows.Media.Color.FromRgb(0x1A, 0xBC, 0x89), ((System.Windows.Media.SolidColorBrush)notesButton.Foreground).Color, "open notes button is green");
                AssertTrue(blocks.All(block => block.Text != "Cost" || block.ActualWidth <= 0), "session logs omit the cost column");
                System.Windows.Controls.TextBlock timeHeader = VisibleBlock(blocks, "Time");
                System.Windows.Controls.TextBlock modelHeader = VisibleBlock(blocks, "Model");
                System.Windows.Controls.TextBlock speedHeader = VisibleBlock(blocks, "Speed");
                SessionLogRow sample = viewModel.CodexSessionLogs.Rows[0];
                System.Windows.Controls.TextBlock timeCell = VisibleBlock(blocks, sample.Time);
                System.Windows.Controls.TextBlock speedCell = VisibleBlock(blocks, sample.Speed);
                AssertTrue(timeCell.ToolTip == null, "session log time has no tooltip");
                AssertAligned(timeHeader, timeCell, "time");
                AssertAligned(modelHeader, VisibleBlock(blocks, "deepseek-v4-flash"), "model");
                AssertAligned(speedHeader, speedCell, "speed");
                AssertTrue(Math.Abs(timeCell.ActualWidth - 100) < 1, $"time column is fixed, width {timeCell.ActualWidth:0.0}");
                AssertTrue(Math.Abs(speedCell.ActualWidth - 76) < 1, $"speed column is fixed, width {speedCell.ActualWidth:0.0}");
                AssertGap(timeCell, VisibleBlock(blocks, "deepseek-v4-flash"), "time and model");
                foreach (string model in models)
                {
                    System.Windows.Controls.TextBlock row = VisibleBlock(blocks, model);
                    AssertTrue(row.ToolTip == null, $"{model} has no tooltip");
                    AssertGap(row, speedCell, model);
                    double textWidth = TextWidth(row);
                    AssertTrue(row.ActualWidth + 0.5 >= textWidth, $"{model} is fully visible, slot {row.ActualWidth:0.0}, text {textWidth:0.0}");
                    AssertEqual(44d, ((System.Windows.FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(System.Windows.Media.VisualTreeHelper.GetParent(row))).ActualHeight, "session log row height");
                }

                viewModel.CodexSessionLogs.ToggleCommand.Execute(null);
                Layout();
                AssertEqual(SessionLogListViewModel.NotesGlyph, notesButton.Content, "closing session logs keeps the notes glyph");
                System.Windows.Media.Color closed = ((System.Windows.Media.SolidColorBrush)notesButton.Foreground).Color;
                AssertTrue(closed != System.Windows.Media.Color.FromRgb(0x1A, 0xBC, 0x89), "closed notes button leaves the green state");
                window.Close();

                static System.Windows.Controls.TextBlock VisibleBlock(List<System.Windows.Controls.TextBlock> source, string text)
                {
                    return source.First(block => block.Text == text && block.ActualWidth > 0);
                }

                static double Left(System.Windows.Controls.TextBlock block, System.Windows.FrameworkElement root)
                {
                    return block.TransformToAncestor(root).Transform(new System.Windows.Point(0, 0)).X;
                }

                static double TextWidth(System.Windows.Controls.TextBlock block)
                {
                    System.Windows.Controls.TextBlock probe = new()
                    {
                        Text = block.Text,
                        FontFamily = block.FontFamily,
                        FontSize = block.FontSize,
                        FontWeight = block.FontWeight,
                    };
                    probe.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                    return probe.DesiredSize.Width;
                }

                void AssertAligned(System.Windows.Controls.TextBlock header, System.Windows.Controls.TextBlock cell, string column)
                {
                    double delta = Math.Abs(Left(header, content) - Left(cell, content));
                    AssertTrue(delta < 1, $"{column} header and cell share a left edge, delta {delta:0.0}");
                }

                void AssertGap(System.Windows.Controls.TextBlock leading, System.Windows.Controls.TextBlock following, string label)
                {
                    double gap = Left(following, content) - (Left(leading, content) + TextWidth(leading));
                    AssertTrue(gap >= 6, $"{label} keeps a gap, {gap:0.0}");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw new InvalidOperationException(failure.ToString(), failure);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Walks up to the button that hosts a glyph text block.
    /// </summary>
    private static System.Windows.Controls.Button FindAncestorButton(System.Windows.DependencyObject source)
    {
        System.Windows.DependencyObject? current = source;
        while (current != null)
        {
            if (current is System.Windows.Controls.Button button)
            {
                return button;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        throw new InvalidOperationException("notes button was not found");
    }

    /// <summary>
    /// Collects text blocks under a measured popup.
    /// </summary>
    private static void CollectTextBlocks(System.Windows.DependencyObject root, List<System.Windows.Controls.TextBlock> blocks)
    {
        if (root is System.Windows.Controls.TextBlock block)
        {
            blocks.Add(block);
        }

        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            CollectTextBlocks(System.Windows.Media.VisualTreeHelper.GetChild(root, index), blocks);
        }
    }

    /// <summary>
    /// Session-log rows come from the same Codex increments and Grok turns as the period totals.
    /// </summary>
    private static Task TestSessionLogEntriesAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{\"gpt-fast\":{\"input\":3,\"cachedInput\":0.3,\"output\":15},\"gpt-slow\":{\"input\":3,\"cachedInput\":0.3,\"output\":15}}");
        string sessions = Path.Combine(temp.Path, "codex", "sessions", "2026", "10", "27");
        Directory.CreateDirectory(sessions);
        DateTimeOffset now = new(2026, 10, 27, 22, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset fast = now.AddMinutes(-10);
        DateTimeOffset slow = now.AddMinutes(-30);
        File.WriteAllLines(Path.Combine(sessions, "fast.jsonl"), CreateCodexSpeedWindow(fast, "gpt-fast", 220, 1, 3, 3));
        File.WriteAllLines(Path.Combine(sessions, "slow.jsonl"), CreateCodexSpeedWindow(slow, "gpt-slow", 50, 1, 2, 2));
        File.WriteAllLines(Path.Combine(sessions, "future.jsonl"), CreateCodexSpeedWindow(now.AddDays(1), "gpt-fast", 220, 1, 3, 3));
        TokenCostCollector collector = new(pricingPath);
        string codexRoot = Path.Combine(temp.Path, "codex");
        TokenCostStatistics first = collector.CollectCodex(codexRoot, now);
        AssertEqual(2, first.SessionLogs.Count, "one row per accepted Codex increment, excluding a later local day");
        AssertEqual(true, first.IsSpeedEstimated, "Codex session log speed is estimated");
        AssertEqual("gpt-fast", first.SessionLogs[0].Model, "newest Codex row is first");
        AssertEqual(fast.AddSeconds(3), first.SessionLogs[0].Timestamp, "Codex row time is the token_count time");
        AssertEqual(0.00333m, first.SessionLogs[0].CostUsd, "Codex row uses local pricing");
        AssertEqual(false, first.SessionLogs[0].HasUnpricedUsage, "priced Codex row is complete");
        AssertEqual(2_000L, first.SessionLogs[0].TimedDurationMilliseconds, "Codex row keeps the response window");
        AssertEqual(110m, first.SessionLogs[0].GetOutputTokensPerSecond(), "Codex row speed");
        AssertEqual("gpt-slow", first.SessionLogs[1].Model, "older Codex row is second");
        AssertEqual(0L, first.SessionLogs[1].TimedDurationMilliseconds, "a small Codex output has no speed sample");
        AssertTrue(first.SessionLogs[1].CostUsd != null, "a small Codex output still has a price");
        TokenCostStatistics cached = collector.CollectCodex(codexRoot, now);
        AssertEqual(first.SessionLogs.Count, cached.SessionLogs.Count, "cached Codex collect keeps session log rows");
        AssertEqual(first.SessionLogs[0].TimedOutputTokens, cached.SessionLogs[0].TimedOutputTokens, "cached Codex row keeps speed tokens");
        AssertEqual(first.SessionLogs[0].CostUsd, cached.SessionLogs[0].CostUsd, "cached Codex row keeps cost");

        string grokSession = Path.Combine(temp.Path, "grok", "sessions", "workspace", "one");
        string archived = Path.Combine(temp.Path, "grok", "archived_sessions", "workspace", "one");
        Directory.CreateDirectory(grokSession);
        Directory.CreateDirectory(archived);
        string fastTurn = CreateGrokTokenUpdate("p1", now.AddMinutes(-5), 10, 0, 200, costUsdTicks: 1_326_000_000, apiDurationMilliseconds: 10_000);
        string partialTurn = CreateGrokTokenUpdate("p3", now.AddMinutes(-2), 10, 0, 200, costUsdTicks: 2_000_000_000, costIsPartial: true, apiDurationMilliseconds: 10_000);
        string shortTurn = CreateGrokTokenUpdate("p2", now.AddMinutes(-1), 10, 0, 20, apiDurationMilliseconds: 50);
        string futureTurn = CreateGrokTokenUpdate("future", now.AddDays(1), 10, 0, 200, costUsdTicks: 5_000_000_000, apiDurationMilliseconds: 10_000);
        File.WriteAllLines(Path.Combine(grokSession, "updates.jsonl"), [fastTurn, partialTurn, shortTurn, futureTurn]);
        File.WriteAllLines(Path.Combine(archived, "updates.jsonl"), [fastTurn]);
        TokenCostStatistics grok = new TokenCostCollector(pricingPath).CollectGrok(Path.Combine(temp.Path, "grok"), now);
        AssertEqual(3, grok.SessionLogs.Count, "an archived Grok twin and a later local day are not extra rows");
        AssertEqual(false, grok.IsSpeedEstimated, "Grok speed is measured");
        AssertEqual(now.AddMinutes(-1), grok.SessionLogs[0].Timestamp, "newest Grok row is first");
        AssertEqual(null, grok.SessionLogs[0].CostUsd, "an unknown model does not invent a row cost");
        AssertEqual(true, grok.SessionLogs[0].HasUnpricedUsage, "an unknown model row stays unpriced");
        AssertEqual(0L, grok.SessionLogs[0].TimedDurationMilliseconds, "a short Grok call has no speed");
        AssertEqual(0.2m, grok.SessionLogs[1].CostUsd, "a partial Grok row keeps the reported cost");
        AssertEqual(true, grok.SessionLogs[1].HasUnpricedUsage, "a partial Grok row stays marked");
        AssertEqual(0.1326m, grok.SessionLogs[2].CostUsd, "reported Grok ticks become the row cost");
        AssertEqual(20m, grok.SessionLogs[2].GetOutputTokensPerSecond(), "Grok row speed uses the paired duration");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds one Grok Build turn-completed usage entry with per-model counters and optional API timing.
    /// </summary>
    private static string CreateGrokTokenUpdate(
        string promptId,
        DateTimeOffset timestamp,
        long inputTokens,
        long cachedReadTokens,
        long outputTokens,
        long costUsdTicks = 0,
        bool costIsPartial = false,
        string sessionUpdate = "turn_completed",
        long reasoningTokens = 0,
        long? apiDurationMilliseconds = null)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToUnixTimeSeconds(),
            method = "_x.ai/session/update",
            @params = new
            {
                update = new
                {
                    sessionUpdate,
                    prompt_id = promptId,
                    usage = new
                    {
                        costIsPartial,
                        modelUsage = new Dictionary<string, object>
                        {
                            ["grok-4.5-build"] = new
                            {
                                inputTokens,
                                cachedReadTokens,
                                outputTokens,
                                reasoningTokens,
                                modelCalls = 1,
                                apiDurationMs = apiDurationMilliseconds,
                                costUsdTicks,
                            },
                        },
                    },
                },
            },
        });
    }

    /// <summary>
    /// Builds one Grok Build turn-completed usage entry without modelUsage details, with optional API timing.
    /// </summary>
    private static string CreateGrokTopLevelTokenUpdate(string promptId, DateTimeOffset timestamp, long inputTokens, long cachedReadTokens, long outputTokens, long costUsdTicks,
                                                      long? apiDurationMilliseconds = null)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp = timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            method = "_x.ai/session/update",
            @params = new
            {
                update = new
                {
                    sessionUpdate = "turn_completed",
                    prompt_id = promptId,
                    usage = new
                    {
                        inputTokens,
                        cachedReadTokens,
                        outputTokens,
                        costUsdTicks,
                        apiDurationMs = apiDurationMilliseconds,
                    },
                },
            },
        });
    }

    /// <summary>
    /// Tests all-success, partial-failure, and all-failure API refresh summaries.
    /// </summary>
    private static Task TestApiUsageSummaryAsync()
    {
        DateTimeOffset updatedAt = new(2026, 7, 14, 22, 22, 22, TimeSpan.FromHours(8));
        ApiUsageResult available = new("available", true, "$1.00", string.Empty, string.Empty, updatedAt);
        ApiUsageResult unavailable = new("unavailable", false, "N/A", "N/A", "Request failed", updatedAt.AddSeconds(1));

        AssertEqual(ApiUsageRefreshStatus.AllAvailable, ApiUsageCollector.Summarize([available]).Status, "all API refreshes should be available");
        ApiUsageSummary partial = ApiUsageCollector.Summarize([available, unavailable]);
        AssertEqual(ApiUsageRefreshStatus.PartiallyAvailable, partial.Status, "mixed API refreshes should be partial");
        AssertEqual(1, partial.ErrorCount, "partial API refresh error count");
        AssertEqual(ApiUsageRefreshStatus.Unavailable, ApiUsageCollector.Summarize([unavailable]).Status, "all failed API refreshes should be unavailable");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies prompt publication, isolated failure, visibility and cancellation guards, and independent Grok costs.
    /// </summary>
    private static async Task TestIndependentRefreshAsync()
    {
        TaskCompletionSource<int> slow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<int> fast = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> published = [];
        List<Exception> failures = [];
        Task slowPublication = TrayController.PublishRefreshResultAsync(slow.Task, published.Add, () => true, failures.Add, CancellationToken.None);
        Task fastPublication = TrayController.PublishRefreshResultAsync(fast.Task, published.Add, () => true, failures.Add, CancellationToken.None);
        fast.SetResult(2);
        await fastPublication.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEqual("2", string.Join(',', published), "fast source must publish before the slow source completes");
        AssertTrue(!slowPublication.IsCompleted, "slow source remains owned and pending");
        slow.SetException(new IOException("test collection failed"));
        await slowPublication.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEqual(1, failures.Count, "source failure should be observed independently");
        await TrayController.PublishRefreshResultAsync(Task.FromResult(3), published.Add, () => false, failures.Add, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await TrayController.PublishRefreshResultAsync(Task.FromResult(4), published.Add, () => true, failures.Add, cancellation.Token);
        AssertEqual("2", string.Join(',', published), "hidden or cancelled results must not publish");

        bool visible = true;
        TaskCompletionSource<int> hidden = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task hiddenPublication = TrayController.PublishRefreshResultAsync(hidden.Task, published.Add, () => visible, failures.Add, CancellationToken.None);
        visible = false;
        hidden.SetResult(5);
        await hiddenPublication.WaitAsync(TimeSpan.FromSeconds(5));
        using CancellationTokenSource pendingCancellation = new();
        TaskCompletionSource<int> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task cancelledPublication = TrayController.PublishRefreshResultAsync(cancelled.Task, published.Add, () => true, failures.Add, pendingCancellation.Token);
        pendingCancellation.Cancel();
        cancelled.SetResult(6);
        await cancelledPublication.WaitAsync(TimeSpan.FromSeconds(5));
        TaskCompletionSource<int> lateFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task lateFailurePublication = TrayController.PublishRefreshResultAsync(lateFailure.Task, published.Add, () => true, failures.Add, pendingCancellation.Token);
        lateFailure.SetException(new IOException("late collection failure"));
        await lateFailurePublication.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEqual("2", string.Join(',', published), "visibility and cancellation must be checked after collection completes");
        AssertEqual(1, failures.Count, "late failures must be observed without notifying after cancellation");
        AssertTrue(lateFailure.Task.Exception != null && lateFailurePublication.IsCompletedSuccessfully, "late collection fault must remain observed by its owner");

        TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
        TokenCostStatistics statistics = new() { LastTwentyFourHours = new TokenCostSummary { TotalTokens = 3000 } };
        viewModel.UpdateGrokTokenCost(statistics);
        AssertEqual("3.00K", viewModel.GrokTokenCost.SelectedTokenDisplay, "cost can arrive before billing");
        DateTimeOffset now = DateTimeOffset.Now;
        viewModel.UpdateGrokDashboard(new GrokUsageDashboard(new GrokUsageSnapshot(42, now.ToUnixTimeSeconds() + 100), string.Empty, now));
        AssertEqual("3.00K", viewModel.GrokTokenCost.SelectedTokenDisplay, "billing must preserve separately published costs");
        AssertEqual(System.Windows.Media.Color.FromRgb(26, 188, 137), ((System.Windows.Media.SolidColorBrush)viewModel.GrokStatusDotBrush).Color, "both independent results are available");
        viewModel.UpdateGrokTokenCost(null);
        AssertEqual(System.Windows.Media.Color.FromRgb(226, 176, 54), ((System.Windows.Media.SolidColorBrush)viewModel.GrokStatusDotBrush).Color, "cost failure must preserve billing availability");
    }

    /// <summary>
    /// Verifies runtime repricing even when file length, write time, and session contents are unchanged.
    /// </summary>
    private static Task TestPricingReloadAsync()
    {
        using TempDirectory temp = new();
        string pricing = Path.Combine(temp.Path, "pricing.json");
        string sessions = Path.Combine(temp.Path, "sessions");
        Directory.CreateDirectory(sessions);
        DateTimeOffset now = DateTimeOffset.Now;
        File.WriteAllText(pricing, "{\"gpt-test\":{\"input\":1,\"cachedInput\":0,\"output\":1}}");
        DateTime writeTime = File.GetLastWriteTimeUtc(pricing);
        File.WriteAllLines(Path.Combine(sessions, "test.jsonl"),
        [
            CreateCodexRecord(now.AddMinutes(-1), "turn_context", new { model = "gpt-test" }),
            CreateCodexTokenCount(now.AddSeconds(-30).ToString("O"), last: (100, 0, 100), total: (100, 0, 100)),
        ]);
        TokenCostCollector collector = new(pricing);
        AssertEqual(0.0002m, collector.CollectCodex(temp.Path, now).Today.CostUsd, "initial calculated cost");
        File.WriteAllText(pricing, "{\"gpt-test\":{\"input\":9,\"cachedInput\":0,\"output\":9}}");
        File.SetLastWriteTimeUtc(pricing, writeTime);
        AssertEqual(0.0018m, collector.CollectCodex(temp.Path, now).Today.CostUsd, "unchanged sessions must use new prices");
        File.WriteAllText(pricing, "{}");
        TokenCostStatistics missing = collector.CollectCodex(temp.Path, now);
        AssertTrue(missing.Today.HasUnpricedUsage, "missing price must mark partial costs");
        AssertEqual(200L, missing.Today.TotalTokens, "unknown pricing must preserve tokens");
        AssertEqual(0m, missing.Today.CostUsd, "known cost subtotal remains zero");
        File.WriteAllText(pricing, "{\"gpt-test\":{\"input\":1,\"cachedInput\":0,\"output\":1}}");
        AssertTrue(!collector.CollectCodex(temp.Path, now).Today.HasUnpricedUsage, "restored prices must clear partial status");
        string grokSession = Path.Combine(sessions, "grok-test");
        Directory.CreateDirectory(grokSession);
        File.WriteAllLines(Path.Combine(grokSession, "updates.jsonl"),
            [CreateGrokTokenUpdate("partial", now.AddSeconds(-10), 10, 0, 1, 100_000_000, costIsPartial: true)]);
        TokenCostSummary grok = collector.CollectGrok(temp.Path, now).Today;
        AssertEqual(0.01m, grok.CostUsd, "partial reported fee remains included without local pricing");
        AssertTrue(grok.HasUnpricedUsage, "unpriced partial reported costs must be marked incomplete");
        using (FileStream locked = new(Path.Combine(grokSession, "updates.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool failed = false;
            try
            {
                collector.CollectGrok(temp.Path, now);
            }
            catch (IOException)
            {
                failed = true;
            }

            AssertTrue(failed, "a previously readable Grok session must not silently become a successful zero-cost snapshot");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies failed reads keep success times while clearing values and marking partial costs.
    /// </summary>
    private static Task TestDataFreshnessAsync()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
        TokenCostSummary partial = new() { TotalTokens = 10, CostUsd = 0, HasUnpricedUsage = true };
        viewModel.UpdateTokenCost(new TokenCostStatistics
        {
            LastTwentyFourHours = partial,
            Models = [new TokenCostModelStatistics { Model = "unknown-price", LastTwentyFourHours = partial }],
        });
        AssertTrue(viewModel.CodexTokenCost.SelectedCostDisplay.Contains('*'), "partial cost must be visibly marked");
        DateTimeOffset? lastSuccess = viewModel.CodexTokenCost.Freshness.LastSuccess;
        viewModel.UpdateTokenCost(null, "pricing unavailable");
        AssertEqual(lastSuccess, viewModel.CodexTokenCost.Freshness.LastSuccess, "failure must preserve the success timestamp");
        AssertEqual("N/A", viewModel.CodexTokenCost.SelectedCostDisplay, "failed read must clear old values");
        AssertEqual("pricing unavailable", viewModel.CodexTokenCost.Freshness.Error, "cost failure reason must stay on the refresh state");
        ApiMonitorViewModel card = new(new ApiMonitorSettings { Id = "test", Provider = ApiMonitorSettings.DeepSeekProvider });
        card.Update(new ApiUsageResult("test", true, "¥1.00", "", "", now));
        card.Update(new ApiUsageResult("test", false, "N/A", "", "timeout", now.AddMinutes(1)));
        AssertEqual(now, card.BalanceState.LastSuccess, "API failures must not advance success time");
        AssertEqual("N/A", card.BalanceDisplay, "API failure must clear old balance");
        AssertTrue(card.StatusTooltip.Contains("timeout"), "API failure reason must be accessible");
        ApiUsageResult partialUsage = new("nano", true, "$1.00", "N/A", "", now, Provider: ApiMonitorSettings.NanoGptProvider, UsedError: "Usage timed out");
        ApiMonitorViewModel nano = new(new ApiMonitorSettings { Id = "nano", Provider = ApiMonitorSettings.NanoGptProvider });
        nano.Update(partialUsage);
        AssertTrue(nano.HasSecondaryDisplay && nano.UsedDisplay == "N/A", "failed NanoGPT usage must remain visible as unavailable");
        AssertEqual(ApiUsageRefreshStatus.PartiallyAvailable, ApiUsageCollector.Summarize([partialUsage]).Status, "usage failure must mark API summary partial");
        AssertEqual(System.Windows.Media.Color.FromRgb(226, 176, 54), ((System.Windows.Media.SolidColorBrush)nano.StatusDotBrush).Color, "partial API card must use yellow status");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests that the refresh command tracks its task and prevents concurrent execution.
    /// </summary>
    private static async Task TestRefreshCommandAsync()
    {
        TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshCount = 0;
        Func<Task> refreshAsync = async () =>
        {
            refreshCount++;
            started.TrySetResult(true);
            await release.Task;
        };
        TrayPopupViewModel viewModel = new(new AppSettings(), refreshAsync);

        viewModel.RefreshCommand.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        AssertTrue(viewModel.RefreshCommand.IsRunning, "refresh command should report a running task");
        AssertTrue(!viewModel.RefreshCommand.CanExecute(null), "refresh command should reject concurrent execution");
        Task? executionTask = viewModel.RefreshCommand.ExecutionTask;
        AssertTrue(executionTask != null, "refresh command should expose its execution task");

        viewModel.AddApiMonitorCommand.Execute(null);
        viewModel.ApiMonitors[0].ToggleEditingCommand.Execute(null);

        AssertEqual(1, refreshCount, "programmatic refresh should not reenter a running command");
        AssertTrue(ReferenceEquals(executionTask, viewModel.RefreshCommand.ExecutionTask), "programmatic refresh should preserve the active execution task");

        release.TrySetResult(true);
        await executionTask!;

        AssertEqual(1, refreshCount, "refresh command invocation count");
        AssertTrue(!viewModel.RefreshCommand.IsRunning, "refresh command should stop running after completion");
        AssertTrue(viewModel.RefreshCommand.CanExecute(null), "refresh command should become executable after completion");
    }

    /// <summary>
    /// Verifies the shared token-cost rows, donut, and switchable chart data.
    /// </summary>
    private static Task TestTokenCostChartViewModelAsync()
    {
        TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
        DateTime firstDate = new(2026, 8, 1);
        TokenCostDailySummary[] daily = Enumerable.Range(0, 30)
            .Select(index => new TokenCostDailySummary
            {
                Date = firstDate.AddDays(index),
                Summary = new TokenCostSummary
                {
                    CostUsd = index + 1,
                    TotalTokens = (index + 1) * 100,
                },
            })
            .ToArray();
        TokenCostStatistics statistics = new()
        {
            LastTwentyFourHours = new TokenCostSummary { TotalTokens = 900, CostUsd = 6, CacheReadTokens = 360, CacheableInputTokens = 900 },
            Today = new TokenCostSummary { TotalTokens = 1_000, CostUsd = 7, CacheReadTokens = 400, CacheableInputTokens = 1_000 },
            LastSevenDays = new TokenCostSummary { TotalTokens = 2_000, CostUsd = 13, CacheReadTokens = 260, CacheableInputTokens = 2_000 },
            LastThirtyDays = new TokenCostSummary { TotalTokens = 3_000, CostUsd = 30 },
            CurrentWeek = new TokenCostSummary { TotalTokens = 1_000, CostUsd = 9, CacheReadTokens = 450, CacheableInputTokens = 1_000 },
            CurrentMonth = new TokenCostSummary { TotalTokens = 2_000, CostUsd = 20 },
            CurrentYear = new TokenCostSummary { TotalTokens = 3_500, CostUsd = 80 },
            Lifetime = new TokenCostSummary { TotalTokens = 4_000, CostUsd = 100 },
            LastThirtyDaysDaily = daily,
            CurrentMonthDaily = daily[..20],
            LastSevenDaysDaily = daily[^7..],
            Models =
            [
                new TokenCostModelStatistics
                {
                    Model = "gpt-5.6-sol-high",
                    LastTwentyFourHours = new TokenCostSummary { TotalTokens = 500, CostUsd = 4 },
                    Today = new TokenCostSummary { TotalTokens = 600, CostUsd = 4.5m },
                    LastSevenDays = new TokenCostSummary { TotalTokens = 1_300, CostUsd = 8.45m },
                    CurrentWeek = new TokenCostSummary { TotalTokens = 800, CostUsd = 7.2m },
                    CurrentMonth = new TokenCostSummary { TotalTokens = 1_200, CostUsd = 12 },
                    CurrentYear = new TokenCostSummary { TotalTokens = 1_500, CostUsd = 15 },
                },
                new TokenCostModelStatistics
                {
                    Model = "gpt-5.4",
                    LastTwentyFourHours = new TokenCostSummary { TotalTokens = 400, CostUsd = 2 },
                    Today = new TokenCostSummary { TotalTokens = 400, CostUsd = 2.5m },
                    LastSevenDays = new TokenCostSummary { TotalTokens = 700, CostUsd = 4.55m },
                    CurrentWeek = new TokenCostSummary { TotalTokens = 200, CostUsd = 1.8m },
                    CurrentMonth = new TokenCostSummary { TotalTokens = 800, CostUsd = 8 },
                    CurrentYear = new TokenCostSummary { TotalTokens = 500, CostUsd = 5 },
                },
            ],
        };
        viewModel.UpdateTokenCost(statistics);

        AssertEqual(
            System.Windows.Media.Color.FromRgb(26, 188, 137),
            ((System.Windows.Media.SolidColorBrush)viewModel.CodexTokenCost.AccentBrush).Color,
            "Codex token cost accent should stay green before usage status is available");
        AssertEqual(
            System.Windows.Media.Color.FromRgb(224, 91, 77),
            ((System.Windows.Media.SolidColorBrush)viewModel.CodexStatusDotBrush).Color,
            "Codex usage status should remain red before the first usage refresh");
        AssertEqual("24H|7D|30D|Lifetime", string.Join('|', viewModel.CodexTokenCost.Rows.Select(row => row.Title)), "Codex token cost row titles");
        AssertEqual("$6.00|$13.00|$30.00|$100.00", string.Join('|', viewModel.CodexTokenCost.Rows.Select(row => row.Display.Cost)), "Codex token cost row values");
        AssertEqual(30, viewModel.CodexTokenCost.ChartDays.Count, "Codex token cost chart day count");
        AssertEqual("30", viewModel.CodexTokenCost.ChartDays[29].Label, "Codex token cost chart today label");
        AssertEqual(58d, viewModel.CodexTokenCost.ChartDays[29].BarHeight, "Codex token cost chart maximum height");
        AssertEqual($"2026-08-30{Environment.NewLine}Tokens: 3.00K{Environment.NewLine}Cost: $30.00", viewModel.CodexTokenCost.ChartDays[29].Tooltip, "Codex token cost chart tooltip");
        AssertEqual("1|10|20|30", string.Join('|', viewModel.CodexTokenCost.ChartDays.Where(day => day.Label.Length > 0).Select(day => day.Label)), "Codex token cost chart labels");
        AssertEqual(30, viewModel.CodexTokenCost.ChartColumnCount, "rolling Codex token cost chart columns");
        AssertEqual("1|10|20|30", string.Join('|', viewModel.CodexTokenCost.ChartLabels.Select(label => label.Text)), "rolling Codex token cost chart axis labels");
        AssertEqual("0.90K", viewModel.CodexTokenCost.SelectedTokenDisplay, "Codex selected 24-hour token total");
        AssertEqual("$6.00 · 40%", viewModel.CodexTokenCost.SelectedCostDisplay, "Codex selected 24-hour cost and cache hit");
        AssertEqual("56%|44%", string.Join('|', viewModel.CodexTokenCost.DonutSegments.Select(segment => segment.Share)), "Codex 24-hour donut model shares");
        AssertEqual(
            $"GPT-5.6-sol{Environment.NewLine}Tokens: 0.50K{Environment.NewLine}Cost: $4.00{Environment.NewLine}Share: 56%",
            viewModel.CodexTokenCost.DonutSegments[0].Tooltip,
            "Codex 24-hour donut model tooltip");
        AssertEqual(
            $"GPT-5.4{Environment.NewLine}Tokens: 0.40K{Environment.NewLine}Cost: $2.00{Environment.NewLine}Share: 44%",
            viewModel.CodexTokenCost.DonutSegments[1].Tooltip,
            "Codex 24-hour donut second model tooltip");
        viewModel.CodexTokenCost.ToggleChartPeriodCommand.Execute(null);
        AssertEqual("Today|Week|Month|Year", string.Join('|', viewModel.CodexTokenCost.Rows.Select(row => row.Title)), "calendar Codex token cost row titles");
        AssertEqual("$7.00|$9.00|$20.00|$80.00", string.Join('|', viewModel.CodexTokenCost.Rows.Select(row => row.Display.Cost)), "calendar Codex token cost row values");
        AssertEqual("1.00K", viewModel.CodexTokenCost.SelectedTokenDisplay, "Codex selected today token total");
        AssertEqual("60%|40%", string.Join('|', viewModel.CodexTokenCost.DonutSegments.Select(segment => segment.Share)), "Codex today donut model shares");
        AssertEqual(
            $"GPT-5.6-sol{Environment.NewLine}Tokens: 0.60K{Environment.NewLine}Cost: $4.50{Environment.NewLine}Share: 60%",
            viewModel.CodexTokenCost.DonutSegments[0].Tooltip,
            "Codex today donut model tooltip");
        AssertEqual(31, viewModel.CodexTokenCost.ChartDays.Count, "current-month Codex token cost chart day count");
        AssertEqual(31, viewModel.CodexTokenCost.ChartColumnCount, "current-month Codex token cost chart columns");
        AssertEqual("1|11|21|31", string.Join('|', viewModel.CodexTokenCost.ChartLabels.Select(label => label.Text)), "current-month Codex token cost chart axis labels");
        AssertEqual(58d, viewModel.CodexTokenCost.ChartDays[19].BarHeight, "current-month Codex token cost chart maximum height");
        AssertTrue(viewModel.CodexTokenCost.ChartDays[19].IsInteractive, "current-month current-day slot should be interactive");
        AssertTrue(!viewModel.CodexTokenCost.ChartDays[20].IsInteractive, "current-month future slot should not be interactive");
        AssertTrue(viewModel.CodexTokenCost.ChartDays.Skip(20).All(day => day.BarHeight == 0), "current-month future slots should remain empty");
        viewModel.CodexTokenCost.SelectPeriodCommand.Execute(viewModel.CodexTokenCost.Rows[1]);
        AssertEqual("1.00K", viewModel.CodexTokenCost.SelectedTokenDisplay, "Codex selected current-week token total");
        AssertEqual("$9.00 · 45%", viewModel.CodexTokenCost.SelectedCostDisplay, "Codex selected current-week cost and cache hit");
        AssertEqual("80%|20%", string.Join('|', viewModel.CodexTokenCost.DonutSegments.Select(segment => segment.Share)), "Codex current-week donut model shares");
        AssertEqual(
            $"GPT-5.6-sol{Environment.NewLine}Tokens: 0.80K{Environment.NewLine}Cost: $7.20{Environment.NewLine}Share: 80%",
            viewModel.CodexTokenCost.DonutSegments[0].Tooltip,
            "Codex current-week donut model tooltip");
        viewModel.CodexTokenCost.SelectPeriodCommand.Execute(viewModel.CodexTokenCost.Rows[3]);
        AssertEqual("3.50K", viewModel.CodexTokenCost.SelectedTokenDisplay, "Codex selected current-year token total");
        AssertEqual("$80.00 · 0%", viewModel.CodexTokenCost.SelectedCostDisplay, "Codex selected current-year cost and cache hit");
        AssertEqual("75%|25%", string.Join('|', viewModel.CodexTokenCost.DonutSegments.Select(segment => segment.Share)), "Codex current-year donut model shares");
        AssertEqual(
            $"GPT-5.6-sol{Environment.NewLine}Tokens: 1.50K{Environment.NewLine}Cost: $15.00{Environment.NewLine}Share: 75%",
            viewModel.CodexTokenCost.DonutSegments[0].Tooltip,
            "Codex current-year donut model tooltip");
        viewModel.CodexTokenCost.SelectPeriodCommand.Execute(viewModel.CodexTokenCost.Rows[1]);
        viewModel.CodexTokenCost.ToggleChartPeriodCommand.Execute(null);
        AssertEqual("24H|7D|30D|Lifetime", string.Join('|', viewModel.CodexTokenCost.Rows.Select(row => row.Title)), "restored Codex token cost row titles");
        AssertEqual("$6.00|$13.00|$30.00|$100.00", string.Join('|', viewModel.CodexTokenCost.Rows.Select(row => row.Display.Cost)), "restored Codex token cost row values");
        AssertEqual("2.00K", viewModel.CodexTokenCost.SelectedTokenDisplay, "Codex selected 7D token total");
        AssertEqual("$13.00 · 13%", viewModel.CodexTokenCost.SelectedCostDisplay, "Codex selected 7D cost and cache hit");
        AssertEqual("GPT-5.6-sol|GPT-5.4", string.Join('|', viewModel.CodexTokenCost.DonutSegments.Select(segment => segment.Label)), "Codex donut model labels");
        AssertEqual("65%|35%", string.Join('|', viewModel.CodexTokenCost.DonutSegments.Select(segment => segment.Share)), "Codex 7D donut model shares");
        AssertEqual(
            $"GPT-5.6-sol{Environment.NewLine}Tokens: 1.30K{Environment.NewLine}Cost: $8.45{Environment.NewLine}Share: 65%",
            viewModel.CodexTokenCost.DonutSegments[0].Tooltip,
            "Codex 7D donut model tooltip");
        AssertTrue(viewModel.CodexTokenCost.Rows[1].IsSelected, "Codex 7D row should be selected");

        TokenCostStatistics grokStatistics = new()
        {
            LastTwentyFourHours = new TokenCostSummary { TotalTokens = 600 },
            Today = new TokenCostSummary { TotalTokens = 700 },
            LastSevenDays = new TokenCostSummary { TotalTokens = 2_800 },
            LastThirtyDays = new TokenCostSummary { TotalTokens = 2_800 },
            Lifetime = new TokenCostSummary { TotalTokens = 2_800 },
            LastThirtyDaysDaily = daily
                .Select(day => new TokenCostDailySummary
                {
                    Date = day.Date,
                    Summary = new TokenCostSummary { TotalTokens = day.Summary.TotalTokens },
                })
                .ToArray(),
        };
        viewModel.UpdateGrokDashboard(new GrokUsageDashboard(null, "N/A", DateTimeOffset.Now), grokStatistics);
        AssertEqual(
            System.Windows.Media.Color.FromRgb(226, 176, 54),
            ((System.Windows.Media.SolidColorBrush)viewModel.GrokStatusDotBrush).Color,
            "Grok usage-only token cost should keep a yellow status");
        AssertEqual(
            System.Windows.Media.Color.FromRgb(26, 188, 137),
            ((System.Windows.Media.SolidColorBrush)viewModel.GrokTokenCost.AccentBrush).Color,
            "Grok token cost accent should stay green when usage is unavailable");
        AssertEqual("24H|7D|30D|Lifetime", string.Join('|', viewModel.GrokTokenCost.Rows.Select(row => row.Title)), "Grok token cost row titles");
        AssertEqual("N/A|N/A|N/A|N/A", string.Join('|', viewModel.GrokTokenCost.Rows.Select(row => row.Display.Cost)), "Grok unknown cost values");
        AssertEqual(58d, viewModel.GrokTokenCost.ChartDays[29].BarHeight, "Grok token chart maximum height");
        AssertEqual($"2026-08-30{Environment.NewLine}Tokens: 3.00K", viewModel.GrokTokenCost.ChartDays[29].Tooltip, "Grok token chart tooltip");
        AssertEqual("0%", viewModel.GrokTokenCost.SelectedCostDisplay, "unknown cost omits the dollar value and keeps cache hit");

        viewModel.CodexTokenCost.SelectPeriodCommand.Execute(viewModel.CodexTokenCost.Rows[0]);
        viewModel.UpdateTokenCost(new TokenCostStatistics
        {
            LastTwentyFourHours = new TokenCostSummary { TotalTokens = 1_500, CostUsd = 15 },
            Models =
            [
                new TokenCostModelStatistics { Model = "a", LastTwentyFourHours = new TokenCostSummary { TotalTokens = 500, CostUsd = 5 } },
                new TokenCostModelStatistics { Model = "b", LastTwentyFourHours = new TokenCostSummary { TotalTokens = 400, CostUsd = 4 } },
                new TokenCostModelStatistics { Model = "c", LastTwentyFourHours = new TokenCostSummary { TotalTokens = 300, CostUsd = 3 } },
                new TokenCostModelStatistics { Model = "d", LastTwentyFourHours = new TokenCostSummary { TotalTokens = 200, CostUsd = 2 } },
                new TokenCostModelStatistics { Model = "e", LastTwentyFourHours = new TokenCostSummary { TotalTokens = 100, CostUsd = 1 } },
            ],
        });
        AssertEqual("a|b|c|Other", string.Join('|', viewModel.CodexTokenCost.DonutSegments.Select(segment => segment.Label)), "Codex donut other model labels");
        AssertEqual(
            $"Other{Environment.NewLine}Tokens: 0.30K{Environment.NewLine}Cost: $3.00{Environment.NewLine}Share: 20%",
            viewModel.CodexTokenCost.DonutSegments[3].Tooltip,
            "Codex donut other model tooltip");

        viewModel.UpdateTokenCost(new TokenCostStatistics
        {
            LastTwentyFourHours = new TokenCostSummary { TotalTokens = 241_460 },
            Models =
            [
                new TokenCostModelStatistics
                {
                    Model = "grok-4.6-build",
                    LastTwentyFourHours = new TokenCostSummary { TotalTokens = 241_460 },
                },
            ],
        });
        AssertEqual(
            $"grok-4.6-build{Environment.NewLine}Tokens: 241.46K{Environment.NewLine}Share: 100%",
            viewModel.CodexTokenCost.DonutSegments[0].Tooltip,
            "unpriced donut model tooltip");

        viewModel.UpdateTokenCost(null);
        AssertEqual(30, viewModel.CodexTokenCost.ChartDays.Count, "unavailable Codex token cost chart day count");
        AssertEqual(0, viewModel.CodexTokenCost.DonutSegments.Count, "unavailable Codex donut segment count");
        AssertEqual("N/A", viewModel.CodexTokenCost.SelectedTokenDisplay, "unavailable Codex token total");
        AssertEqual("N/A", viewModel.CodexTokenCost.SelectedCostDisplay, "unavailable Codex cost and cache hit");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests dirty tracking for every migrated editable property.
    /// </summary>
    private static Task TestMigratedDirtyPropertiesAsync()
    {
        List<(string Name, Action<TrayPopupViewModel> Change, Action<TrayPopupViewModel> Restore)> cases =
        [
            (nameof(TrayPopupViewModel.LiteMonitorDir), viewModel => viewModel.LiteMonitorDir = "C:\\LiteMonitor", viewModel => viewModel.LiteMonitorDir = string.Empty),
            (nameof(TrayPopupViewModel.TrafficMonitorDir),
             viewModel => viewModel.TrafficMonitorDir = "C:\\TrafficMonitor",
             viewModel => viewModel.TrafficMonitorDir = string.Empty),
            (nameof(TrayPopupViewModel.PortText),
             viewModel => viewModel.PortText = "17891",
             viewModel => viewModel.PortText = CodexTrayDefaults.Port.ToString(CultureInfo.InvariantCulture)),
            (nameof(TrayPopupViewModel.RefreshIntervalText),
             viewModel => viewModel.RefreshIntervalText = "2",
             viewModel => viewModel.RefreshIntervalText = CodexTrayDefaults.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture)),
            (nameof(TrayPopupViewModel.StartWithWindows), viewModel => viewModel.StartWithWindows = true, viewModel => viewModel.StartWithWindows = false),
            (nameof(TrayPopupViewModel.UseAbsoluteResetTime),
             viewModel => viewModel.UseAbsoluteResetTime = !CodexTrayDefaults.UseAbsoluteResetTime,
             viewModel => viewModel.UseAbsoluteResetTime = CodexTrayDefaults.UseAbsoluteResetTime),
        ];

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            cases.Add((nameof(TrayPopupViewModel.MicaEnabled), viewModel => viewModel.MicaEnabled = false, viewModel => viewModel.MicaEnabled = true));
        }

        foreach ((string name, Action<TrayPopupViewModel> change, Action<TrayPopupViewModel> restore) in cases)
        {
            TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
            change(viewModel);
            AssertEqual(SettingsStatus.Unsaved, viewModel.SettingsStatus, $"{name} should mark settings dirty");
            AssertTrue(viewModel.SaveSettingsCommand.CanExecute(null), $"{name} should enable save");
            restore(viewModel);
            AssertEqual(SettingsStatus.Clean, viewModel.SettingsStatus, $"{name} should restore clean status");
            AssertTrue(!viewModel.SaveSettingsCommand.CanExecute(null), $"{name} should disable save after restore");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests exact dependent and unchanged-value notifications for migrated tray properties.
    /// </summary>
    private static Task TestMigratedTrayNotificationsAsync()
    {
        TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.LiteMonitorDir = "C:\\LiteMonitor";
        AssertEqual(
            "LiteMonitorDir|LiteMonitorDirDisplay|SettingsStatus|SettingsStatusBrush|SettingsStatusText",
            string.Join('|', changedProperties.Order()),
            "LiteMonitor path notifications");
        changedProperties.Clear();
        viewModel.LiteMonitorDir = "C:\\LiteMonitor";
        AssertEqual(0, changedProperties.Count, "unchanged LiteMonitor path notifications");

        viewModel = new(new AppSettings(), () => Task.CompletedTask);
        changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        viewModel.TrafficMonitorDir = "C:\\TrafficMonitor";
        AssertEqual(
            "SettingsStatus|SettingsStatusBrush|SettingsStatusText|TrafficMonitorDir|TrafficMonitorDirDisplay",
            string.Join('|', changedProperties.Order()),
            "TrafficMonitor path notifications");

        viewModel = new(new AppSettings(), () => Task.CompletedTask);
        changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        viewModel.IsDetectingLiteMonitor = true;
        AssertEqual("IsDetectingLiteMonitor|IsLiteMonitorActionsEnabled", string.Join('|', changedProperties.Order()), "LiteMonitor detecting notifications");
        changedProperties.Clear();
        viewModel.IsDetectingLiteMonitor = true;
        AssertEqual(0, changedProperties.Count, "unchanged LiteMonitor detecting notifications");
        viewModel.IsDetectingTrafficMonitor = true;
        AssertEqual("IsDetectingTrafficMonitor|IsTrafficMonitorActionsEnabled", string.Join('|', changedProperties.Order()), "TrafficMonitor detecting notifications");

        changedProperties.Clear();
        viewModel.IsRefreshing = true;
        AssertEqual(nameof(TrayPopupViewModel.IsRefreshing), string.Join('|', changedProperties), "pure generated property notification");
        changedProperties.Clear();
        viewModel.IsRefreshing = true;
        AssertEqual(0, changedProperties.Count, "unchanged pure generated property notifications");

        InAppDialogRequest initialDialog = new("Title", "Message", "OK");
        viewModel.ShowInAppDialog(initialDialog);
        changedProperties.Clear();
        InAppDialogRequest secondaryDialog = initialDialog with { SecondaryButtonText = "Cancel" };
        viewModel.ShowInAppDialog(secondaryDialog);
        AssertEqual(
            "HasInAppDialogSecondaryButton|InAppDialogSecondaryButtonText",
            string.Join('|', changedProperties.Order()),
            "dialog secondary button notifications");
        changedProperties.Clear();
        viewModel.ShowInAppDialog(secondaryDialog);
        AssertEqual(0, changedProperties.Count, "unchanged dialog secondary button notifications");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests save and API monitor move command states.
    /// </summary>
    private static Task TestApiMonitorCommandStatesAsync()
    {
        AppSettings settings = new();
        TrayPopupViewModel viewModel = new(settings, () => Task.CompletedTask);

        AssertTrue(!viewModel.SaveSettingsCommand.CanExecute(null), "save command should be disabled for clean settings");
        viewModel.PortText = (CodexTrayDefaults.Port + 1).ToString(CultureInfo.InvariantCulture);
        AssertTrue(viewModel.SaveSettingsCommand.CanExecute(null), "save command should be enabled for dirty settings");

        viewModel.AddApiMonitorCommand.Execute(null);
        viewModel.AddApiMonitorCommand.Execute(null);
        ApiMonitorViewModel first = viewModel.ApiMonitors[0];
        ApiMonitorViewModel second = viewModel.ApiMonitors[1];

        AssertTrue(!viewModel.MoveApiMonitorUpCommand.CanExecute(first), "first API monitor should not move up");
        AssertTrue(viewModel.MoveApiMonitorDownCommand.CanExecute(first), "first API monitor should move down");
        AssertTrue(viewModel.MoveApiMonitorUpCommand.CanExecute(second), "last API monitor should move up");
        AssertTrue(!viewModel.MoveApiMonitorDownCommand.CanExecute(second), "last API monitor should not move down");

        viewModel.MoveApiMonitorDownCommand.Execute(first);

        AssertEqual(first, viewModel.ApiMonitors[1], "API monitor should move down");
        AssertTrue(viewModel.MoveApiMonitorUpCommand.CanExecute(first), "moved API monitor should move up");
        AssertTrue(!viewModel.MoveApiMonitorDownCommand.CanExecute(first), "moved API monitor should not move below the last position");
        AssertEqual(0, settings.ApiMonitors.Count, "reordering pending cards must not save them");
        first.ToggleEditingCommand.Execute(null);
        second.ToggleEditingCommand.Execute(null);
        string savedName = settings.ApiMonitors.Single(card => card.Id == first.Id).Name;
        first.ToggleEditingCommand.Execute(null);
        first.Name = "Unfinished edit";
        viewModel.MoveApiMonitorUpCommand.Execute(first);
        AssertEqual(first.Id, settings.ApiMonitors[0].Id, "reordering saved cards must persist their order");
        AssertEqual(savedName, settings.ApiMonitors[0].Name, "reordering must not persist unfinished card fields");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests dependent property notifications after migrating to Toolkit ObservableObject.
    /// </summary>
    private static Task TestApiMonitorNotificationsAsync()
    {
        ApiMonitorViewModel viewModel = new(new ApiMonitorSettings());
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.Name = "Personal";
        AssertEqual("DisplayName|Name", string.Join('|', changedProperties.Order()), "API monitor name notifications");
        changedProperties.Clear();
        viewModel.Name = "Personal";
        AssertEqual(0, changedProperties.Count, "unchanged API monitor name notifications");

        ApiUsageResult result = new(viewModel.Id, false, "N/A", "N/A", "Waiting for refresh", DateTimeOffset.UtcNow, "USD balance");
        viewModel.Update(result);
        AssertEqual(
            "BalanceTooltip|HasBalanceTooltip|StatusTooltip",
            string.Join('|', changedProperties.Order()),
            "balance tooltip notifications");
        changedProperties.Clear();
        viewModel.Update(result);
        AssertEqual(0, changedProperties.Count, "unchanged balance tooltip notifications");

        AssertEqual(
            string.Join('|', [
                ApiMonitorSettings.DeepSeekProvider,
                ApiMonitorSettings.OpenRouterProvider,
                ApiMonitorSettings.VercelProvider,
                ApiMonitorSettings.NanoGptProvider,
                ApiMonitorSettings.NewApiProvider,
            ]),
            string.Join('|', viewModel.ProviderOptions),
            "API provider option order");
        AssertTrue(!viewModel.ProviderOptions.Contains("Grok", StringComparer.Ordinal), "Grok should not be an API provider option");
        viewModel.Provider = ApiMonitorSettings.OpenRouterProvider;
        AssertEqual("https://openrouter.ai", viewModel.BaseUrl, "OpenRouter default base URL");
        AssertTrue(!viewModel.HasSecondaryDisplay, "OpenRouter waiting secondary display");
        viewModel.Update(new ApiUsageResult(viewModel.Id, true, "$74.75", "$25.75", string.Empty, DateTimeOffset.UtcNow));
        AssertTrue(viewModel.HasSecondaryDisplay, "OpenRouter secondary display");
        viewModel.Provider = ApiMonitorSettings.NanoGptProvider;
        AssertEqual("https://nano-gpt.com", viewModel.BaseUrl, "NanoGPT default base URL");
        AssertEqual("N/A", viewModel.BalanceDisplay, "provider switch should clear balance");
        AssertEqual("N/A", viewModel.UsedDisplay, "provider switch should clear used display");
        AssertEqual(string.Empty, viewModel.BalanceTooltip, "provider switch should clear tooltip");
        AssertEqual("Waiting for refresh", viewModel.StatusText, "provider switch should clear status");
        AssertEqual("Used(30d):", viewModel.SecondaryDisplayLabel, "NanoGPT secondary display label");
        viewModel.Update(new ApiUsageResult(
            viewModel.Id,
            true,
            "$74.75",
            "$25.75",
            string.Empty,
            DateTimeOffset.UtcNow,
            Provider: ApiMonitorSettings.OpenRouterProvider));
        AssertEqual("N/A", viewModel.BalanceDisplay, "late OpenRouter result should be ignored");
        viewModel.Update(new ApiUsageResult(
            viewModel.Id,
            true,
            "$12.35",
            "$3.00",
            string.Empty,
            DateTimeOffset.UtcNow,
            Provider: ApiMonitorSettings.NanoGptProvider));
        AssertTrue(viewModel.HasSecondaryDisplay, "NanoGPT successful usage display");
        AssertEqual("$12.35", viewModel.BalanceDisplay, "current NanoGPT result should update balance");
        changedProperties.Clear();
        viewModel.Update(new ApiUsageResult(viewModel.Id, true, "$12.35", "N/A", string.Empty, DateTimeOffset.UtcNow, UsedError: "Usage failed"));
        AssertTrue(viewModel.HasSecondaryDisplay, "NanoGPT failed usage should retain the unavailable secondary display");
        AssertTrue(changedProperties.Contains(nameof(ApiMonitorViewModel.HasSecondaryDisplay)), "NanoGPT secondary visibility notification");
        viewModel.Provider = ApiMonitorSettings.VercelProvider;
        AssertEqual("https://ai-gateway.vercel.sh", viewModel.BaseUrl, "Vercel default base URL");
        AssertTrue(!viewModel.HasSecondaryDisplay, "Vercel waiting secondary display");
        viewModel.Update(new ApiUsageResult(viewModel.Id, true, "$95.50", "$4.50", string.Empty, DateTimeOffset.UtcNow));
        AssertTrue(viewModel.HasSecondaryDisplay, "Vercel secondary display");
        viewModel.BaseUrl = "https://custom.example";
        viewModel.Provider = ApiMonitorSettings.DeepSeekProvider;
        AssertEqual("https://custom.example", viewModel.BaseUrl, "custom base URL should be preserved");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests that a weekly window in the primary slot is not mistaken for a five hour window.
    /// </summary>
    private static async Task TestLoneWeeklyQuotaAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 13, 12, 0, 0, TimeSpan.FromHours(8));
        File.WriteAllText(Path.Combine(temp.Path, "auth.json"), JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt",
            tokens = new
            {
                access_token = "test-token",
                account_id = "account-123",
            },
        }));

        string body = JsonSerializer.Serialize(new
        {
            rate_limit = new
            {
                primary_window = new
                {
                    used_percent = 58.0,
                    limit_window_seconds = 604800,
                    reset_at = now.AddDays(6).AddHours(23).ToUnixTimeSeconds(),
                },
            },
        });
        using HttpClient client = new(new FakeHttpMessageHandler(body));
        CodexUsageCollector collector = new(() => now, client);

        UsageResponse response = await collector.CollectAsync(temp.Path);

        AssertTrue(response.Available, "response should be available");
        AssertEqual(0, response.Limits.Session.WindowMinutes, "session window should be absent");
        AssertEqual(10080, response.Limits.Weekly.WindowMinutes, "weekly window duration");
        AssertEqual(42, response.Limits.Weekly.RemainingPercent, "weekly remaining percent");
        AssertEqual("42%", response.Display.Weekly, "weekly plugin display");
    }

    /// <summary>
    /// Tests reset credit parsing and local expiry conversion.
    /// </summary>
    private static async Task TestResetCreditsAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset nearestExpiry = new(2026, 7, 14, 18, 30, 0, TimeSpan.Zero);
        string resetCreditsBody = JsonSerializer.Serialize(new
        {
            available_count = 3,
            credits = new[]
            {
                new { status = "available", expires_at = nearestExpiry.AddDays(2).ToString("O") },
                new { status = "redeemed", expires_at = nearestExpiry.AddHours(-1).ToString("O") },
                new { status = "available", expires_at = nearestExpiry.ToString("O") },
                new { status = "available", expires_at = nearestExpiry.AddDays(1).ToString("O") },
            },
        });
        CodexUsageCollector collector = CreateCodexUsageCollector(temp.Path, now, now.AddHours(1).ToUnixTimeSeconds(), now.AddDays(2).ToUnixTimeSeconds(), 10.0, 20.0, out HttpClient client, resetCreditsBody);
        using HttpClient _ = client;

        UsageResponse response = await collector.CollectAsync(temp.Path);

        AssertTrue(response.ResetCredits.Available, "reset credits should be available");
        AssertEqual(3, response.ResetCredits.AvailableCount, "reset credit count");
        AssertEqual(nearestExpiry.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), response.ResetCredits.NearestExpiryLocal, "nearest local reset credit expiry");
        AssertEqual(
            $"{nearestExpiry.AddDays(1).ToLocalTime():MM-dd} · {nearestExpiry.AddDays(2).ToLocalTime():MM-dd}",
            response.ResetCredits.OtherExpiriesLocal,
            "other local reset credit expiries");
    }

    /// <summary>
    /// Verifies quota thresholds and reset credit availability states used by the UI color bindings.
    /// </summary>
    private static Task TestQuotaAndResetCreditColorStatesAsync()
    {
        TrayPopupViewModel.QuotaViewModel quota = new("Test");
        UsageLimit limit = new() { WindowMinutes = 1 };
        (int remaining, System.Windows.Media.Color color)[] cases =
        [
            (0, System.Windows.Media.Color.FromRgb(224, 91, 77)),
            (19, System.Windows.Media.Color.FromRgb(224, 91, 77)),
            (20, System.Windows.Media.Color.FromRgb(226, 176, 54)),
            (49, System.Windows.Media.Color.FromRgb(226, 176, 54)),
            (50, System.Windows.Media.Color.FromRgb(26, 188, 137)),
            (100, System.Windows.Media.Color.FromRgb(26, 188, 137)),
        ];

        foreach ((int remaining, System.Windows.Media.Color expectedColor) in cases)
        {
            limit.RemainingPercent = remaining;
            quota.Update(limit);
            System.Windows.Media.Color actualColor = ((System.Windows.Media.SolidColorBrush)quota.AccentBrush).Color;
            AssertEqual(expectedColor, actualColor, $"quota color at {remaining}%");
        }

        limit.WindowMinutes = 0;
        quota.Update(limit);
        AssertEqual("N/A", quota.PercentText, "inactive quota percent text");
        AssertEqual("unknown", quota.ResetText, "inactive quota reset text");
        AssertTrue(quota.IsResetVisible, "inactive visible quota should show the unknown reset text");

        TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
        UsageResponse response = new()
        {
            Available = true,
            ResetCredits = new ResetCredits { Available = true, AvailableCount = 0 },
        };
        viewModel.UpdateStatus(isRunning: true, CodexTrayDefaults.Port, response, error: null);
        AssertTrue(!viewModel.CodexHasResetCredits, "zero reset credits should use the inactive color");
        AssertEqual("0 Available", viewModel.CodexResetCreditsDisplay, "zero reset credits should show the full Available label");
        AssertTrue(!viewModel.CodexSessionQuota.IsVisible, "missing Session window should hide its quota page");
        AssertTrue(!viewModel.CodexQuotaPager.HasMultiplePages, "missing Session window should leave one Codex quota page");
        AssertTrue(ReferenceEquals(viewModel.CodexQuotaPager.FirstQuota, viewModel.CodexSessionQuota), "Session stays on the first Codex quota page");
        AssertTrue(ReferenceEquals(viewModel.CodexQuotaPager.SecondQuota, viewModel.CodexWeeklyQuota), "Weekly stays on the second Codex quota page");

        response.ResetCredits.AvailableCount = 1;
        viewModel.UpdateStatus(isRunning: true, CodexTrayDefaults.Port, response, error: null);
        AssertTrue(viewModel.CodexHasResetCredits, "positive reset credits should use the active color");
        AssertEqual("1 Available", viewModel.CodexResetCreditsDisplay, "positive reset credits should show the full Available label");

        response.Limits.Session = new UsageLimit { WindowMinutes = 300, RemainingPercent = 40, ResetLabel = "1h15m" };
        response.Limits.Weekly = new UsageLimit { WindowMinutes = 10_080, RemainingPercent = 80, ResetLabel = "3d04h" };
        viewModel.UpdateStatus(isRunning: true, CodexTrayDefaults.Port, response, error: null);
        AssertTrue(viewModel.CodexSessionQuota.IsVisible, "valid Session window should show its quota page");
        AssertTrue(viewModel.CodexQuotaPager.HasMultiplePages, "valid Session window should enable Codex quota paging");
        AssertEqual("40%", viewModel.CodexSessionQuota.PercentText, "valid Session percent text");
        AssertEqual("1h15m", viewModel.CodexSessionQuota.ResetText, "valid Session reset text");
        AssertEqual("80%", viewModel.CodexWeeklyQuota.PercentText, "Weekly percent text after Session becomes valid");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps Codex usage plan_type values onto the current Pro 5x/10x/25x badge labels.
    /// </summary>
    private static Task TestCodexPlanDisplayAsync()
    {
        TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
        UsageResponse response = new() { Available = true };
        (string planType, string display)[] cases =
        [
            ("free", "FREE"),
            ("go", "GO"),
            ("plus", "PLUS"),
            ("chatgpt", "PLUS"),
            ("prolite", "PRO 5X"),
            ("pro_lite", "PRO 5X"),
            ("pro-5x", "PRO 5X"),
            ("pro", "PRO 10X"),
            ("pro_10x", "PRO 10X"),
            ("pro_20x", "PRO 10X"),
            ("promax", "PRO 25X"),
            ("pro_max", "PRO 25X"),
            ("pro_25x", "PRO 25X"),
            ("business", "UNKNOWN"),
        ];

        foreach ((string planType, string display) in cases)
        {
            response.PlanType = planType;
            viewModel.UpdateStatus(isRunning: true, CodexTrayDefaults.Port, response, error: null);
            AssertEqual(display, viewModel.CodexPlanDisplay, $"Codex badge for {planType}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies cache-read over cacheable-input hit percent rounding.
    /// </summary>
    private static Task TestCacheHitPercentAsync()
    {
        AssertEqual(0, new TokenCostSummary().GetCacheHitPercent(), "empty period cache hit");
        AssertEqual(0, new TokenCostSummary { CacheableInputTokens = 100 }.GetCacheHitPercent(), "input without cache reads");
        AssertEqual(40, new TokenCostSummary { CacheReadTokens = 400, CacheableInputTokens = 1_000 }.GetCacheHitPercent(), "cc-switch style cache hit");
        AssertEqual(67, new TokenCostSummary { CacheReadTokens = 2, CacheableInputTokens = 3 }.GetCacheHitPercent(), "rounded cache hit");
        AssertEqual(100, new TokenCostSummary { CacheReadTokens = 995, CacheableInputTokens = 1_000 }.GetCacheHitPercent(), "near-complete cache hit");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies duration-weighted speed, invalid timing, deduplication, period selection, and donut model grouping.
    /// </summary>
    private static Task TestModelOutputSpeedAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{}");
        string active = Path.Combine(temp.Path, "sessions", "workspace", "speed-session");
        string archived = Path.Combine(temp.Path, "archived_sessions", "workspace", "speed-session");
        Directory.CreateDirectory(active);
        Directory.CreateDirectory(archived);
        DateTimeOffset now = new(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset today = now.AddHours(-3);
        string fast = CreateGrokTokenUpdate("fast", today, 10, 0, 100, reasoningTokens: 80, apiDurationMilliseconds: 1_000);
        List<string> lines =
        [
            fast,
            CreateGrokTokenUpdate("slow", today.AddMinutes(1), 10, 0, 100, apiDurationMilliseconds: 9_000),
            CreateGrokTokenUpdate("untimed", today.AddMinutes(2), 10, 0, 900),
            CreateGrokTokenUpdate("yesterday", today.AddHours(-25), 10, 0, 300, apiDurationMilliseconds: 2_000),
            CreateGrokTopLevelTokenUpdate("top-level", today, 10, 0, 25, 0, apiDurationMilliseconds: 500),
            CreateGrokTokenUpdate("zero", today, 10, 0, 0, apiDurationMilliseconds: 1_000)
                .Replace("grok-4.5-build", "zero-output", StringComparison.Ordinal),
            CreateGrokTokenUpdate("few", today, 10, 0, 99, apiDurationMilliseconds: 1_000)
                .Replace("grok-4.5-build", "few-output", StringComparison.Ordinal),
            CreateGrokTokenUpdate("brief", today, 10, 0, 100, apiDurationMilliseconds: 99)
                .Replace("grok-4.5-build", "short-duration", StringComparison.Ordinal),
        ];
        string[] invalidDurations = ["null", "0", "-1", "\"1000\"", "1.5", "9223372036854775808"];
        for (int index = 0; index < invalidDurations.Length; index++)
        {
            lines.Add(CreateGrokTokenUpdate($"invalid-{index}", today, 10, 0, 50, apiDurationMilliseconds: 1_000)
                .Replace("grok-4.5-build", "invalid-timing", StringComparison.Ordinal)
                .Replace("\"apiDurationMs\":1000", $"\"apiDurationMs\":{invalidDurations[index]}", StringComparison.Ordinal));
        }

        lines.Add(CreateGrokTokenUpdate("missing-output", today, 10, 0, 50, apiDurationMilliseconds: 1_000)
            .Replace("grok-4.5-build", "invalid-timing", StringComparison.Ordinal)
            .Replace("\"outputTokens\":50", "\"unusedOutputTokens\":50", StringComparison.Ordinal));
        lines.Add(JsonSerializer.Serialize(new
        {
            timestamp = today.ToUnixTimeSeconds(),
            method = "_x.ai/session/update",
            @params = new
            {
                update = new
                {
                    sessionUpdate = "turn_completed",
                    prompt_id = "multi-model",
                    usage = new
                    {
                        apiDurationMs = 99_999,
                        modelUsage = new Dictionary<string, object>
                        {
                            ["model-a"] = new { inputTokens = 10, outputTokens = 100, apiDurationMs = 1_000 },
                            ["model-b"] = new { inputTokens = 10, outputTokens = 100, apiDurationMs = 4_000 },
                        },
                    },
                },
            },
        }));
        File.WriteAllLines(Path.Combine(active, "updates.jsonl"), lines);
        File.WriteAllLines(Path.Combine(archived, "updates.jsonl"), [fast]);
        TokenCostStatistics statistics = new TokenCostCollector(pricingPath).CollectGrok(temp.Path, now);
        TokenCostModelStatistics model = statistics.Models.Single(item => item.Model == "grok-4.5-build");
        AssertEqual(1_130L, model.Today.TotalTokens, "untimed usage still contributes tokens without duplicating archived turns");
        AssertEqual(200L, model.Today.TimedOutputTokens, "speed excludes untimed output and does not add reasoning twice");
        AssertEqual(10_000L, model.Today.TimedDurationMilliseconds, "paired API duration survives turn deduplication");
        AssertEqual(20m, model.Today.GetOutputTokensPerSecond(), "speed uses summed output over summed durations");
        AssertEqual(20m, model.LastTwentyFourHours.GetOutputTokensPerSecond(), "24-hour speed excludes older timing");
        foreach (TokenCostSummary summary in new[] { model.LastSevenDays, model.LastThirtyDays, model.CurrentWeek, model.CurrentMonth, model.CurrentYear, model.Lifetime })
        {
            AssertEqual(500L, summary.TimedOutputTokens, "longer periods include historical timed output");
            AssertEqual(12_000L, summary.TimedDurationMilliseconds, "longer periods include matching API duration");
        }

        AssertEqual(300L, statistics.LastThirtyDaysDaily[^2].Summary.TimedOutputTokens, "daily speed retains paired output");
        TokenCostSummary invalid = statistics.Models.Single(item => item.Model == "invalid-timing").Today;
        AssertEqual(370L, invalid.TotalTokens, "invalid timing keeps token totals");
        AssertEqual(0m, invalid.CostUsd, "invalid timing keeps unknown-price cost behavior");
        AssertEqual(null, invalid.GetOutputTokensPerSecond(), "invalid timing cannot produce a speed");
        AssertEqual(null, statistics.Models.Single(item => item.Model == "zero-output").Today.GetOutputTokensPerSecond(), "zero output is not a measured speed sample");
        AssertEqual(null, statistics.Models.Single(item => item.Model == "unknown").Today.GetOutputTokensPerSecond(), "output below the measured floor has no speed");
        AssertEqual(null, statistics.Models.Single(item => item.Model == "few-output").Today.GetOutputTokensPerSecond(), "99 output tokens are below the measured floor");
        AssertEqual(null, statistics.Models.Single(item => item.Model == "short-duration").Today.GetOutputTokensPerSecond(), "API duration below 100 ms is not a speed sample");
        AssertEqual(100m, statistics.Models.Single(item => item.Model == "model-a").Today.GetOutputTokensPerSecond(), "model A uses its own API duration");
        AssertEqual(25m, statistics.Models.Single(item => item.Model == "model-b").Today.GetOutputTokensPerSecond(), "model B does not inherit top-level duration");

        TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
        viewModel.UpdateGrokDashboard(new GrokUsageDashboard(null, "N/A", now), statistics);
        AssertTrue(viewModel.GrokTokenCost.DonutSegments.Single(segment => segment.Label == "grok-4.5-build").Tooltip.Contains("Speed: 20.0 tok/s"), "Grok tooltip displays measured speed");
        viewModel.GrokTokenCost.SelectPeriodCommand.Execute(viewModel.GrokTokenCost.Rows[1]);
        AssertTrue(viewModel.GrokTokenCost.DonutSegments.Single(segment => segment.Label == "grok-4.5-build").Tooltip.Contains("Speed: 41.7 tok/s"), "tooltip speed follows selected period");
        viewModel.UpdateTokenCost(new TokenCostStatistics
        {
            Models =
            [
                new TokenCostModelStatistics { Model = "same-high", LastTwentyFourHours = new TokenCostSummary { TotalTokens = 500, TimedOutputTokens = 100, TimedDurationMilliseconds = 1_000 } },
                new TokenCostModelStatistics { Model = "same-low", LastTwentyFourHours = new TokenCostSummary { TotalTokens = 400, TimedOutputTokens = 100, TimedDurationMilliseconds = 9_000 } },
                new TokenCostModelStatistics { Model = "b", LastTwentyFourHours = new TokenCostSummary { TotalTokens = 300 } },
                new TokenCostModelStatistics { Model = "c", LastTwentyFourHours = new TokenCostSummary { TotalTokens = 200 } },
                new TokenCostModelStatistics { Model = "d", LastTwentyFourHours = new TokenCostSummary { TotalTokens = 100, TimedOutputTokens = 100, TimedDurationMilliseconds = 1_000 } },
                new TokenCostModelStatistics { Model = "e", LastTwentyFourHours = new TokenCostSummary { TotalTokens = 80, TimedOutputTokens = 100, TimedDurationMilliseconds = 9_000 } },
                new TokenCostModelStatistics { Model = "f", LastTwentyFourHours = new TokenCostSummary { TotalTokens = 60 } },
            ],
        });
        AssertTrue(viewModel.CodexTokenCost.DonutSegments[0].Tooltip.Contains("Speed: 20.0 tok/s"), "same display label merges raw timing before calculating speed");
        AssertTrue(viewModel.CodexTokenCost.DonutSegments.Single(segment => segment.Label == "Other").Tooltip.Contains("Speed: 20.0 tok/s"), "Other excludes untimed tokens from weighted speed");
        AssertTrue(!viewModel.CodexTokenCost.DonutSegments.Single(segment => segment.Label == "b").Tooltip.Contains("Speed:"), "missing timing omits Speed");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies Codex response windows, both usage/tool orderings, replay, caches, and incomplete timing.
    /// </summary>
    private static Task TestCodexOutputSpeedAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{}");
        string sessions = Path.Combine(temp.Path, "sessions");
        string archived = Path.Combine(temp.Path, "archived_sessions");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archived);
        DateTimeOffset now = new(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset today = now.AddHours(-3);
        DateTimeOffset past = today.AddHours(-25);
        const string parentId = "11111111-1111-1111-1111-111111111111";
        const string childId = "22222222-2222-2222-2222-222222222222";
        string firstUsage = CreateCodexTokenCount(today.AddSeconds(15.001).ToString("O"), last: (100, 20, 300), total: (140, 20, 540));
        string thirdUsage = CreateCodexTokenCount(today.AddSeconds(23.001).ToString("O"), last: (20, 0, 200), total: (210, 20, 980));
        string[] parentLines =
        [
            CreateCodexRecord(past, "event_msg", new { type = "task_started", turn_id = "past" }),
            CreateCodexRecord(past, "turn_context", new { turn_id = "past", model = "gpt-test" }),
            CreateCodexRecord(past.AddSeconds(8), "response_item", new { type = "message", role = "assistant" }),
            CreateCodexTokenCount(past.AddSeconds(8.001).ToString("O"), last: (40, 0, 240), total: (40, 0, 240)),
            CreateCodexRecord(past.AddSeconds(8.002), "event_msg", new { type = "task_complete", turn_id = "past" }),
            CreateCodexRecord(today, "event_msg", new { type = "task_started", turn_id = "first" }),
            CreateCodexRecord(today, "turn_context", new { turn_id = "first", model = "gpt-test" }),
            CreateCodexRecord(today.AddSeconds(1), "response_item", new { type = "message", role = "user" }),
            CreateCodexRecord(today.AddSeconds(3), "response_item", new { type = "reasoning" }),
            CreateCodexRecord(today.AddSeconds(5), "response_item", new { type = "custom_tool_call" }),
            CreateCodexRecord(today.AddSeconds(15), "response_item", new { type = "custom_tool_call_output" }),
            firstUsage,
            firstUsage.Replace(today.AddSeconds(15.001).ToString("O"), today.AddSeconds(16).ToString("O"), StringComparison.Ordinal),
            CreateCodexRecord(today.AddSeconds(19.001), "response_item", new { type = "message", role = "assistant" }),
            CreateCodexTokenCount(today.AddSeconds(19.002).ToString("O"), last: (50, 0, 240), total: (190, 20, 780)),
            CreateCodexRecord(today.AddSeconds(19.003), "event_msg", new { type = "task_complete", turn_id = "first" }),
            CreateCodexRecord(today.AddSeconds(20), "event_msg", new { type = "task_started", turn_id = "second" }),
            CreateCodexRecord(today.AddSeconds(20), "turn_context", new { turn_id = "second", model = "gpt-test" }),
            CreateCodexRecord(today.AddSeconds(21), "event_msg", new { type = "user_message" }),
            CreateCodexRecord(today.AddSeconds(23), "response_item", new { type = "function_call" }),
            thirdUsage,
            CreateCodexRecord(today.AddSeconds(33), "response_item", new { type = "function_call_output" }),
            thirdUsage.Replace(today.AddSeconds(23.001).ToString("O"), today.AddSeconds(35).ToString("O"), StringComparison.Ordinal),
            CreateCodexRecord(today.AddSeconds(37), "response_item", new { type = "message", role = "assistant" }),
            CreateCodexTokenCount(today.AddSeconds(37.001).ToString("O"), last: (30, 0, 360), total: (240, 20, 1340)),
            CreateCodexRecord(today.AddSeconds(37.002), "event_msg", new { type = "task_complete", turn_id = "second" }),
            CreateCodexRecord(today.AddSeconds(40), "event_msg", new { type = "task_complete", turn_id = "idle" }),
        ];
        File.WriteAllLines(Path.Combine(sessions, $"rollout-{parentId}.jsonl"), parentLines);
        File.WriteAllLines(Path.Combine(archived, $"rollout-{parentId}.jsonl"), parentLines);
        DateTimeOffset childStart = today.AddSeconds(40);
        string childPath = Path.Combine(sessions, $"rollout-{childId}.jsonl");
        File.WriteAllLines(childPath,
        [
            CreateCodexRecord(childStart, "session_meta", new { id = childId, forked_from_id = parentId }),
            .. parentLines,
            CreateCodexRecord(childStart, "event_msg", new { type = "task_started", turn_id = "child" }),
            CreateCodexRecord(childStart, "turn_context", new { turn_id = "child", model = "gpt-test" }),
            CreateCodexRecord(childStart.AddSeconds(1), "response_item", new { type = "message", role = "developer" }),
            CreateCodexRecord(childStart.AddSeconds(3), "response_item", new { type = "message", role = "assistant" }),
            CreateCodexTokenCount(childStart.AddSeconds(3.001).ToString("O"), last: (10, 0, 200), total: (250, 20, 1540)),
            CreateCodexRecord(childStart.AddSeconds(3.002), "event_msg", new { type = "task_complete", turn_id = "child" }),
        ]);
        string[] invalidCases = ["legacy", "missing-context", "missing-end", "backwards", "zero", "cumulative", "mismatch", "aborted", "compacted", "malformed"];
        foreach (string invalidCase in invalidCases)
        {
            List<string> lines =
            [
                CreateCodexRecord(today, "event_msg", new { type = "task_started", turn_id = "invalid" }),
                CreateCodexRecord(today, "turn_context", new { turn_id = "invalid", model = "gpt-invalid" }),
                CreateCodexRecord(today.AddSeconds(1), "response_item", new { type = "message", role = "user" }),
                CreateCodexRecord(today.AddSeconds(5), "response_item", new { type = "message", role = "assistant" }),
                CreateCodexTokenCount(today.AddSeconds(6).ToString("O"), last: (10, 0, 100), total: (10, 0, 100)),
            ];
            switch (invalidCase)
            {
                case "legacy": lines.RemoveAt(0); break;
                case "missing-context":
                    lines.RemoveAt(1);
                    lines.Insert(0, CreateCodexRecord(today.AddSeconds(-1), "turn_context", new { turn_id = "previous", model = "gpt-invalid" }));
                    break;
                case "missing-end": lines.RemoveAt(3); break;
                case "backwards": lines[3] = CreateCodexRecord(today.AddSeconds(0.5), "response_item", new { type = "message", role = "assistant" }); break;
                case "zero": lines[3] = CreateCodexRecord(today.AddSeconds(1), "response_item", new { type = "message", role = "assistant" }); break;
                case "cumulative": lines[4] = CreateCodexTokenCount(today.AddSeconds(6).ToString("O"), total: (10, 0, 100)); break;
                case "mismatch": lines.Insert(3, CreateCodexRecord(today.AddSeconds(2), "turn_context", new { turn_id = "other", model = "gpt-invalid" })); break;
                case "aborted": lines.Insert(3, CreateCodexRecord(today.AddSeconds(2), "event_msg", new { type = "turn_aborted", turn_id = "invalid" })); break;
                case "compacted": lines.Insert(3, CreateCodexRecord(today.AddSeconds(2), "compacted", new { })); break;
                case "malformed": lines.Insert(3, "{\"type\":\"response_item\",bad}"); break;
            }

            File.WriteAllLines(Path.Combine(sessions, invalidCase + ".jsonl"), lines);
        }

        TokenCostCollector collector = new(pricingPath);
        TokenCostStatistics statistics = collector.CollectCodex(temp.Path, now);
        TokenCostModelStatistics model = statistics.Models.Single(item => item.Model == "gpt-test");
        AssertTrue(statistics.IsSpeedEstimated, "Codex marks response-window speed as estimated");
        AssertEqual(1510L, model.Today.TotalTokens, "duplicate snapshots and parent replay keep existing token deduplication");
        AssertEqual(1300L, model.Today.TimedOutputTokens, "only accepted live response outputs contribute to speed");
        AssertEqual(16_000L, model.Today.TimedDurationMilliseconds, "tool waits are excluded before and after usage records");
        AssertEqual(81.25m, model.Today.GetOutputTokensPerSecond(), "Codex response windows produce weighted speed");
        AssertEqual(81.25m, model.LastTwentyFourHours.GetOutputTokensPerSecond(), "rolling speed excludes older responses");
        AssertEqual(1540L, model.Lifetime.TimedOutputTokens, "historical responses are measured once");
        AssertEqual(24_000L, model.Lifetime.TimedDurationMilliseconds, "historical windows retain matching duration");
        TokenCostSummary invalid = statistics.Models.Single(item => item.Model == "gpt-invalid").Today;
        AssertEqual(1100L, invalid.TotalTokens, "unmeasurable responses keep their usage");
        AssertEqual(null, invalid.GetOutputTokensPerSecond(), "incomplete or ambiguous response windows have no speed");
        AssertEqual(1300L, collector.CollectCodex(temp.Path, now).Today.TimedOutputTokens, "cached rollouts retain timing");
        AssertEqual(16_000L, collector.CollectCodex(temp.Path, now).Today.TimedDurationMilliseconds, "cached rollouts retain duration");
        AssertEqual(null, collector.CollectCodex(temp.Path, now.AddDays(1)).Today.GetOutputTokensPerSecond(), "cached timing is rebucketed for a new day");
        File.AppendAllLines(childPath,
        [
            CreateCodexRecord(childStart.AddMinutes(1), "event_msg", new { type = "task_started", turn_id = "appended" }),
            CreateCodexRecord(childStart.AddMinutes(1), "turn_context", new { turn_id = "appended", model = "gpt-test" }),
            CreateCodexRecord(childStart.AddMinutes(1).AddSeconds(3), "response_item", new { type = "message", role = "assistant" }),
            CreateCodexTokenCount(childStart.AddMinutes(1).AddSeconds(3.001).ToString("O"), last: (10, 0, 210), total: (260, 20, 1750)),
        ]);
        AssertEqual(1510L, collector.CollectCodex(temp.Path, now).Today.TimedOutputTokens, "appended usage invalidates the timing cache");

        TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
        viewModel.UpdateTokenCost(statistics);
        AssertTrue(viewModel.CodexTokenCost.DonutSegments.Single(segment => segment.Label == "GPT-test").Tooltip.Contains("Speed: ~81.3 tok/s"), "Codex tooltip marks estimated speed");
        viewModel.CodexTokenCost.SelectPeriodCommand.Execute(viewModel.CodexTokenCost.Rows[1]);
        AssertTrue(viewModel.CodexTokenCost.DonutSegments.Single(segment => segment.Label == "GPT-test").Tooltip.Contains("Speed: ~64.2 tok/s"), "estimated speed follows period selection");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds a timestamped Codex rollout record for response-window checks.
    /// </summary>
    private static string CreateCodexRecord(DateTimeOffset timestamp, string type, object payload)
    {
        return JsonSerializer.Serialize(new { timestamp = timestamp.ToString("O"), type, payload });
    }

    /// <summary>
    /// Verifies estimated speed floors, token_usage_record ends, and ignored late-flushed output items.
    /// </summary>
    private static Task TestCodexSpeedWindowEdgesAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{}");
        string sessions = Path.Combine(temp.Path, "sessions");
        Directory.CreateDirectory(sessions);
        DateTimeOffset now = new(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset today = now.AddHours(-3);

        File.WriteAllLines(Path.Combine(sessions, "short.jsonl"), CreateCodexSpeedWindow(today, "gpt-short", output: 200, userSeconds: 0, assistantSeconds: 0.4, usageSeconds: 0.5));
        File.WriteAllLines(Path.Combine(sessions, "few.jsonl"), CreateCodexSpeedWindow(today, "gpt-few", output: 199, userSeconds: 1, assistantSeconds: 5, usageSeconds: 6));
        File.WriteAllLines(Path.Combine(sessions, "hour.jsonl"), CreateCodexSpeedWindow(today, "gpt-hour", output: 200, userSeconds: 0, assistantSeconds: 3601, usageSeconds: 3601.001));

        DateTimeOffset recordStart = today.AddMinutes(10);
        File.WriteAllLines(Path.Combine(sessions, "usage-record.jsonl"),
        [
            CreateCodexRecord(recordStart, "event_msg", new { type = "task_started", turn_id = "record" }),
            CreateCodexRecord(recordStart, "turn_context", new { turn_id = "record", model = "gpt-record" }),
            CreateCodexRecord(recordStart.AddSeconds(1), "response_item", new { type = "message", role = "user" }),
            CreateCodexRecord(recordStart.AddSeconds(3), "response_item", new { type = "message", role = "assistant" }),
            CreateCodexRecord(recordStart.AddSeconds(5), "token_usage_record", new { usage = new { input_tokens = 10, cached_input_tokens = 0, output_tokens = 200 } }),
            CreateCodexTokenCount(recordStart.AddSeconds(5.001).ToString("O"), last: (10, 0, 200), total: (10, 0, 200)),
        ]);

        DateTimeOffset flushStart = today.AddMinutes(20);
        File.WriteAllLines(Path.Combine(sessions, "trailing-flush.jsonl"),
        [
            CreateCodexRecord(flushStart, "event_msg", new { type = "task_started", turn_id = "flush" }),
            CreateCodexRecord(flushStart, "turn_context", new { turn_id = "flush", model = "gpt-flush" }),
            CreateCodexRecord(flushStart.AddSeconds(1), "response_item", new { type = "message", role = "user" }),
            CreateCodexRecord(flushStart.AddSeconds(3), "response_item", new { type = "message", role = "assistant" }),
            CreateCodexTokenCount(flushStart.AddSeconds(3.001).ToString("O"), last: (10, 0, 200), total: (10, 0, 200)),
            CreateCodexRecord(flushStart.AddSeconds(3.05), "response_item", new { type = "message", role = "assistant" }),
            CreateCodexRecord(flushStart.AddSeconds(30), "response_item", new { type = "message", role = "user" }),
            CreateCodexRecord(flushStart.AddSeconds(32), "response_item", new { type = "message", role = "assistant" }),
            CreateCodexTokenCount(flushStart.AddSeconds(32.001).ToString("O"), last: (10, 0, 200), total: (20, 0, 400)),
        ]);

        TokenCostStatistics statistics = new TokenCostCollector(pricingPath).CollectCodex(temp.Path, now);
        AssertEqual(210L, statistics.Models.Single(item => item.Model == "gpt-short").Today.TotalTokens, "short windows still bill tokens");
        AssertEqual(null, statistics.Models.Single(item => item.Model == "gpt-short").Today.GetOutputTokensPerSecond(), "estimated speed ignores sub-second windows");
        AssertEqual(209L, statistics.Models.Single(item => item.Model == "gpt-few").Today.TotalTokens, "small outputs still bill tokens");
        AssertEqual(null, statistics.Models.Single(item => item.Model == "gpt-few").Today.GetOutputTokensPerSecond(), "estimated speed ignores outputs below 200");
        AssertEqual(210L, statistics.Models.Single(item => item.Model == "gpt-hour").Today.TotalTokens, "hour-plus windows still bill tokens");
        AssertEqual(null, statistics.Models.Single(item => item.Model == "gpt-hour").Today.GetOutputTokensPerSecond(), "estimated speed ignores durations over one hour");
        AssertEqual(4000L, statistics.Models.Single(item => item.Model == "gpt-record").Today.TimedDurationMilliseconds, "matching token_usage_record is the response end");
        AssertEqual(50m, statistics.Models.Single(item => item.Model == "gpt-record").Today.GetOutputTokensPerSecond(), "usage-record window keeps estimated speed");
        AssertEqual(4000L, statistics.Models.Single(item => item.Model == "gpt-flush").Today.TimedDurationMilliseconds, "output flushed with token_count does not pin the next start");
        AssertEqual(100m, statistics.Models.Single(item => item.Model == "gpt-flush").Today.GetOutputTokensPerSecond(), "two live windows after a flush keep their own durations");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds one Codex turn with a response window spanning the given offsets.
    /// </summary>
    private static string[] CreateCodexSpeedWindow(DateTimeOffset start, string model, long output, double userSeconds, double assistantSeconds, double usageSeconds)
    {
        return
        [
            CreateCodexRecord(start, "event_msg", new { type = "task_started", turn_id = "edge" }),
            CreateCodexRecord(start, "turn_context", new { turn_id = "edge", model }),
            CreateCodexRecord(start.AddSeconds(userSeconds), "response_item", new { type = "message", role = "user" }),
            CreateCodexRecord(start.AddSeconds(assistantSeconds), "response_item", new { type = "message", role = "assistant" }),
            CreateCodexTokenCount(start.AddSeconds(usageSeconds).ToString("O"), last: (10, 0, output), total: (10, 0, output)),
        ];
    }

    /// <summary>
    /// Verifies dual-segment rollout pages share a high-water mark and unread parent forks are skipped.
    /// </summary>
    private static Task TestCodexForkAndPaginationAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{\"gpt-pages\":{\"input\":2,\"cachedInput\":0.2,\"output\":10},\"gpt-stalled\":{\"input\":2,\"cachedInput\":0.2,\"output\":10}}");
        string sessions = Path.Combine(temp.Path, "sessions", "2026", "07", "11");
        Directory.CreateDirectory(sessions);
        const string threadId = "44444444-4444-4444-4444-444444444444";
        const string pageOneId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string pageTwoId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        File.WriteAllLines(Path.Combine(sessions, $"rollout-2026-07-11T09-00-00-{threadId}_{pageOneId}.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-pages\"}}",
            CreateCodexTokenCount("2026-07-11T09:00:00+08:00", last: (1000, 400, 100), total: (1000, 400, 100)),
        ]);
        File.WriteAllLines(Path.Combine(sessions, $"rollout-2026-07-11T10-00-00-{threadId}_{pageTwoId}.jsonl"),
        [
            CreateCodexTokenCount("2026-07-11T10:00:00+08:00", total: (2000, 800, 200)),
        ]);

        const string parentId = "55555555-5555-5555-5555-555555555555";
        const string stalledId = "66666666-6666-6666-6666-666666666666";
        File.WriteAllLines(Path.Combine(sessions, $"rollout-{parentId}.jsonl"),
        [
            $"{{\"timestamp\":\"2026-07-11T09:00:00+08:00\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{parentId}\"}}}}",
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-stalled\"}}",
            CreateCodexTokenCount("2026-07-11T10:00:00+08:00", last: (10, 0, 10), total: (10, 0, 10)),
        ]);
        File.WriteAllLines(Path.Combine(sessions, $"rollout-{stalledId}.jsonl"),
        [
            $"{{\"timestamp\":\"2026-07-11T11:00:00+08:00\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{stalledId}\",\"forked_from_id\":\"{parentId}\"}}}}",
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-stalled\"}}",
            CreateCodexTokenCount("2026-07-11T11:05:00+08:00", last: (5000, 0, 500), total: (5010, 0, 510)),
        ]);

        TokenCostStatistics statistics = new TokenCostCollector(pricingPath)
            .CollectCodex(temp.Path, new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8)));
        AssertEqual(2200L, statistics.Models.Single(item => item.Model == "gpt-pages").Today.TotalTokens, "stitched pages continue the cumulative high-water");
        AssertEqual(0.00456m, statistics.Models.Single(item => item.Model == "gpt-pages").Today.CostUsd, "stitched page cost");
        AssertEqual(20L, statistics.Models.Single(item => item.Model == "gpt-stalled").Today.TotalTokens, "child is skipped until the parent log reaches the fork time");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies cumulative token deltas and cached input pricing.
    /// </summary>
    private static Task TestCodexTokenCostCollectorAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{\"gpt-test\":{\"input\":2,\"cachedInput\":0.2,\"output\":10}}");
        string sessions = Path.Combine(temp.Path, "sessions", "2026", "07", "11");
        Directory.CreateDirectory(sessions);
        string sessionPath = Path.Combine(sessions, "rollout.jsonl");
        File.WriteAllLines(sessionPath,
        [
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-1\",\"source\":\"cli\"}}",
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2026-07-11T09:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":400,\"output_tokens\":100}}}}",
            "{\"timestamp\":\"2026-07-11T10:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":2500,\"cached_input_tokens\":1000,\"output_tokens\":300}}}}",
        ]);
        File.WriteAllLines(Path.Combine(sessions, "yesterday.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2026-07-10T13:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":500,\"cached_input_tokens\":100,\"output_tokens\":50}}}}",
        ]);
        File.WriteAllLines(Path.Combine(sessions, "unknown-model.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"unknown-model\"}}",
            "{\"timestamp\":\"2026-07-11T11:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":0,\"output_tokens\":0}}}}",
        ]);
        File.WriteAllLines(Path.Combine(sessions, "last-sunday.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2026-07-05T10:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":70,\"cached_input_tokens\":0,\"output_tokens\":0}}}}",
        ]);
        File.WriteAllLines(Path.Combine(sessions, "last-month.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2026-06-30T10:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":40,\"cached_input_tokens\":0,\"output_tokens\":0}}}}",
        ]);
        File.WriteAllLines(Path.Combine(sessions, "older.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2026-05-01T10:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":0,\"output_tokens\":0}}}}",
        ]);
        File.WriteAllLines(Path.Combine(sessions, "new-year.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2026-01-01T00:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":25,\"cached_input_tokens\":0,\"output_tokens\":0}}}}",
        ]);
        File.WriteAllLines(Path.Combine(sessions, "previous-year.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2025-12-31T23:59:59+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":80,\"cached_input_tokens\":0,\"output_tokens\":0}}}}",
        ]);

        using FileStream activeWriter = new(sessionPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        TokenCostCollector collector = new(pricingPath);
        File.WriteAllLines(Path.Combine(sessions, "future.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2026-07-12T10:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":10000,\"cached_input_tokens\":0,\"output_tokens\":0}}}}",
        ]);

        TokenCostSummary summary = collector.CollectCodex(temp.Path, new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8))).Today;
        AssertEqual(2900L, summary.TotalTokens, "today total tokens");
        AssertEqual(0.0062m, summary.CostUsd, "today API-equivalent cost");
        AssertEqual(1000L, summary.CacheReadTokens, "today cache-read tokens");
        AssertEqual(2600L, summary.CacheableInputTokens, "today cacheable input tokens");
        AssertEqual(38, summary.GetCacheHitPercent(), "today cache hit percent");
        TokenCostStatistics statistics = collector.CollectCodex(temp.Path, new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8)));
        AssertEqual(3450L, statistics.LastTwentyFourHours.TotalTokens, "last 24 hours total tokens");
        AssertEqual(3520L, statistics.LastSevenDays.TotalTokens, "last 7 days total tokens");
        AssertEqual(3560L, statistics.LastThirtyDays.TotalTokens, "last 30 days total tokens");
        AssertEqual(0.00774m, statistics.LastThirtyDays.CostUsd, "last 30 days API-equivalent cost");
        AssertEqual(3685L, statistics.CurrentYear.TotalTokens, "current-year tokens include January 1 and exclude the previous local year");
        AssertEqual(0.00799m, statistics.CurrentYear.CostUsd, "current-year API-equivalent cost");
        AssertEqual(3765L, statistics.Lifetime.TotalTokens, "lifetime tokens");
        AssertEqual(0.00815m, statistics.Lifetime.CostUsd, "lifetime API-equivalent cost");
        AssertEqual(30, statistics.LastThirtyDaysDaily.Count, "30-day chart slot count");
        AssertEqual(new DateTime(2026, 6, 12), statistics.LastThirtyDaysDaily[0].Date, "30-day chart first date");
        AssertEqual(40L, statistics.LastThirtyDaysDaily[18].Summary.TotalTokens, "30-day chart June token count");
        AssertEqual(11, statistics.CurrentMonthDaily.Count, "current-month chart slot count");
        AssertEqual(new DateTime(2026, 7, 1), statistics.CurrentMonthDaily[0].Date, "current-month chart first date");
        AssertEqual(70L, statistics.CurrentMonthDaily[4].Summary.TotalTokens, "current-month chart July token count");
        AssertEqual(3450L, statistics.CurrentWeek.TotalTokens, "current-week total tokens");
        AssertEqual(3520L, statistics.CurrentMonth.TotalTokens, "current-month total tokens");
        AssertEqual(7, statistics.LastSevenDaysDaily.Count, "daily chart slot count");
        AssertEqual(new DateTime(2026, 7, 5), statistics.LastSevenDaysDaily[0].Date, "daily chart first date");
        AssertEqual(70L, statistics.LastSevenDaysDaily[0].Summary.TotalTokens, "daily chart first tokens");
        AssertEqual(550L, statistics.LastSevenDaysDaily[5].Summary.TotalTokens, "daily chart yesterday tokens");
        AssertEqual(new DateTime(2026, 7, 11), statistics.LastSevenDaysDaily[6].Date, "daily chart today date");
        AssertEqual(2900L, statistics.LastSevenDaysDaily[6].Summary.TotalTokens, "daily chart today tokens");
        AssertEqual("gpt-test|unknown-model", string.Join('|', statistics.Models.Select(model => model.Model)), "model statistics names");
        AssertEqual(3350L, statistics.Models[0].LastTwentyFourHours.TotalTokens, "priced model last 24 hours tokens");
        AssertEqual(2800L, statistics.Models[0].Today.TotalTokens, "priced model today tokens");
        AssertEqual(3350L, statistics.Models[0].CurrentWeek.TotalTokens, "priced model current-week tokens");
        AssertEqual(3420L, statistics.Models[0].CurrentMonth.TotalTokens, "priced model current-month tokens");
        AssertEqual(3585L, statistics.Models[0].CurrentYear.TotalTokens, "priced model current-year tokens");
        AssertEqual(3665L, statistics.Models[0].Lifetime.TotalTokens, "priced model lifetime tokens include the previous local year");
        AssertEqual(100L, statistics.Models[1].Today.TotalTokens, "unknown model today tokens");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies Codex last_token_usage wins over non-monotonic and interleaved totals.
    /// </summary>
    private static Task TestCodexLastTokenUsageAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{\"gpt-test\":{\"input\":2,\"cachedInput\":0.2,\"output\":10}}");
        DateTimeOffset now = new(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8));

        TokenCostSummary interleaved = CollectCodexSession(
            temp.Path,
            pricingPath,
            now,
            "interleaved.jsonl",
            [
                CreateCodexTokenCount("2026-07-11T09:00:00+08:00", last: (151_258, 10_000, 500), total: (76_780_408, 70_000_000, 1_000_000)),
                CreateCodexTokenCount("2026-07-11T10:00:00+08:00", last: (200_000, 20_000, 800), total: (87_709_262, 80_000_000, 2_000_000)),
            ]);
        AssertEqual(352_558L, interleaved.TotalTokens, "interleaved last usage tokens");
        AssertEqual(0.661516m, interleaved.CostUsd, "interleaved last usage cost");

        TokenCostSummary regression = CollectCodexSession(
            temp.Path,
            pricingPath,
            now,
            "regression.jsonl",
            [
                CreateCodexTokenCount("2026-07-11T09:00:00+08:00", total: (1_000, 400, 100)),
                CreateCodexTokenCount("2026-07-11T10:00:00+08:00", total: (500, 200, 50)),
                CreateCodexTokenCount("2026-07-11T11:00:00+08:00", total: (1_200, 500, 150)),
            ]);
        AssertEqual(1_350L, regression.TotalTokens, "non-monotonic total high-water tokens");
        AssertEqual(0.003m, regression.CostUsd, "non-monotonic total high-water cost");

        TokenCostSummary preferred = CollectCodexSession(
            temp.Path,
            pricingPath,
            now,
            "last-preferred.jsonl",
            [
                CreateCodexTokenCount("2026-07-11T09:00:00+08:00", last: (100, 20, 10), total: (10_000, 8_000, 500)),
                CreateCodexTokenCount("2026-07-11T10:00:00+08:00", total: (10_200, 8_040, 520), emptyLast: true),
            ]);
        AssertEqual(330L, preferred.TotalTokens, "empty last object falls back to total high-water");
        AssertEqual(0.000792m, preferred.CostUsd, "empty last object fallback cost");

        TokenCostSummary replayed = CollectCodexSession(
            temp.Path,
            pricingPath,
            now,
            "snapshot-replay.jsonl",
            [
                CreateCodexTokenCount("2026-07-11T09:00:00+08:00", last: (100, 20, 10), total: (1_000, 400, 100)),
                CreateCodexTokenCount("2026-07-11T09:00:01+08:00", last: (100, 20, 10), total: (1_000, 400, 100)),
                CreateCodexTokenCount("2026-07-11T10:00:00+08:00", last: (80, 10, 8), total: (1_080, 410, 108)),
            ]);
        AssertEqual(198L, replayed.TotalTokens, "unchanged snapshot is not billed twice");
        AssertEqual(0.000486m, replayed.CostUsd, "unchanged snapshot cost");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies an archived Codex rollout does not double-count an active copy.
    /// </summary>
    private static Task TestCodexArchivedRolloutDedupAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{\"gpt-test\":{\"input\":2,\"cachedInput\":0.2,\"output\":10}}");
        const string threadId = "11111111-1111-1111-1111-111111111111";
        string sessions = Path.Combine(temp.Path, "sessions", "2026", "07", "11");
        string archived = Path.Combine(temp.Path, "archived_sessions");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archived);
        File.WriteAllLines(Path.Combine(sessions, $"rollout-{threadId}.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            CreateCodexTokenCount("2026-07-11T09:00:00+08:00", last: (100, 0, 10)),
        ]);
        File.WriteAllLines(Path.Combine(archived, $"rollout-{threadId}.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            CreateCodexTokenCount("2026-07-11T09:00:00+08:00", last: (9_999, 0, 999)),
        ]);

        TokenCostSummary summary = new TokenCostCollector(pricingPath)
            .CollectCodex(temp.Path, new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8)))
            .Today;
        AssertEqual(110L, summary.TotalTokens, "archived duplicate rollout is skipped");
        AssertEqual(0.0003m, summary.CostUsd, "archived duplicate rollout cost");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies zstd session logs keep older-date totals, prefer a plain sibling, and skip a damaged frame.
    /// </summary>
    private static Task TestCodexCompressedHistoryAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{\"gpt-test\":{\"input\":2,\"cachedInput\":0.2,\"output\":10}}");
        DateTimeOffset now = new(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8));
        const string olderId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string todayId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        const string bothId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
        const string threadId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
        const string pageOneId = "ffffffff-ffff-ffff-ffff-ffffffffffff";
        const string pageTwoId = "12121212-1212-1212-1212-121212121212";
        string olderDir = Path.Combine(temp.Path, "sessions", "2026", "05", "01");
        string todayDir = Path.Combine(temp.Path, "sessions", "2026", "07", "11");
        string archived = Path.Combine(temp.Path, "archived_sessions");
        Directory.CreateDirectory(olderDir);
        Directory.CreateDirectory(todayDir);
        Directory.CreateDirectory(archived);
        WriteCompressedCodexLog(Path.Combine(olderDir, $"rollout-{olderId}.jsonl.zst"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            CreateCodexTokenCount("2026-05-01T10:00:00+08:00", last: (100, 0, 10)),
        ]);
        WriteCompressedCodexLog(Path.Combine(olderDir, $"rollout-2026-05-01T09-00-00-{threadId}_{pageOneId}.jsonl.zst"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            CreateCodexTokenCount("2026-05-01T09:00:00+08:00", total: (1000, 0, 100)),
        ]);
        WriteCompressedCodexLog(Path.Combine(olderDir, $"rollout-2026-05-01T10-00-00-{threadId}_{pageTwoId}.jsonl.zst"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            CreateCodexTokenCount("2026-05-01T11:00:00+08:00", total: (2000, 0, 200)),
        ]);
        File.WriteAllLines(Path.Combine(todayDir, $"rollout-{todayId}.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            CreateCodexTokenCount("2026-07-11T09:00:00+08:00", last: (200, 0, 20)),
        ]);
        File.WriteAllLines(Path.Combine(todayDir, $"rollout-{bothId}.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            CreateCodexTokenCount("2026-07-11T10:00:00+08:00", last: (30, 0, 3)),
        ]);
        WriteCompressedCodexLog(Path.Combine(todayDir, $"rollout-{bothId}.jsonl.zst"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            CreateCodexTokenCount("2026-07-11T10:00:00+08:00", last: (9000, 0, 900)),
        ]);
        WriteCompressedCodexLog(Path.Combine(archived, $"rollout-{todayId}.jsonl.zst"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            CreateCodexTokenCount("2026-07-11T09:00:00+08:00", last: (8000, 0, 800)),
        ]);
        WriteCompressedCodexLog(Path.Combine(archived, "rollout-abababab-abab-abab-abab-abababababab.jsonl.zst"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            CreateCodexTokenCount("2026-06-01T10:00:00+08:00", last: (40, 0, 4)),
        ]);
        File.WriteAllBytes(Path.Combine(olderDir, "rollout-dddddddd-dddd-dddd-dddd-dddddddddddd.jsonl.zst"), [1, 2, 3, 4, 5]);

        TokenCostStatistics statistics = new TokenCostCollector(pricingPath).CollectCodex(temp.Path, now);
        AssertEqual(253L, statistics.Today.TotalTokens, "plain sibling and archived zstd twin are not double-counted");
        AssertEqual(2607L, statistics.Lifetime.TotalTokens, "older zstd rollouts, stitched pages, and archived-only logs are included");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes one Codex JSONL log as a zstd frame, matching local history compression.
    /// </summary>
    private static void WriteCompressedCodexLog(string path, IReadOnlyList<string> lines)
    {
        using FileStream stream = File.Create(path);
        using CompressionStream compression = new(stream, 3);
        using StreamWriter writer = new(compression, leaveOpen: true);
        foreach (string line in lines)
        {
            writer.WriteLine(line);
        }
    }

    /// <summary>
    /// Collects one isolated Codex session file under a unique directory.
    /// </summary>
    private static TokenCostSummary CollectCodexSession(
        string root,
        string pricingPath,
        DateTimeOffset now,
        string fileName,
        string[] lines,
        string model = "gpt-test")
    {
        string collectRoot = Path.Combine(root, Path.GetFileNameWithoutExtension(fileName));
        string sessions = Path.Combine(collectRoot, "sessions", "2026", "07", "11");
        Directory.CreateDirectory(sessions);
        File.WriteAllLines(Path.Combine(sessions, fileName), [$"{{\"type\":\"turn_context\",\"payload\":{{\"model\":\"{model}\"}}}}", .. lines]);
        return new TokenCostCollector(pricingPath).CollectCodex(collectRoot, now).Today;
    }

    /// <summary>
    /// Builds one Codex token_count JSONL line with optional last and total usage.
    /// </summary>
    private static string CreateCodexTokenCount(
        string timestamp,
        (long Input, long Cached, long Output)? last = null,
        (long Input, long Cached, long Output)? total = null,
        bool emptyLast = false)
    {
        string lastJson = emptyLast
            ? "\"last_token_usage\":{}"
            : last is { } lastUsage
                ? $"\"last_token_usage\":{{\"input_tokens\":{lastUsage.Input},\"cached_input_tokens\":{lastUsage.Cached},\"output_tokens\":{lastUsage.Output}}}"
                : string.Empty;
        string totalJson = total is { } totalUsage
            ? $"\"total_token_usage\":{{\"input_tokens\":{totalUsage.Input},\"cached_input_tokens\":{totalUsage.Cached},\"output_tokens\":{totalUsage.Output}}}"
            : string.Empty;
        string info = string.Join(',', new[] { lastJson, totalJson }.Where(part => part.Length > 0));
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{{info}}}}}}}";
    }
    private static Task TestModelAliasPricingAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["grok-4.5"] = new
            {
                input = 2.0m,
                cachedInput = 0.3m,
                output = 6.0m,
                aliases = new[] { "grok-4.5-build" },
            },
        }));
        string sessions = Path.Combine(temp.Path, "sessions", "2026", "08", "12");
        Directory.CreateDirectory(sessions);
        File.WriteAllLines(Path.Combine(sessions, "alias.jsonl"),
        [
            JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "grok-4.5-build" } }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-12T09:00:00+08:00",
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        total_token_usage = new { input_tokens = 1_000, cached_input_tokens = 100, output_tokens = 50 },
                    },
                },
            }),
        ]);

        TokenCostSummary summary = new TokenCostCollector(pricingPath)
            .CollectCodex(temp.Path, new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(8)))
            .Today;
        AssertEqual(1_050L, summary.TotalTokens, "alias pricing total tokens");
        AssertEqual(0.00213m, summary.CostUsd, "alias pricing cost");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies configured pricing periods select rates by UTC weekday hours, including an all-day period.
    /// </summary>
    private static Task TestDeepSeekPeakPricingAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        object[] flashPeriods =
        [
            new { input = 0.3m, cachedInput = 0.006m, output = 1.2m, daysUtc = new[] { 1, 2, 3, 4, 5 }, startUtc = "01:00", endUtc = "04:00" },
            new { input = 0.3m, cachedInput = 0.006m, output = 1.2m, daysUtc = new[] { 1, 2, 3, 4, 5 }, startUtc = "06:00", endUtc = "10:00" },
        ];
        File.WriteAllText(pricingPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["deepseek-v4-flash"] = new
            {
                input = 0.15m,
                cachedInput = 0.003m,
                output = 0.6m,
                periods = flashPeriods,
                aliases = new[] { "deepseek-flash" },
            },
            ["gpt-test"] = new { input = 2m, cachedInput = 0.2m, output = 10m },
            ["incomplete-peak"] = new
            {
                input = 0.15m,
                cachedInput = 0.003m,
                output = 0.6m,
                periods = new object[] { new { input = 0.3m } },
            },
            ["all-day-period"] = new
            {
                input = 0.15m,
                cachedInput = 0.003m,
                output = 0.6m,
                periods = new object[] { new { input = 0.3m, cachedInput = 0.006m, output = 1.2m } },
            },
        }));
        DateTimeOffset weekdayNow = new(2026, 8, 12, 20, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset weekendNow = new(2026, 8, 15, 20, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset weekdayPeak = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));

        TokenCostSummary morningPeak = CollectCodexSession(
            temp.Path,
            pricingPath,
            weekdayNow,
            "peak-morning.jsonl",
            [CreateCodexTokenCount("2026-08-12T09:00:00+08:00", last: (1_000_000, 0, 0))],
            "deepseek-flash");
        AssertEqual(0.3m, morningPeak.CostUsd, "weekday 01:00 UTC uses peak price");

        TokenCostSummary noonOffPeak = CollectCodexSession(
            temp.Path,
            pricingPath,
            weekdayNow,
            "off-noon.jsonl",
            [CreateCodexTokenCount("2026-08-12T12:00:00+08:00", last: (1_000_000, 0, 0))],
            "deepseek-v4-flash");
        AssertEqual(0.15m, noonOffPeak.CostUsd, "weekday 04:00 UTC uses off-peak price");

        TokenCostSummary afternoonPeak = CollectCodexSession(
            temp.Path,
            pricingPath,
            weekdayNow,
            "peak-afternoon.jsonl",
            [CreateCodexTokenCount("2026-08-12T14:00:00+08:00", last: (1_000_000, 500_000, 100_000))],
            "deepseek-v4-flash");
        AssertEqual(0.273m, afternoonPeak.CostUsd, "weekday 06:00 UTC uses peak input, cache, and output");

        TokenCostSummary eveningOffPeak = CollectCodexSession(
            temp.Path,
            pricingPath,
            weekdayNow,
            "off-evening.jsonl",
            [CreateCodexTokenCount("2026-08-12T18:00:00+08:00", last: (1_000_000, 0, 0))],
            "deepseek-v4-flash");
        AssertEqual(0.15m, eveningOffPeak.CostUsd, "weekday 10:00 UTC uses off-peak price");

        TokenCostSummary weekendOffPeak = CollectCodexSession(
            temp.Path,
            pricingPath,
            weekendNow,
            "off-weekend.jsonl",
            [CreateCodexTokenCount("2026-08-15T09:00:00+08:00", last: (1_000_000, 0, 0))],
            "deepseek-v4-flash");
        AssertEqual(0.15m, weekendOffPeak.CostUsd, "Saturday peak clock hours stay off-peak");

        TokenCostSummary flatModel = CollectCodexSession(
            temp.Path,
            pricingPath,
            weekdayNow,
            "flat-model.jsonl",
            [CreateCodexTokenCount("2026-08-12T09:00:00+08:00", last: (1_000_000, 0, 0))]);
        AssertEqual(2m, flatModel.CostUsd, "models without peak keep a flat rate in peak hours");

        TokenCostSummary incompletePeak = CollectCodexSession(
            temp.Path,
            pricingPath,
            weekdayNow,
            "incomplete-peak.jsonl",
            [CreateCodexTokenCount("2026-08-12T09:00:00+08:00", last: (1_000_000, 0, 0))],
            "incomplete-peak");
        AssertEqual(0.15m, incompletePeak.CostUsd, "incomplete period keeps the default rate");

        TokenCostSummary allDayPeriod = CollectCodexSession(
            temp.Path,
            pricingPath,
            weekendNow,
            "all-day-period.jsonl",
            [CreateCodexTokenCount("2026-08-15T09:00:00+08:00", last: (1_000_000, 0, 0))],
            "all-day-period");
        AssertEqual(0.3m, allDayPeriod.CostUsd, "a single period without a window applies all day");

        string grokSession = Path.Combine(temp.Path, "grok", "sessions", "workspace", "peak");
        Directory.CreateDirectory(grokSession);
        File.WriteAllLines(Path.Combine(grokSession, "updates.jsonl"),
        [
            CreateGrokTokenUpdate("prompt-fallback-peak", weekdayPeak, 1_000_000, 0, 0)
                .Replace("grok-4.5-build", "deepseek-flash", StringComparison.Ordinal),
            CreateGrokTokenUpdate("prompt-ticks-peak", weekdayPeak.AddMinutes(1), 1_000_000, 0, 0, 50_000_000)
                .Replace("grok-4.5-build", "deepseek-v4-flash", StringComparison.Ordinal),
        ]);
        TokenCostStatistics grok = new TokenCostCollector(pricingPath).CollectGrok(Path.Combine(temp.Path, "grok"), weekdayNow);
        AssertEqual(0.3m, grok.Models.Single(model => model.Model == "deepseek-flash").Today.CostUsd, "Grok fallback uses peak price");
        AssertEqual(0.005m, grok.Models.Single(model => model.Model == "deepseek-v4-flash").Today.CostUsd, "Grok complete ticks ignore local peak price");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies CCSwitch-compatible Grok turn costs, fallback pricing, deduplication, and calendar periods.
    /// </summary>
    private static Task TestGrokTokenCostCollectorAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["grok-4.5-build"] = new { input = 2m, cachedInput = 0.3m, output = 6m },
            ["grok-4.6-build"] = new { input = 2m, cachedInput = 0.5m, output = 6m },
            ["deepseek-v4-flash"] = new { input = 0.14m, cachedInput = 0.0028m, output = 0.28m },
            ["deepseek-v4-pro"] = new { input = 0.435m, cachedInput = 0.003625m, output = 0.87m },
        }));
        string activeSession = Path.Combine(temp.Path, "sessions", "workspace", "session-a");
        string archivedDuplicate = Path.Combine(temp.Path, "archived_sessions", "workspace", "session-a");
        string archivedSession = Path.Combine(temp.Path, "archived_sessions", "workspace", "session-b");
        Directory.CreateDirectory(activeSession);
        Directory.CreateDirectory(archivedDuplicate);
        Directory.CreateDirectory(archivedSession);
        DateTimeOffset now = new(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset today = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset yesterday = today.AddDays(-1);
        string exactUsage = CreateGrokTokenUpdate("prompt-exact", today, 16_632, 0, 104, 388_880_000, reasoningTokens: 80);
        File.WriteAllLines(Path.Combine(activeSession, "updates.jsonl"),
        [
            exactUsage,
            CreateGrokTokenUpdate("prompt-partial", today.AddMinutes(1), 13_793, 13_696, 21, 10_000_000, costIsPartial: true),
            CreateGrokTokenUpdate("prompt-aggregate", today.AddMinutes(2), 589_412, 493_184, 10_240),
            CreateGrokTokenUpdate("prompt-deepseek", today.AddMinutes(3), 1_000_000, 500_000, 100_000)
                .Replace("grok-4.5-build", "deepseek-v4-pro", StringComparison.Ordinal),
            CreateGrokTokenUpdate("prompt-deepseek-flash", today.AddMinutes(4), 1_000_000, 500_000, 100_000)
                .Replace("grok-4.5-build", "deepseek-v4-flash", StringComparison.Ordinal),
            CreateGrokTokenUpdate("prompt-grok46", today.AddMinutes(5), 1_000, 0, 100)
                .Replace("grok-4.5-build", "grok-4.6-build", StringComparison.Ordinal),
            CreateGrokTokenUpdate("prompt-future", today.AddDays(1), 1_000, 0, 100, 50_000_000),
            CreateGrokTokenUpdate("prompt-snapshot", today, 9_999, 0, 9, sessionUpdate: "usage_snapshot"),
            CreateGrokTokenUpdate("prompt-empty-kind", today, 9_999, 0, 9, sessionUpdate: ""),
            "{\"method\":\"session/update\",\"params\":{\"_meta\":{\"totalTokens\":999999,\"promptId\":\"legacy\"}}}",
            "{malformed",
        ]);
        File.WriteAllLines(Path.Combine(archivedDuplicate, "updates.jsonl"), [exactUsage]);
        string archivedPath = Path.Combine(archivedSession, "updates.jsonl");
        File.WriteAllLines(archivedPath,
        [
            CreateGrokTokenUpdate("prompt-archived", yesterday, 100, 50, 10),
            CreateGrokTopLevelTokenUpdate("prompt-top-level", yesterday.AddMinutes(1), 50, 20, 10, 10_000_000),
        ]);

        TokenCostStatistics statistics;
        using (FileStream activeWriter = new(archivedPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            statistics = new TokenCostCollector(pricingPath).CollectGrok(temp.Path, now);
        }

        AssertEqual(2_831_302L, statistics.Today.TotalTokens, "Grok today tokens");
        AssertEqual(1_506_880L, statistics.Today.CacheReadTokens, "Grok today cache-read tokens");
        AssertEqual(2_620_837L, statistics.Today.CacheableInputTokens, "Grok today cacheable input tokens");
        AssertEqual(57, statistics.Today.GetCacheHitPercent(), "Grok today cache hit percent");
        AssertEqual(2_831_472L, statistics.LastSevenDays.TotalTokens, "Grok last 7 days tokens");
        AssertEqual(2_831_472L, statistics.LastThirtyDays.TotalTokens, "Grok last 30 days tokens");
        AssertEqual(2_831_472L, statistics.Lifetime.TotalTokens, "Grok lifetime tokens");
        AssertEqual(0.8534805m, statistics.Today.CostUsd, "Grok reported, partial, aggregate, and alternate-model fallback cost");
        AssertEqual(0.8546555m, statistics.Lifetime.CostUsd, "Grok lifetime API-equivalent cost");
        AssertEqual(170L, statistics.LastSevenDaysDaily[5].Summary.TotalTokens, "Grok yesterday tokens");
        AssertEqual(2_831_302L, statistics.LastSevenDaysDaily[6].Summary.TotalTokens, "Grok daily today tokens");
        AssertEqual(
            "deepseek-v4-flash|deepseek-v4-pro|grok-4.5-build|grok-4.6-build|unknown",
            string.Join('|', statistics.Models.Select(model => model.Model)),
            "Grok model statistics names");
        AssertEqual(1_100_000L, statistics.Models[0].Today.TotalTokens, "Grok deepseek-v4-flash today tokens");
        AssertEqual(630_202L, statistics.Models[2].Today.TotalTokens, "Grok grok-4.5-build today tokens");
        AssertEqual(60L, statistics.Models[4].Lifetime.TotalTokens, "Grok unknown model lifetime tokens");

        File.AppendAllLines(archivedPath, [CreateGrokTokenUpdate("prompt-unpriced", yesterday, 10, 0, 1).Replace("grok-4.5-build", "future-grok-model", StringComparison.Ordinal)]);
        TokenCostStatistics unpricedStatistics = new TokenCostCollector(pricingPath).CollectGrok(temp.Path, now);
        AssertEqual(2_831_483L, unpricedStatistics.Lifetime.TotalTokens, "unpriced Grok tokens still count");
        AssertEqual(0.8546555m, unpricedStatistics.Lifetime.CostUsd, "unpriced Grok usage keeps priced cost at $0.00 increment");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies Grok model-name cleanup, ticks-only turns, and unpriced models as $0.00.
    /// </summary>
    private static Task TestGrokBillingEdgesAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{\"grok-4.5-build\":{\"input\":2,\"cachedInput\":0.3,\"output\":6}}");
        string session = Path.Combine(temp.Path, "sessions", "workspace", "session-edges");
        Directory.CreateDirectory(session);
        DateTimeOffset now = new(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset today = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(8));
        File.WriteAllLines(Path.Combine(session, "updates.jsonl"),
        [
            CreateGrokTokenUpdate("prompt-prefix", today, 1_000, 0, 100).Replace("grok-4.5-build", "xai/grok-4.5-build", StringComparison.Ordinal),
            CreateGrokTokenUpdate("prompt-date", today.AddMinutes(1), 500, 0, 50).Replace("grok-4.5-build", "grok-4.5-build-2026-08-01", StringComparison.Ordinal),
            CreateGrokTokenUpdate("prompt-tools", today.AddMinutes(2), 0, 0, 0, 100_000_000),
            CreateGrokTokenUpdate("prompt-unknown", today.AddMinutes(3), 10, 0, 1).Replace("grok-4.5-build", "future-grok-model", StringComparison.Ordinal),
        ]);

        TokenCostStatistics statistics = new TokenCostCollector(pricingPath).CollectGrok(temp.Path, now);
        AssertEqual(1_661L, statistics.Today.TotalTokens, "normalized and unpriced Grok tokens");
        AssertEqual(0.0139m, statistics.Today.CostUsd, "Grok prefix, snapshot, and ticks-only cost");
        AssertEqual("future-grok-model|grok-4.5-build", string.Join('|', statistics.Models.Select(model => model.Model)), "normalized Grok model names");
        AssertEqual(0m, statistics.Models[0].Today.CostUsd, "unknown Grok model costs $0.00");
        AssertEqual(0.0139m, statistics.Models[1].Today.CostUsd, "normalized Grok model keeps priced and ticks cost");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies Codex token cost ignores an adjacent OpenCode database.
    /// </summary>
    private static Task TestOpenCodeTokenCostIgnoredAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{\"gpt-test\":{\"input\":2,\"cachedInput\":0.2,\"output\":10}}");
        string codexRoot = Path.Combine(temp.Path, "codex");
        string sessions = Path.Combine(codexRoot, "sessions", "2026", "07", "11");
        string openCodeRoot = Path.Combine(codexRoot, ".local", "share", "opencode");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(openCodeRoot);
        File.WriteAllLines(Path.Combine(sessions, "rollout.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2026-07-11T09:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":400,\"output_tokens\":100}}}}",
        ]);
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(openCodeRoot, "opencode.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE message (time_created INTEGER NOT NULL, data TEXT NOT NULL);";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO message VALUES ($time, $data);";
            command.Parameters.AddWithValue("$time", new DateTimeOffset(2026, 7, 11, 9, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$data", "{\"role\":\"assistant\",\"providerID\":\"openai\",\"modelID\":\"gpt-test\",\"tokens\":{\"input\":600,\"output\":100,\"reasoning\":50,\"cache\":{\"read\":400,\"write\":0}}}");
            command.ExecuteNonQuery();
        }

        TokenCostSummary summary = new TokenCostCollector(pricingPath)
            .CollectCodex(codexRoot, new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8)))
            .Today;
        AssertEqual(1_100L, summary.TotalTokens, "Codex tokens exclude OpenCode");
        AssertEqual(0.00228m, summary.CostUsd, "Codex cost excludes OpenCode");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that only token events matching an explicit parent rollout are treated as replay history.
    /// </summary>
    private static Task TestSubagentTokenCostAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{\"gpt-test\":{\"input\":2,\"cachedInput\":0.2,\"output\":10}}");
        string sessions = Path.Combine(temp.Path, "sessions", "2026", "07", "11");
        Directory.CreateDirectory(sessions);
        const string parentId = "11111111-1111-1111-1111-111111111111";
        const string legacyChildId = "22222222-2222-2222-2222-222222222222";
        const string spawnedChildId = "33333333-3333-3333-3333-333333333333";
        File.WriteAllLines(Path.Combine(sessions, $"rollout-{parentId}.jsonl"),
        [
            $"{{\"timestamp\":\"2026-07-11T08:00:00+08:00\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{parentId}\",\"source\":\"cli\"}}}}",
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2026-07-11T09:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":400,\"output_tokens\":100}}}}",
            "{\"timestamp\":\"2026-07-11T10:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":2000,\"cached_input_tokens\":800,\"output_tokens\":200}}}}",
            "{\"timestamp\":\"2026-07-11T10:30:00+08:00\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
        ]);
        File.WriteAllLines(Path.Combine(sessions, $"rollout-{legacyChildId}.jsonl"),
        [
            $"{{\"timestamp\":\"2026-07-11T10:15:00+08:00\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{legacyChildId}\",\"source\":{{\"subagent\":{{\"other\":\"review\"}}}}}}}}",
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2026-07-11T10:20:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":300,\"cached_input_tokens\":100,\"output_tokens\":30}}}}",
        ]);
        File.WriteAllLines(Path.Combine(sessions, $"rollout-{spawnedChildId}.jsonl"),
        [
            $"{{\"timestamp\":\"2026-07-11T10:30:00+08:00\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{spawnedChildId}\",\"source\":{{\"subagent\":{{\"thread_spawn\":{{\"parent_thread_id\":\"{parentId}\"}}}}}}}}}}",
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2026-07-11T09:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":400,\"output_tokens\":100}}}}",
            "{\"timestamp\":\"2026-07-11T10:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":2000,\"cached_input_tokens\":800,\"output_tokens\":200}}}}",
            "{\"timestamp\":\"2026-07-11T11:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":2300,\"cached_input_tokens\":900,\"output_tokens\":230}}}}",
        ]);

        TokenCostCollector collector = new(pricingPath);
        TokenCostSummary lifetime = collector.CollectCodex(
            temp.Path,
            new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8))).Lifetime;
        AssertEqual(2860L, lifetime.TotalTokens, "subagent lifetime excludes only matching replay prefix");
        AssertEqual(0.006m, lifetime.CostUsd, "subagent lifetime cost");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a collector backed by fake OAuth credentials and a fixed official quota response.
    /// </summary>
    private static CodexUsageCollector CreateCodexUsageCollector(string codexRoot, DateTimeOffset now, long sessionResetAt, long weeklyResetAt, double primaryUsed, double secondaryUsed, out HttpClient client, string? resetCreditsBody = null)
    {
        File.WriteAllText(Path.Combine(codexRoot, "auth.json"), JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt",
            tokens = new
            {
                access_token = "test-token",
                account_id = "account-123",
            },
        }));

        string body = JsonSerializer.Serialize(new
        {
            plan_type = "pro",
            rate_limit = new
            {
                primary_window = new
                {
                    used_percent = primaryUsed,
                    limit_window_seconds = 18000,
                    reset_at = sessionResetAt,
                },
                secondary_window = new
                {
                    used_percent = secondaryUsed,
                    limit_window_seconds = 604800,
                    reset_at = weeklyResetAt,
                },
            },
        });
        client = new HttpClient(new FakeHttpMessageHandler(body, resetCreditsBody));
        return new CodexUsageCollector(() => now, client);
    }

    /// <summary>
    /// Finds one named element inside an ItemsControl item template.
    /// </summary>
    private static T FindItemsControlTemplateChild<T>(System.Windows.Controls.ItemsControl itemsControl, int index, string name)
        where T : System.Windows.FrameworkElement
    {
        if (itemsControl.ItemContainerGenerator.ContainerFromIndex(index) is not System.Windows.Controls.ContentPresenter presenter)
        {
            throw new InvalidOperationException($"Could not find ItemsControl container {index} for {name}.");
        }

        presenter.ApplyTemplate();
        if (itemsControl.ItemTemplate.FindName(name, presenter) is not T child)
        {
            throw new InvalidOperationException($"Could not find {typeof(T).Name} named {name} in ItemsControl item {index}.");
        }

        return child;
    }

    /// <summary>
    /// Finds one named element inside a generated WPF template tree.
    /// </summary>
    private static T FindNamedDescendant<T>(System.Windows.DependencyObject root, string name)
        where T : System.Windows.FrameworkElement
    {
        if (root is T element && element.Name == name)
        {
            return element;
        }

        int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            System.Windows.DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            try
            {
                return FindNamedDescendant<T>(child, name);
            }
            catch (InvalidOperationException)
            {
            }
        }

        throw new InvalidOperationException($"Could not find {typeof(T).Name} named {name}.");
    }

    /// <summary>
    /// Finds the first descendant of the specified type.
    /// </summary>
    private static T FindTypedDescendant<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        if (root is T match)
        {
            return match;
        }

        int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            try
            {
                return FindTypedDescendant<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, index));
            }
            catch (InvalidOperationException)
            {
            }
        }

        throw new InvalidOperationException($"Could not find {typeof(T).Name}.");
    }

    /// <summary>
    /// Resolves a repository-relative file from the test output directory.
    /// </summary>
    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"repository file was not found: {relativePath}");
    }

    /// <summary>
    /// Asserts that a condition is true.
    /// </summary>
    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Asserts one published pricing period's rates and UTC window.
    /// </summary>
    private static void AssertPricingPeriod(
        JsonElement period,
        decimal input,
        decimal cachedInput,
        decimal output,
        string daysUtc,
        string startUtc,
        string endUtc,
        string name)
    {
        AssertEqual(input, period.GetProperty("input").GetDecimal(), $"{name} input");
        AssertEqual(cachedInput, period.GetProperty("cachedInput").GetDecimal(), $"{name} cached input");
        AssertEqual(output, period.GetProperty("output").GetDecimal(), $"{name} output");
        AssertEqual(daysUtc, string.Join(',', period.GetProperty("daysUtc").EnumerateArray().Select(day => day.GetInt32())), $"{name} days");
        AssertEqual(startUtc, period.GetProperty("startUtc").GetString(), $"{name} start");
        AssertEqual(endUtc, period.GetProperty("endUtc").GetString(), $"{name} end");
    }

    /// <summary>
    /// Asserts that two values are equal.
    /// </summary>
    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
        }
    }

    /// <summary>
    /// Asserts that an asynchronous operation propagates cancellation.
    /// </summary>
    private static async Task AssertCanceledAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("operation should have propagated cancellation");
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexTrayTests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Creates a temporary directory for a test.
    /// </summary>
    public TempDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// Deletes the temporary directory.
    /// </summary>
    public void Dispose()
    {
        Directory.Delete(Path, true);
    }
}

internal sealed class CanceledHttpMessageHandler : HttpMessageHandler
{
    /// <summary>
    /// Simulates an HttpClient timeout that was not caused by the caller token.
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated timeout"));
    }
}

internal sealed class GrokOAuthRefreshHttpMessageHandler : HttpMessageHandler
{
    private const string k_CreditsEndpoint = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    private const string k_SettingsEndpoint = "https://cli-chat-proxy.grok.com/v1/settings";
    private const string k_TokenEndpoint = "https://auth.x.ai/oauth2/token";
    private readonly string m_OldAccessToken;
    private readonly string m_NewAccessToken;
    private readonly string m_ExpectedRefreshToken;
    private readonly string m_RotatedRefreshToken;
    private readonly string m_CreditsBody;
    private readonly string m_SettingsBody;
    private readonly HttpStatusCode m_SettingsStatus;

    /// <summary>
    /// Creates a handler that refreshes OAuth tokens and then serves billing usage.
    /// </summary>
    public GrokOAuthRefreshHttpMessageHandler(
        string oldAccessToken,
        string newAccessToken,
        string expectedRefreshToken,
        string rotatedRefreshToken,
        string creditsBody,
        string settingsBody = "{}",
        HttpStatusCode settingsStatus = HttpStatusCode.OK)
    {
        m_OldAccessToken = oldAccessToken;
        m_NewAccessToken = newAccessToken;
        m_ExpectedRefreshToken = expectedRefreshToken;
        m_RotatedRefreshToken = rotatedRefreshToken;
        m_CreditsBody = creditsBody;
        m_SettingsBody = settingsBody;
        m_SettingsStatus = settingsStatus;
    }

    /// <summary>
    /// Handles Grok OAuth refresh and billing requests for collector tests.
    /// </summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? requestUri = request.RequestUri?.ToString();
        if (requestUri == k_TokenEndpoint)
        {
            string body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!body.Contains($"refresh_token={Uri.EscapeDataString(m_ExpectedRefreshToken)}", StringComparison.Ordinal) &&
                !body.Contains($"refresh_token={m_ExpectedRefreshToken}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("missing Grok refresh token");
            }

            string response = JsonSerializer.Serialize(new
            {
                access_token = m_NewAccessToken,
                refresh_token = m_RotatedRefreshToken,
                expires_in = 3600,
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }

        if (requestUri == k_CreditsEndpoint)
        {
            if (!request.Headers.TryGetValues("X-XAI-Token-Auth", out IEnumerable<string>? tokenAuth) || tokenAuth.Single() != "xai-grok-cli" ||
                !request.Headers.TryGetValues("x-userid", out IEnumerable<string>? userIds) || userIds.Single() != "grok-user-123" ||
                !request.Headers.Accept.Any(value => value.MediaType == "application/json"))
            {
                throw new InvalidOperationException("missing Grok credits headers");
            }

            if (request.Headers.Authorization?.Parameter == m_OldAccessToken)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (request.Headers.Authorization?.Parameter != m_NewAccessToken)
            {
                throw new InvalidOperationException("unexpected Grok access token");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(m_CreditsBody, Encoding.UTF8, "application/json"),
            };
        }

        if (requestUri == k_SettingsEndpoint)
        {
            if (!request.Headers.TryGetValues("X-XAI-Token-Auth", out IEnumerable<string>? tokenAuth) || tokenAuth.Single() != "xai-grok-cli" ||
                !request.Headers.TryGetValues("x-userid", out IEnumerable<string>? userIds) || userIds.Single() != "grok-user-123" ||
                !request.Headers.Accept.Any(value => value.MediaType == "application/json") ||
                request.Headers.Authorization?.Parameter != m_NewAccessToken)
            {
                throw new InvalidOperationException("missing Grok settings headers");
            }

            return new HttpResponseMessage(m_SettingsStatus)
            {
                Content = new StringContent(m_SettingsBody, Encoding.UTF8, "application/json"),
            };
        }

        throw new InvalidOperationException($"unexpected Grok request URI: {requestUri}");
    }
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private const string k_UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
    private const string k_ResetCreditsEndpoint = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    private readonly string m_UsageBody;
    private readonly string m_ResetCreditsBody;

    /// <summary>
    /// Creates a handler that returns a fixed JSON response.
    /// </summary>
    public FakeHttpMessageHandler(string usageBody, string? resetCreditsBody = null)
    {
        m_UsageBody = usageBody;
        m_ResetCreditsBody = resetCreditsBody ?? "{\"available_count\":0,\"credits\":[]}";
    }

    /// <summary>
    /// Sends a fake HTTP response for collector tests.
    /// </summary>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? requestUri = request.RequestUri?.ToString();
        if (requestUri != k_UsageEndpoint && requestUri != k_ResetCreditsEndpoint)
        {
            throw new InvalidOperationException("unexpected request URI");
        }

        if (request.Headers.Authorization?.Parameter != "test-token")
        {
            throw new InvalidOperationException("missing authorization header");
        }

        if (!request.Headers.TryGetValues("ChatGPT-Account-Id", out IEnumerable<string>? accountIds) || accountIds.Single() != "account-123")
        {
            throw new InvalidOperationException("missing account header");
        }

        if (requestUri == k_ResetCreditsEndpoint &&
            (!request.Headers.TryGetValues("OpenAI-Beta", out IEnumerable<string>? betaValues) || betaValues.Single() != "codex-1" ||
             !request.Headers.TryGetValues("originator", out IEnumerable<string>? originatorValues) || originatorValues.Single() != "Codex Desktop"))
        {
            throw new InvalidOperationException("missing reset credit headers");
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(requestUri == k_UsageEndpoint ? m_UsageBody : m_ResetCreditsBody, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    /// Sends a fake asynchronous HTTP response for collector tests.
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(Send(request, cancellationToken));
    }
}

internal sealed class ApiUsageHttpMessageHandler : HttpMessageHandler
{
    /// <summary>
    /// Returns fixed account responses for supported API providers.
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string host = request.RequestUri?.Host ?? string.Empty;
        string body;
        if (host == "deepseek.example")
        {
            AssertBearerRequest(request, HttpMethod.Get, "/user/balance", "deepseek-key");
            body = "{\"is_available\":true,\"balance_infos\":[{\"currency\":\"USD\",\"total_balance\":\"15.00\"},{\"currency\":\"CNY\",\"total_balance\":\"110.00\"}]}";
        }
        else if (host == "newapi.example")
        {
            AssertBearerRequest(request, HttpMethod.Get, "/api/user/self", "newapi-token");
            if (!request.Headers.TryGetValues("New-Api-User", out IEnumerable<string>? userIds) || userIds.Single() != "42")
            {
                throw new InvalidOperationException("missing NewAPI user header");
            }

            body = "{\"success\":true,\"data\":{\"group\":\"default\",\"quota\":5000000,\"used_quota\":2500000}}";
        }
        else if (host == "openrouter.example")
        {
            AssertBearerRequest(request, HttpMethod.Get, "/api/v1/credits", "openrouter-management-key");
            body = "{\"data\":{\"total_credits\":100.5,\"total_usage\":25.75}}";
        }
        else if (host == "ai-gateway.example")
        {
            AssertBearerRequest(request, HttpMethod.Get, "/v1/credits", "vercel-gateway-key");
            body = "{\"balance\":\"95.50\",\"total_used\":\"4.50\"}";
        }
        else if (host is "nano-gpt.example" or "nano-gpt-failure.example")
        {
            string token = host == "nano-gpt.example" ? "nanogpt-key" : "nanogpt-failure-key";
            if (request.RequestUri?.AbsolutePath == "/api/check-balance")
            {
                if (request.Method != HttpMethod.Post || request.Headers.Authorization != null ||
                    !request.Headers.TryGetValues("X-API-Key", out IEnumerable<string>? apiKeys) || apiKeys.Single() != token)
                {
                    throw new InvalidOperationException("invalid NanoGPT balance request");
                }

                body = "{\"usd_balance\":\"12.3456\"}";
            }
            else
            {
                AssertBearerRequest(request, HttpMethod.Get, "/api/v1/usage", token);
                if (!string.IsNullOrEmpty(request.RequestUri?.Query))
                {
                    throw new InvalidOperationException("unexpected NanoGPT usage query");
                }

                if (host == "nano-gpt-failure.example")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                body = "{\"totals\":{\"netCostUsd\":3.0}}";
            }
        }
        else
        {
            throw new InvalidOperationException("unexpected API usage host");
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }

    /// <summary>
    /// Validates a monitored API request path and credential.
    /// </summary>
    private static void AssertBearerRequest(HttpRequestMessage request, HttpMethod method, string path, string token)
    {
        if (request.Method != method || request.RequestUri?.AbsolutePath != path ||
            request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Authorization.Parameter != token)
        {
            throw new InvalidOperationException("invalid API usage request");
        }
    }
}
