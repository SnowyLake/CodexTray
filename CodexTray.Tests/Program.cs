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

namespace CodexTray.Tests;

internal static class Program
{
    private static int s_Failures;

    /// <summary>
    /// Runs deterministic checks or the opt-in redacted Cursor live probe.
    /// </summary>
    private static async Task<int> Main(string[] args)
    {
        if (args.SequenceEqual(["--cursor-live"], StringComparer.Ordinal))
        {
            return await RunCursorLiveAsync();
        }

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
        await RunAsync("omits reset suffix when disabled", TestDisplayWithoutResetSuffixAsync);
        await RunAsync("uses absolute reset time when enabled", TestAbsoluteResetTimeAsync);
        await RunAsync("serves health and usage over HTTP", TestHttpServerAsync);
        await RunAsync("merges and clears plugin usage sources", TestUsageCacheSourcesAsync);
        await RunAsync("drains active HTTP clients on stop", TestHttpServerActiveClientStopAsync);
        await RunAsync("recovers HTTP server after bind failure", TestHttpServerBindFailureRecoveryAsync);
        await RunAsync("supports idempotent stop and restart", TestHttpServerStopRestartAsync);
        await RunAsync("propagates collector cancellation", TestCollectorCancellationAsync);
        await RunAsync("distinguishes API cancellation from timeout", TestApiUsageCancellationAsync);
        await RunAsync("installs LiteMonitor plugin config", TestPluginInstallAsync);
        await RunAsync("installs TrafficMonitor plugin", TestTrafficMonitorPluginInstallAsync);
        await RunAsync("stores settings beside the executable", TestSettingsStorePathAsync);
        await RunAsync("repairs missing settings fields", TestSettingsStoreRepairsMissingFieldsAsync);
        await RunAsync("repairs null and malformed settings", TestSettingsStoreRepairsNullAndMalformedValuesAsync);
        await RunAsync("normalizes settings refresh interval", TestSettingsNormalizeAsync);
        await RunAsync("formats compact token units", TestTokenUnitFormattingAsync);
        await RunAsync("persists API monitor settings", TestApiMonitorSettingsAsync);
        await RunAsync("collects DeepSeek and NewAPI balances", TestApiUsageCollectorAsync);
        await RunAsync("parses Grok billing protobuf", TestGrokUsageCollectorAsync);
        await RunAsync("refreshes expired Grok Build OAuth", TestGrokBuildOAuthRefreshAsync);
        await RunAsync("ignores OpenCode OAuth for Grok", TestGrokIgnoresOpenCodeOAuthAsync);
        await RunAsync("parses Cursor usage-summary JSON", TestCursorUsageCollectorAsync);
        await RunAsync("refreshes expired Cursor OAuth", TestCursorOAuthRefreshAsync);
        await RunAsync("collects complete Cursor dashboard token cost", TestCursorDashboardAsync);
        await RunAsync("classifies Cursor event totalCents validation", TestCursorTotalCentsValidationAsync);
        await RunAsync("clears incomplete Cursor dashboard regions", TestCursorDashboardFailuresAsync);
        await RunAsync("shares one Cursor dashboard OAuth refresh", TestCursorDashboardRefreshBudgetAsync);
        await RunAsync("includes published model pricing", TestPublishedModelPricingAsync);
        await RunAsync("summarizes API refresh statuses", TestApiUsageSummaryAsync);
        await RunAsync("tracks asynchronous refresh commands", TestRefreshCommandAsync);
        await RunAsync("builds rolling token cost chart", TestTokenCostChartViewModelAsync);
        await RunAsync("tracks migrated dirty properties", TestMigratedDirtyPropertiesAsync);
        await RunAsync("raises migrated tray notifications", TestMigratedTrayNotificationsAsync);
        await RunAsync("updates API monitor command states", TestApiMonitorCommandStatesAsync);
        await RunAsync("raises dependent API monitor notifications", TestApiMonitorNotificationsAsync);
        await RunAsync("collects exact Codex token cost", TestTokenCostCollectorAsync);
        await RunAsync("applies model alias pricing", TestModelAliasPricingAsync);
        await RunAsync("collects local Grok Build token statistics", TestGrokTokenCostCollectorAsync);
        await RunAsync("collects local OpenCode token cost", TestOpenCodeTokenCostAsync);
        await RunAsync("counts live subagent usage without replaying parent history", TestSubagentTokenCostAsync);
        Console.WriteLine(s_Failures == 0 ? "All C# tests passed." : $"C# tests failed: {s_Failures}");
        return s_Failures == 0 ? 0 : 1;
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
                AssertEqual("Session", viewModel.SessionQuota.Title, "Codex session quota title");
                AssertEqual("Weekly", viewModel.WeeklyQuota.Title, "Codex weekly quota title");
                AssertEqual("Weekly", viewModel.GrokWeeklyQuota.Title, "Grok weekly quota title");
                AssertEqual("Monthly", viewModel.CursorMonthlyQuota.Title, "Cursor monthly quota title");
                viewModel.UpdateStatus(
                    isRunning: true,
                    CodexTrayDefaults.Port,
                    new UsageResponse
                    {
                        Available = true,
                        Limits = new UsageLimits
                        {
                            Session = new UsageLimit { WindowMinutes = 300, RemainingPercent = 100 },
                            Weekly = new UsageLimit { WindowMinutes = 10_080, RemainingPercent = 98 },
                        },
                        ResetCredits = new ResetCredits { Available = true, AvailableCount = 1 },
                    },
                    error: null);
                TrayPopupWindow window = new(viewModel);
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
                System.Windows.Controls.Border codexSessionCard = (System.Windows.Controls.Border)window.FindName("CodexSessionCard");
                System.Windows.Controls.Border codexWeeklyCard = (System.Windows.Controls.Border)window.FindName("CodexWeeklyCard");
                System.Windows.Controls.Border codexResetCreditsCard = (System.Windows.Controls.Border)window.FindName("CodexResetCreditsCard");
                AssertEqual(91d, codexSessionCard.ActualHeight, "Codex Session card height");
                AssertEqual(codexSessionCard.ActualHeight, codexWeeklyCard.ActualHeight, "Codex large card heights");
                AssertEqual(68d, codexResetCreditsCard.ActualHeight, "Codex small card height");

                viewModel.UpdateGrokDashboard(new GrokUsageDashboard(
                    new GrokUsageSnapshot(5, DateTimeOffset.Now.AddDays(7).ToUnixTimeSeconds(), "SuperGrok Heavy"),
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
                AssertTrue(grokQuotaScrollViewer.ScrollableHeight == 0, $"Grok weekly quota card should not scroll, actual {grokQuotaScrollViewer.ScrollableHeight}");
                System.Windows.Controls.StackPanel grokQuotaCardPanel = (System.Windows.Controls.StackPanel)window.FindName("GrokQuotaCardPanel");
                AssertEqual(1, grokQuotaCardPanel.Children.Count, "Grok quota card count");
                System.Windows.Controls.Border grokWeeklyCard = (System.Windows.Controls.Border)window.FindName("GrokWeeklyCard");
                AssertEqual(codexWeeklyCard.ActualHeight, grokWeeklyCard.ActualHeight, "Codex and Grok Weekly card heights");
                AssertEqual(4, viewModel.GrokTokenCostRows.Count, "Grok token cost row count");
                AssertEqual(7, viewModel.GrokTokenCostChartDays.Count, "Grok token cost chart day count");
                AssertEqual("SUPERGROK HEAVY", viewModel.GrokPlanDisplay, "Grok subscription badge");
                System.Windows.Controls.ItemsControl grokChartItemsControl = (System.Windows.Controls.ItemsControl)window.FindName("GrokTokenCostChartItemsControl");
                System.Windows.Controls.ContentPresenter grokChartPresenter = (System.Windows.Controls.ContentPresenter)grokChartItemsControl.ItemContainerGenerator.ContainerFromIndex(6);
                System.Windows.Controls.Border grokChartBar = (System.Windows.Controls.Border)grokChartItemsControl.ItemTemplate.FindName("TokenCostChartBar", grokChartPresenter);
                System.Windows.Media.Color grokStatusColor = ((System.Windows.Media.SolidColorBrush)viewModel.GrokStatusDotBrush).Color;
                System.Windows.Media.Color codexStatusColor = ((System.Windows.Media.SolidColorBrush)viewModel.StatusDotBrush).Color;
                AssertEqual(grokStatusColor, ((System.Windows.Media.SolidColorBrush)grokChartBar.Background).Color, "Grok token chart status color");
                AssertTrue(grokStatusColor != codexStatusColor, "Grok token chart should not reuse the Codex status color");
                System.Windows.Controls.Button grokTabButton = (System.Windows.Controls.Button)window.FindName("GrokTabButton");
                System.Windows.Controls.StackPanel tabPanel = (System.Windows.Controls.StackPanel)grokTabButton.Parent;
                int grokTabIndex = tabPanel.Children.IndexOf(grokTabButton);
                AssertEqual("Codex", ((System.Windows.Controls.Button)tabPanel.Children[grokTabIndex - 1]).ToolTip, "Grok tab predecessor");
                AssertEqual("Cursor", ((System.Windows.Controls.Button)tabPanel.Children[grokTabIndex + 1]).ToolTip, "Grok tab successor");

                viewModel.UpdateCursorDashboard(new CursorUsageDashboard(
                    new CursorUsageSnapshot("Pro", 2, 3, 4, DateTimeOffset.Now.AddDays(7).ToUnixTimeSeconds()),
                    null,
                    string.Empty,
                    string.Empty,
                    DateTimeOffset.Now));
                viewModel.ShowCursor();
                content.InvalidateMeasure();
                content.Measure(new System.Windows.Size(window.Width, window.Height));
                content.Arrange(new System.Windows.Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                System.Windows.Controls.ScrollViewer cursorQuotaScrollViewer = (System.Windows.Controls.ScrollViewer)window.FindName("CursorQuotaScrollViewer");
                AssertTrue(cursorQuotaScrollViewer.ScrollableHeight == 0, $"default Cursor quota cards should not scroll, actual {cursorQuotaScrollViewer.ScrollableHeight}");
                System.Windows.Controls.Border cursorMonthlyCard = (System.Windows.Controls.Border)window.FindName("CursorMonthlyCard");
                System.Windows.Controls.Border cursorAutoCard = (System.Windows.Controls.Border)window.FindName("CursorAutoCard");
                System.Windows.Controls.Border cursorApiCard = (System.Windows.Controls.Border)window.FindName("CursorApiCard");
                AssertEqual(codexSessionCard.ActualHeight, cursorMonthlyCard.ActualHeight, "Codex and Cursor large card heights");
                AssertEqual(codexResetCreditsCard.ActualHeight, cursorAutoCard.ActualHeight, "Codex and Cursor small card heights");
                AssertEqual(cursorAutoCard.ActualHeight, cursorApiCard.ActualHeight, "Cursor small card heights");

                viewModel.ShowApi();
                content.InvalidateMeasure();
                content.Measure(new System.Windows.Size(window.Width, window.Height));
                content.Arrange(new System.Windows.Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                System.Windows.Controls.ItemsControl apiMonitorItemsControl = (System.Windows.Controls.ItemsControl)window.FindName("ApiMonitorItemsControl");
                System.Windows.Controls.ContentPresenter apiMonitorPresenter = (System.Windows.Controls.ContentPresenter)apiMonitorItemsControl.ItemContainerGenerator.ContainerFromIndex(0);
                System.Windows.DataTemplate apiMonitorTemplate = apiMonitorPresenter.ContentTemplate;
                System.Windows.Controls.Border apiMonitorCard = (System.Windows.Controls.Border)apiMonitorTemplate.FindName("ApiMonitorCard", apiMonitorPresenter);
                AssertEqual(apiMonitorCard.ActualHeight, codexSessionCard.ActualHeight, "API monitor and quota large card heights");
                AssertEqual(apiMonitorCard.Margin.Bottom, codexSessionCard.Margin.Bottom, "API monitor and Codex card spacing");
                AssertEqual(apiMonitorCard.Margin.Bottom, codexWeeklyCard.Margin.Bottom, "API monitor and Codex second card spacing");
                AssertEqual(apiMonitorCard.Margin.Bottom, cursorMonthlyCard.Margin.Bottom, "API monitor and Cursor card spacing");
                AssertEqual(apiMonitorCard.Margin.Bottom, cursorAutoCard.Margin.Bottom, "API monitor and Cursor second card spacing");
                window.Close();
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
                            Session = new UsageLimit { WindowMinutes = 300, RemainingPercent = 72, ResetLabel = "resets in 2h 18m" },
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
                    Today = new TokenCostSummary { TotalTokens = 20_740_000, CostUsd = 13.40m },
                    LastSevenDays = new TokenCostSummary { TotalTokens = 130_100_000, CostUsd = 97.15m },
                    LastThirtyDays = new TokenCostSummary { TotalTokens = 511_420_000, CostUsd = 398.48m },
                    Lifetime = new TokenCostSummary { TotalTokens = 917_780_000, CostUsd = 624.73m },
                    LastSevenDaysDaily = Enumerable.Range(0, 7)
                        .Select(index => new TokenCostDailySummary
                        {
                            Date = now.Date.AddDays(index - 6),
                            Summary = new TokenCostSummary { TotalTokens = (index + 4) * 1_000_000, CostUsd = index + 4 },
                        })
                        .ToArray(),
                });
                viewModel.UpdateGrokDashboard(new GrokUsageDashboard(
                    new GrokUsageSnapshot(20, now.AddDays(5).ToUnixTimeSeconds(), "X Premium"),
                    string.Empty,
                    now),
                    new TokenCostStatistics
                    {
                        Today = new TokenCostSummary { TotalTokens = 8_210_000, CostUsd = 5.72m },
                        LastSevenDays = new TokenCostSummary { TotalTokens = 45_360_000, CostUsd = 31.84m },
                        LastThirtyDays = new TokenCostSummary { TotalTokens = 174_820_000, CostUsd = 118.26m },
                        Lifetime = new TokenCostSummary { TotalTokens = 294_170_000, CostUsd = 201.49m },
                        LastSevenDaysDaily = Enumerable.Range(0, 7)
                            .Select(index => new TokenCostDailySummary
                            {
                                Date = now.Date.AddDays(index - 6),
                                Summary = new TokenCostSummary { TotalTokens = (index + 2) * 900_000, CostUsd = (index + 2) * 0.64m },
                            })
                            .ToArray(),
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
    /// Runs a redacted live Cursor dashboard probe without emitting credentials or event details.
    /// </summary>
    private static async Task<int> RunCursorLiveAsync()
    {
        CursorUsageDashboard dashboard = await new CursorUsageCollector().CollectDashboardAsync();
        Console.WriteLine($"LIVE Cursor usage: {(dashboard.Usage == null ? "unavailable" : "available")}");
        Console.WriteLine($"LIVE Cursor usage error: {FormatLiveError(dashboard.UsageError)}");
        Console.WriteLine($"LIVE Cursor refresh: initial={dashboard.InitialCredentialRefreshUsed}, forced={dashboard.ForcedCredentialRefreshUsed}");
        if (dashboard.TokenCostDiagnostics is not CursorUsageEventsDiagnostics diagnostics)
        {
            Console.WriteLine("LIVE Cursor usage-events: unavailable");
            Console.WriteLine($"LIVE Cursor usage-events error: {FormatLiveError(dashboard.TokenCostError)}");
            return 1;
        }

        Console.WriteLine($"LIVE Cursor usage-events: events={diagnostics.EventCount}, pages={diagnostics.PageCount}, tokenEvents={diagnostics.TokenEventCount}, zeroTokenMissingCost={diagnostics.ZeroTokenMissingCostEventCount}");
        Console.WriteLine(
            $"LIVE Cursor token fields: input={diagnostics.InputTokenFieldCount}/{diagnostics.TokenEventCount}, " +
            $"output={diagnostics.OutputTokenFieldCount}/{diagnostics.TokenEventCount}, " +
            $"cacheRead={diagnostics.CacheReadTokenFieldCount}/{diagnostics.TokenEventCount}, " +
            $"cacheWrite={diagnostics.CacheWriteTokenFieldCount}/{diagnostics.TokenEventCount}");
        Console.WriteLine($"LIVE Cursor usage-events elapsed: {diagnostics.Elapsed.TotalMilliseconds:0} ms");
        return dashboard.Usage == null ? 1 : 0;
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
    /// Formats a collector-generated live probe error without exposing secrets or payload data.
    /// </summary>
    private static string FormatLiveError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "none";
        }

        if (error is "Request timed out" or "Cursor network request failed." or "Cursor response JSON decode failed." or "Cursor response numeric overflow." or "Cursor local auth database could not be read." ||
            error.StartsWith("Cursor local auth database was not found.", StringComparison.Ordinal) ||
            error.StartsWith("Cursor OAuth access token was not found.", StringComparison.Ordinal) ||
            error.StartsWith("Cursor OAuth token", StringComparison.Ordinal) ||
            error.StartsWith("Cursor usage request failed:", StringComparison.Ordinal) ||
            error.StartsWith("Cursor usage-events request failed:", StringComparison.Ordinal) ||
            error.StartsWith("Cursor usage events ", StringComparison.Ordinal) ||
            error.StartsWith("Cursor usage event ", StringComparison.Ordinal) ||
            error.StartsWith("Cursor usage-summary ", StringComparison.Ordinal))
        {
            return error;
        }

        return "unclassified request or response failure";
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
    private static Task TestCollectsLimitsAndDisplayLabelsAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long sessionResetAt = now.AddHours(2).AddMinutes(5).ToUnixTimeSeconds();
        long weeklyResetAt = now.AddDays(3).AddHours(4).ToUnixTimeSeconds();
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, sessionResetAt, weeklyResetAt, 12.0, 34.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = collector.Collect(temp.Path);

        AssertTrue(response.Available, "response should be available");
        AssertEqual(12, response.Limits.Session.UsedPercent, "session used percent");
        AssertEqual(88, response.Limits.Session.RemainingPercent, "session remaining percent");
        AssertEqual(34, response.Limits.Weekly.UsedPercent, "weekly used percent");
        AssertEqual(66, response.Limits.Weekly.RemainingPercent, "weekly remaining percent");
        AssertEqual("pro", response.PlanType, "plan type");
        AssertEqual("88% 2h05m", response.Display.Session, "session display");
        AssertEqual("66% 3d04h", response.Display.Weekly, "weekly display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests weekly countdown labels on the current day.
    /// </summary>
    private static Task TestWeeklyCountdownLabelAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long sessionResetAt = now.AddHours(1).ToUnixTimeSeconds();
        long weeklyResetAt = now.AddHours(3).ToUnixTimeSeconds();
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, sessionResetAt, weeklyResetAt, 20.0, 40.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = collector.Collect(temp.Path);

        AssertEqual("60% 0d03h", response.Display.Weekly, "weekly countdown display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests weekly countdown labels on the next day even when below twenty four hours.
    /// </summary>
    private static Task TestNextDayWeeklyCountdownLabelAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 23, 0, 0, TimeSpan.FromHours(8));
        long sessionResetAt = now.AddHours(1).ToUnixTimeSeconds();
        DateTimeOffset weeklyResetAt = new(2026, 7, 2, 2, 0, 0, TimeSpan.FromHours(8));
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, sessionResetAt, weeklyResetAt.ToUnixTimeSeconds(), 20.0, 40.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = collector.Collect(temp.Path);

        AssertEqual("60% 0d03h", response.Display.Weekly, "weekly next-day countdown display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests the unavailable response when OAuth credentials are missing.
    /// </summary>
    private static Task TestEmptyResponseAsync()
    {
        using TempDirectory temp = new();
        CodexTrayCollector collector = new(() => new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8)));
        UsageResponse response = collector.Collect(temp.Path);

        AssertTrue(!response.Available, "response should be unavailable");
        AssertEqual("N/A", response.Display.Session, "session unavailable display");
        AssertEqual("N/A", response.Display.Summary, "summary unavailable display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests official ChatGPT quota parsing for Codex OAuth accounts.
    /// </summary>
    private static Task TestOfficialQuotaAsync()
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
        CodexTrayCollector collector = new(() => now, client);

        UsageResponse response = collector.Collect(temp.Path);

        AssertTrue(response.Available, "official response should be available");
        AssertEqual("official_api", response.Source, "official source");
        AssertEqual("unknown", response.PlanType, "missing plan type");
        AssertEqual(75, response.Limits.Session.RemainingPercent, "official session remaining percent");
        AssertEqual(60, response.Limits.Weekly.RemainingPercent, "official weekly remaining percent");
        AssertEqual("75% 1h15m", response.Display.Session, "official session display");
        AssertEqual("60% 2d12h", response.Display.Weekly, "official weekly display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests that plugin display values drop the reset suffix when the option is disabled.
    /// </summary>
    private static Task TestDisplayWithoutResetSuffixAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long sessionResetAt = now.AddHours(1).AddMinutes(15).ToUnixTimeSeconds();
        long weeklyResetAt = now.AddDays(2).AddHours(12).ToUnixTimeSeconds();
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, sessionResetAt, weeklyResetAt, 25.0, 40.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = collector.Collect(temp.Path, showResetTimeInPlugins: false);

        AssertEqual("75%", response.Display.Session, "session display without reset suffix");
        AssertEqual("60%", response.Display.Weekly, "weekly display without reset suffix");
        AssertEqual("1h15m", response.Limits.Session.ResetLabel, "reset label remains available for the panel");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests that reset labels use absolute clock and date when the option is enabled.
    /// </summary>
    private static Task TestAbsoluteResetTimeAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long sessionResetAt = now.AddHours(1).AddMinutes(15).ToUnixTimeSeconds();
        long weeklyResetAt = now.AddDays(2).AddHours(12).ToUnixTimeSeconds();
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, sessionResetAt, weeklyResetAt, 25.0, 40.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = collector.Collect(temp.Path, showResetTimeInPlugins: true, useAbsoluteResetTime: true);

        AssertEqual("13:15", response.Limits.Session.ResetLabel, "session absolute reset clock");
        AssertEqual("07-04", response.Limits.Weekly.ResetLabel, "weekly absolute reset date");
        AssertEqual("75% 13:15", response.Display.Session, "session display with absolute reset");
        AssertEqual("60% 07-04", response.Display.Weekly, "weekly display with absolute reset");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests the lightweight HTTP server endpoints.
    /// </summary>
    private static async Task TestHttpServerAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, now.AddHours(1).ToUnixTimeSeconds(), now.AddDays(2).ToUnixTimeSeconds(), 10.0, 20.0, out HttpClient collectorClient);
        using HttpClient _ = collectorClient;
        UsageCache usageCache = new();
        usageCache.UpdateCodex(collector.Collect(temp.Path));
        usageCache.UpdateCursor(CursorUsageCollector.BuildPluginUsage(
            new CursorUsageDashboard(
                new CursorUsageSnapshot("Pro", 25, 0, 0, now.AddDays(30).ToUnixTimeSeconds()),
                null,
                string.Empty,
                string.Empty,
                now),
            showResetTime: true,
            useAbsoluteResetTime: false));
        using LightweightHttpServer server = new(usageCache, 0);
        server.Start();

        using HttpClient client = new();
        string health = await client.GetStringAsync($"http://{CodexTrayDefaults.Host}:{server.Port}{CodexTrayDefaults.HealthEndpointPath}");
        AssertTrue(health.Contains("\"ok\":true", StringComparison.Ordinal), "health response");

        string usageJson = await client.GetStringAsync($"http://{CodexTrayDefaults.Host}:{server.Port}{CodexTrayDefaults.UsageEndpointPath}");
        using JsonDocument document = JsonDocument.Parse(usageJson);
        JsonElement display = document.RootElement.GetProperty("display");
        JsonElement limits = document.RootElement.GetProperty("limits");
        AssertEqual("90% 1h00m", display.GetProperty("session").GetString(), "HTTP plugin session display");
        AssertEqual("80% 2d00h", display.GetProperty("weekly").GetString(), "HTTP plugin weekly display");
        AssertEqual("75% 30d00h", display.GetProperty("cursor_monthly").GetString(), "HTTP plugin Cursor monthly display");
        AssertEqual(75, limits.GetProperty("cursor_monthly").GetProperty("remaining_percent").GetInt32(), "HTTP plugin Cursor monthly remaining percent");
        AssertTrue(!display.TryGetProperty("codex_5h", out JsonElement legacySession), "legacy session plugin field should be removed");
        AssertTrue(!display.TryGetProperty("codex_7d", out JsonElement legacyWeekly), "legacy weekly plugin field should be removed");

        string usageText = await client.GetStringAsync($"http://{CodexTrayDefaults.Host}:{server.Port}{CodexTrayDefaults.UsageTextEndpointPath}");
        string[] usageLines = usageText.Split(Environment.NewLine);
        AssertEqual(3, usageLines.Length, "text endpoint line count");
        AssertEqual("90% 1h00m", usageLines[0], "text endpoint session display");
        AssertEqual("80% 2d00h", usageLines[1], "text endpoint weekly display");
        AssertEqual("75% 30d00h", usageLines[2], "text endpoint Cursor monthly display");
        await server.StopAsync();
    }

    /// <summary>
    /// Tests independent merging and clearing of Codex and Cursor plugin values.
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
                Session = "90%",
                Weekly = "80%",
                Summary = "Codex Session: 90% | Codex Weekly: 80%",
            },
        });
        usageCache.UpdateCursor(new CursorPluginUsage(
            new UsageLimit { Name = "monthly", RemainingPercent = 70 },
            "70%"));

        UsageResponse merged = usageCache.Get() ?? throw new InvalidOperationException("merged usage should be available");
        AssertEqual("90%", merged.Display.Session, "merged Codex session display");
        AssertEqual("70%", merged.Display.CursorMonthly, "merged Cursor monthly display");

        usageCache.ClearCodex();
        UsageResponse cursorOnly = usageCache.Get() ?? throw new InvalidOperationException("Cursor-only usage should be available");
        AssertEqual("N/A", cursorOnly.Display.Session, "cleared Codex session display");
        AssertEqual("70%", cursorOnly.Display.CursorMonthly, "preserved Cursor monthly display");

        usageCache.ClearCursor();
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
    /// Tests pre-canceled Codex, token-cost, Cursor, and monitor locator operations.
    /// </summary>
    private static async Task TestCollectorCancellationAsync()
    {
        using TempDirectory temp = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await AssertCanceledAsync(() => new CodexTrayCollector().CollectAsync(temp.Path, cancellationToken: cancellation.Token));
        await AssertCanceledAsync(() => Task.Run(() => new TokenCostCollector().Collect(temp.Path, openCodeDirectory: temp.Path, cancellationToken: cancellation.Token)));
        await AssertCanceledAsync(() => Task.Run(() => new TokenCostCollector().CollectGrok(temp.Path, cancellationToken: cancellation.Token)));
        await AssertCanceledAsync(() => Task.Run(() => LiteMonitorLocator.AutoDetect(cancellationToken: cancellation.Token)));
        await AssertCanceledAsync(() => new CursorUsageCollector().CollectDashboardAsync(cancellationToken: cancellation.Token));
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
        AssertTrue(content.Contains("\"short_label\": \"Codex-Session\"", StringComparison.Ordinal), "plugin content should include Codex Session item");
        AssertTrue(content.Contains("\"short_label\": \"Codex-Weekly\"", StringComparison.Ordinal), "plugin content should include Codex Weekly item");
        AssertTrue(content.Contains("\"short_label\": \"Cursor-Monthly\"", StringComparison.Ordinal), "plugin content should include Cursor Monthly item");
        AssertTrue(content.Contains("\"format_val\": \"{{cursor_monthly_display}}\"", StringComparison.Ordinal), "plugin content should include Cursor Monthly value");
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
            VisiblePages = PageItem.Cursor | PageItem.Apis,
        };

        store.Save(settings);

        string expectedPath = Path.Combine(temp.Path, CodexTrayDefaults.SettingsFileName);
        AssertEqual(expectedPath, store.SettingsPath, "settings path");
        AssertTrue(File.Exists(expectedPath), "settings file should exist beside executable");
        AssertTrue(!Directory.Exists(Path.Combine(temp.Path, "CodexTray")), "settings directory should not exist");
        AssertEqual(17997, store.Load().Port, "saved settings port");
        AssertEqual(PageItem.Cursor | PageItem.Apis, store.Load().VisiblePages, "saved visible pages");
        settings.VisiblePages = PageItem.Codex | PageItem.Cursor | PageItem.Apis;
        store.Save(settings);
        AssertEqual(PageItem.Codex | PageItem.Cursor | PageItem.Apis, store.Load().VisiblePages, "saved settings should keep Grok hidden");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests loading partial settings and saving default fields back.
    /// </summary>
    private static Task TestSettingsStoreRepairsMissingFieldsAsync()
    {
        using TempDirectory temp = new();
        SettingsStore store = new(temp.Path);
        File.WriteAllText(store.SettingsPath, "{\"Port\":17996}");

        AppSettings settings = store.Load();

        AssertEqual(17996, settings.Port, "existing port");
        AssertEqual(string.Empty, settings.LiteMonitorDir, "default LiteMonitor path");
        AssertEqual(string.Empty, settings.TrafficMonitorDir, "default TrafficMonitor path");
        AssertEqual(CodexTrayDefaults.RefreshIntervalMinutes, settings.RefreshIntervalMinutes, "default refresh interval");
        AssertEqual(PageItem.All, settings.VisiblePages, "default visible pages");
        AssertTrue(!settings.StartWithWindows, "startup should be disabled by default");
        AssertEqual(AppSettings.ThemeModeSystem, settings.ThemeMode, "default theme mode");
        AssertEqual(AppSettings.TokenUnitEnglish, settings.TokenUnit, "default token unit");
        AssertTrue(settings.MicaEnabled, "Mica should be enabled by default");
        AssertTrue(settings.ShowResetTimeInPlugins, "plugin reset time should be shown by default");
        AssertTrue(settings.UseAbsoluteResetTime, "absolute reset time should be enabled by default");
        AssertTrue(settings.HideInvalidProgressBars, "invalid progress bars should be hidden by default");
        AssertEqual(0, settings.ApiMonitors.Count, "default API monitors");

        string repairedJson = File.ReadAllText(store.SettingsPath);
        using JsonDocument document = JsonDocument.Parse(repairedJson);
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.LiteMonitorDir), out _), "repaired settings should include LiteMonitor path");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.TrafficMonitorDir), out _), "repaired settings should include TrafficMonitor path");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.RefreshIntervalMinutes), out _), "repaired settings should include refresh interval");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.VisiblePages), out _), "repaired settings should include visible pages");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.SettingsSchemaVersion), out _), "repaired settings should include schema version");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.ThemeMode), out _), "repaired settings should include theme mode");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.TokenUnit), out _), "repaired settings should include token unit");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.MicaEnabled), out _), "repaired settings should include Mica toggle");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.ApiMonitors), out _), "repaired settings should include API monitors");
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
    /// Tests settings value normalization.
    /// </summary>
    private static Task TestSettingsNormalizeAsync()
    {
        AppSettings settings = new()
        {
            Port = -1,
            RefreshIntervalMinutes = 0,
            ThemeMode = "unexpected",
            TokenUnit = AppSettings.TokenUnitChinese,
            VisiblePages = PageItem.Cursor | (PageItem)(1 << 10),
            BackdropMode = "unexpected",
        };

        settings.Normalize();

        AssertEqual(CodexTrayDefaults.Port, settings.Port, "default port");
        AssertEqual(CodexTrayDefaults.RefreshIntervalMinutes, settings.RefreshIntervalMinutes, "default refresh interval");
        AssertEqual(AppSettings.ThemeModeSystem, settings.ThemeMode, "default theme mode");
        AssertEqual(AppSettings.TokenUnitChinese, settings.TokenUnit, "Chinese token unit");
        AssertEqual(PageItem.Cursor, settings.VisiblePages, "supported visible pages");
        AssertTrue(!settings.MicaEnabled, "unexpected backdrop should disable Mica");
        AssertEqual<string?>(null, settings.BackdropMode, "legacy backdrop should be cleared");
        settings.TokenUnit = "M/B";
        settings.Normalize();
        AssertEqual(AppSettings.TokenUnitEnglish, settings.TokenUnit, "legacy English token unit");
        settings.TokenUnit = "万/亿";
        settings.Normalize();
        AssertEqual(AppSettings.TokenUnitChinese, settings.TokenUnit, "legacy Chinese token unit");
        settings.VisiblePages = PageItem.Codex | PageItem.Cursor | PageItem.Apis;
        settings.Normalize();
        AssertEqual(PageItem.Codex | PageItem.Cursor | PageItem.Apis, settings.VisiblePages, "normalized settings should keep Grok hidden");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests compact token unit thresholds.
    /// </summary>
    private static Task TestTokenUnitFormattingAsync()
    {
        AssertEqual("999.00K", AppSettings.FormatTokenCount(999_000, AppSettings.TokenUnitEnglish), "English K unit");
        AssertEqual("1.00M", AppSettings.FormatTokenCount(1_000_000, AppSettings.TokenUnitEnglish), "English M lower boundary");
        AssertEqual("999.00M", AppSettings.FormatTokenCount(999_000_000, AppSettings.TokenUnitEnglish), "English M upper range");
        AssertEqual("1.00B", AppSettings.FormatTokenCount(1_000_000_000, AppSettings.TokenUnitEnglish), "English B boundary");
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
            VisiblePages = PageItem.Codex | PageItem.Cursor | PageItem.Apis,
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
        AssertEqual("deepseek-secret-token", deepSeek.ApiKey, "saved API key");
        AssertEqual("Personal DeepSeek", deepSeek.Name, "API monitor name");
        AssertEqual("https://openrouter.example", openRouter.BaseUrl, "saved OpenRouter base URL");
        AssertEqual("openrouter-management-key", openRouter.ApiKey, "saved OpenRouter management key");
        AssertEqual("https://nano-gpt.example", nanoGpt.BaseUrl, "saved NanoGPT base URL");
        AssertEqual("nanogpt-key", nanoGpt.ApiKey, "saved NanoGPT API key");
        AssertEqual(3, loaded.ApiMonitors.Count, "Cursor and legacy Grok API monitors should be removed");
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

        IReadOnlyList<ApiUsageResult> results = await collector.CollectAsync([deepSeek, newApi, openRouter, openRouterRoot, nanoGpt, nanoGptUsageFailure]);

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
        AssertEqual(string.Empty, results[5].UsedDisplay, "NanoGPT usage failure display");
    }

    /// <summary>
    /// Tests protobuf parsing for Grok's consumed percentage and reset timestamp.
    /// </summary>
    private static Task TestGrokUsageCollectorAsync()
    {
        const long resetAt = 1_802_592_000;
        byte[] response = CreateGrokBillingResponse(42.5f, resetAt, "Premium support");
        GrokUsageSnapshot snapshot = GrokUsageCollector.ParseGrpcWebResponse(response, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));

        AssertEqual(42.5, snapshot.UsedPercent, "Grok used percentage");
        AssertEqual(resetAt, snapshot.ResetsAt, "Grok reset timestamp");
        AssertEqual(string.Empty, snapshot.SubscriptionTier, "unconfirmed protobuf strings should not become a Grok subscription tier");
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
                CreateGrokBillingResponse(12.5f, 1_802_592_000)));
            GrokUsageCollector collector = new(client);
            GrokUsageSnapshot snapshot = await collector.CollectAsync();

            AssertEqual(12.5, snapshot.UsedPercent, "refreshed Grok Build used percentage");
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
    /// Tests Cursor usage-summary parsing for consumed percentage and billing cycle end.
    /// </summary>
    private static Task TestCursorUsageCollectorAsync()
    {
        const long resetAt = 1_802_592_000;
        string json = CreateCursorUsageSummaryJson(10, 12, 0, DateTimeOffset.FromUnixTimeSeconds(resetAt));
        CursorUsageSnapshot snapshot = CursorUsageCollector.ParseUsageSummary(
            json,
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));

        AssertEqual("pro_plus", snapshot.PlanType, "Cursor plan type");
        AssertEqual(10, snapshot.MonthlyUsedPercent, "Cursor monthly used percentage");
        AssertEqual(12, snapshot.AutoUsedPercent, "Cursor first-party used percentage");
        AssertEqual(0, snapshot.ApiUsedPercent, "Cursor API used percentage");
        AssertEqual(resetAt, snapshot.ResetsAt, "Cursor reset timestamp");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests that an expired Cursor token is refreshed and written back to state.vscdb.
    /// </summary>
    private static async Task TestCursorOAuthRefreshAsync()
    {
        using TempDirectory temp = new();
        string previousDb = Environment.GetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB") ?? string.Empty;
        string dbPath = Path.Combine(temp.Path, "state.vscdb");
        Environment.SetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB", dbPath);
        try
        {
            string oldAccess = CreateTestJwt("user_cursor_test", DateTimeOffset.UtcNow.AddMinutes(-10));
            string newAccess = CreateTestJwt("user_cursor_test", DateTimeOffset.UtcNow.AddHours(1));
            CreateCursorStateDatabase(dbPath, oldAccess, "cursor-refresh");

            string usageJson = CreateCursorUsageSummaryJson(12.5, 20, 5, DateTimeOffset.FromUnixTimeSeconds(1_802_592_000));
            using HttpClient client = new(new CursorOAuthRefreshHttpMessageHandler(
                oldAccess,
                newAccess,
                "cursor-refresh",
                "rotated-cursor-refresh",
                usageJson));
            CursorUsageCollector collector = new(client);
            CursorUsageSnapshot snapshot = await collector.CollectAsync();

            AssertEqual(12.5, snapshot.MonthlyUsedPercent, "refreshed Cursor monthly used percentage");
            AssertEqual(20, snapshot.AutoUsedPercent, "refreshed Cursor first-party used percentage");
            AssertEqual(5, snapshot.ApiUsedPercent, "refreshed Cursor API used percentage");
            AssertEqual(1_802_592_000, snapshot.ResetsAt, "refreshed Cursor reset timestamp");

            string? writtenAccess;
            string? writtenRefresh;
            using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                connection.Open();
                writtenAccess = ReadCursorStateValue(connection, "cursorAuth/accessToken");
                writtenRefresh = ReadCursorStateValue(connection, "cursorAuth/refreshToken");
            }

            SqliteConnection.ClearAllPools();
            AssertEqual(newAccess, writtenAccess, "Cursor access token write-back");
            AssertEqual("rotated-cursor-refresh", writtenRefresh, "Cursor refresh token write-back");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB", string.IsNullOrEmpty(previousDb) ? null : previousDb);
        }
    }

    /// <summary>
    /// Tests complete paged Cursor token-cost aggregation from actual event cents.
    /// </summary>
    private static async Task TestCursorDashboardAsync()
    {
        using TempDirectory temp = new();
        string previousDb = Environment.GetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB") ?? string.Empty;
        string dbPath = Path.Combine(temp.Path, "state.vscdb");
        Environment.SetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB", dbPath);
        try
        {
            DateTimeOffset now = new(2026, 7, 15, 12, 0, 0, TimeSpan.FromHours(8));
            string access = CreateTestJwt("user_cursor_test", DateTimeOffset.UtcNow.AddHours(1));
            CreateCursorStateDatabase(dbPath, access, "cursor-refresh");
            DateTimeOffset weekStart = now.Date.AddDays(-((int)now.DayOfWeek + 6) % 7).AddHours(12);
            object today = CreateCursorUsageEvent(now, "100", 10, "20", 30, "125", 999);
            object yesterday = CreateCursorUsageEvent(now.AddDays(-1), 1, 1, 1, 1, 10, 999);
            object week = CreateCursorUsageEvent(weekStart, 2, 2, 2, 2, 20, 999);
            object nonToken = CreateCursorUsageEvent(now.AddDays(-2), null, null, null, null, null, 999);
            object sevenDay = CreateCursorUsageEvent(now.AddDays(-6), 5, 5, 5, 5, 30, 999);
            object thirtyDay = CreateCursorUsageEvent(now.AddDays(-29), 6, 6, 6, 6, 40, 999);
            object historical = CreateCursorUsageEvent(now.AddDays(-30), 7, 7, 7, 7, 50, 999);
            using HttpClient client = new(new CursorDashboardHttpMessageHandler(
                CreateCursorUsageSummaryJson(10, 12, 0, DateTimeOffset.UtcNow.AddDays(30)),
                page => page switch
                {
                    1 => JsonResponse(CreateCursorUsageEventsJson(7, today, yesterday, week, nonToken)),
                    2 => JsonResponse(CreateCursorUsageEventsJson(7, sevenDay, thirtyDay, historical)),
                    _ => throw new InvalidOperationException("unexpected usage-events page"),
                }));
            CursorUsageDashboard dashboard = await new CursorUsageCollector(client).CollectDashboardAsync(now);

            AssertTrue(dashboard.Usage != null, "Cursor usage should be available");
            AssertTrue(dashboard.TokenCost != null, "Cursor token cost should be available");
            AssertEqual(false, dashboard.InitialCredentialRefreshUsed, "Cursor dashboard should not refresh a valid initial token");
            AssertEqual(false, dashboard.ForcedCredentialRefreshUsed, "Cursor dashboard should not force refresh a valid token");
            AssertEqual(7, dashboard.TokenCostDiagnostics!.EventCount, "Cursor diagnostics event count");
            AssertEqual(2, dashboard.TokenCostDiagnostics.PageCount, "Cursor diagnostics page count");
            AssertEqual(6, dashboard.TokenCostDiagnostics.TokenEventCount, "Cursor diagnostics token event count");
            AssertEqual(6, dashboard.TokenCostDiagnostics.CacheReadTokenFieldCount, "Cursor diagnostics cache read coverage");
            AssertEqual(6, dashboard.TokenCostDiagnostics.CacheWriteTokenFieldCount, "Cursor diagnostics cache write coverage");
            AssertEqual(160L, dashboard.TokenCost!.Today.TotalTokens, "Cursor cache tokens should contribute to total tokens");
            AssertEqual(1.25m, dashboard.TokenCost.Today.CostUsd, "Cursor cost must use totalCents instead of chargedCents");
            AssertEqual(192L, dashboard.TokenCost.LastSevenDays.TotalTokens, "Cursor last 7 days boundary");
            AssertEqual(216L, dashboard.TokenCost.LastThirtyDays.TotalTokens, "Cursor last 30 days boundary");
            AssertEqual(244L, dashboard.TokenCost.Lifetime.TotalTokens, "Cursor lifetime boundary");
            AssertEqual(2.75m, dashboard.TokenCost.Lifetime.CostUsd, "Cursor lifetime event cents");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB", string.IsNullOrEmpty(previousDb) ? null : previousDb);
        }
    }

    /// <summary>
    /// Tests safe totalCents error classification and empty-token event handling.
    /// </summary>
    private static async Task TestCursorTotalCentsValidationAsync()
    {
        using TempDirectory temp = new();
        string previousDb = Environment.GetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB") ?? string.Empty;
        string dbPath = Path.Combine(temp.Path, "state.vscdb");
        Environment.SetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB", dbPath);
        try
        {
            DateTimeOffset now = new(2026, 7, 15, 12, 0, 0, TimeSpan.FromHours(8));
            string access = CreateTestJwt("user_cursor_test", DateTimeOffset.UtcNow.AddHours(1));
            CreateCursorStateDatabase(dbPath, access, "cursor-refresh");

            CursorUsageDashboard missing = await CollectCursorEventCostFixtureAsync(
                now,
                CreateCursorTokenUsageEvent(now, 1, 0, 0, 0, includeTotalCents: false));
            AssertEqual("Cursor usage event totalCents is missing.", missing.TokenCostError, "missing totalCents error");

            CursorUsageDashboard nullValue = await CollectCursorEventCostFixtureAsync(
                now,
                CreateCursorTokenUsageEvent(now, 1, 0, 0, 0, includeTotalCents: true, totalCents: null));
            AssertEqual("Cursor usage event totalCents is null.", nullValue.TokenCostError, "null totalCents error");

            CursorUsageDashboard nonNumeric = await CollectCursorEventCostFixtureAsync(
                now,
                CreateCursorTokenUsageEvent(now, 1, 0, 0, 0, includeTotalCents: true, totalCents: "not-a-number"));
            AssertEqual("Cursor usage event totalCents is a nonnumeric string.", nonNumeric.TokenCostError, "nonnumeric totalCents error");

            CursorUsageDashboard negative = await CollectCursorEventCostFixtureAsync(
                now,
                CreateCursorTokenUsageEvent(now, 1, 0, 0, 0, includeTotalCents: true, totalCents: -1));
            AssertEqual("Cursor usage event totalCents is negative.", negative.TokenCostError, "negative totalCents error");

            CursorUsageDashboard nonFinite = await CollectCursorEventCostFixtureAsync(
                now,
                CreateCursorTokenUsageEvent(now, 1, 0, 0, 0, includeTotalCents: true, totalCents: "NaN"));
            AssertEqual("Cursor usage event totalCents is nonfinite.", nonFinite.TokenCostError, "nonfinite totalCents error");

            CursorUsageDashboard zeroTokenMissingCost = await CollectCursorEventCostFixtureAsync(
                now,
                CreateCursorTokenUsageEvent(now, 0, 0, 0, 0, includeTotalCents: false));
            AssertTrue(zeroTokenMissingCost.TokenCost != null, "zero-token event without cost should not clear token cost");
            AssertEqual(0L, zeroTokenMissingCost.TokenCost!.Lifetime.TotalTokens, "zero-token event lifetime");
            AssertEqual(0m, zeroTokenMissingCost.TokenCost.Lifetime.CostUsd, "zero-token event cost");
            AssertEqual(1, zeroTokenMissingCost.TokenCostDiagnostics!.ZeroTokenMissingCostEventCount, "zero-token missing-cost diagnostics");
            AssertEqual(0, zeroTokenMissingCost.TokenCostDiagnostics.TokenEventCount, "zero-token missing-cost should not count as a token event");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB", string.IsNullOrEmpty(previousDb) ? null : previousDb);
        }
    }

    /// <summary>
    /// Tests independent dashboard failure regions and atomic token-cost paging failure.
    /// </summary>
    private static async Task TestCursorDashboardFailuresAsync()
    {
        using TempDirectory temp = new();
        string previousDb = Environment.GetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB") ?? string.Empty;
        string dbPath = Path.Combine(temp.Path, "state.vscdb");
        Environment.SetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB", dbPath);
        try
        {
            DateTimeOffset now = new(2026, 7, 15, 12, 0, 0, TimeSpan.FromHours(8));
            string access = CreateTestJwt("user_cursor_test", DateTimeOffset.UtcNow.AddHours(1));
            CreateCursorStateDatabase(dbPath, access, "cursor-refresh");
            object eventRecord = CreateCursorUsageEvent(now, 1, 1, 1, 1, 1, 1);
            using HttpClient usageFailureClient = new(new CursorDashboardHttpMessageHandler(
                CreateCursorUsageSummaryJson(10, 10, 10, DateTimeOffset.UtcNow.AddDays(30)),
                _ => JsonResponse(CreateCursorUsageEventsJson(1, eventRecord)),
                usageStatus: HttpStatusCode.InternalServerError));
            CursorUsageDashboard usageFailure = await new CursorUsageCollector(usageFailureClient).CollectDashboardAsync(now);
            AssertTrue(usageFailure.Usage == null, "failed usage-summary must clear usage region");
            AssertTrue(usageFailure.TokenCost != null, "usage-events should remain available after usage-summary failure");

            using HttpClient pagingFailureClient = new(new CursorDashboardHttpMessageHandler(
                CreateCursorUsageSummaryJson(10, 10, 10, DateTimeOffset.UtcNow.AddDays(30)),
                page => page == 1
                    ? JsonResponse(CreateCursorUsageEventsJson(2, eventRecord))
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError)));
            CursorUsageDashboard pagingFailure = await new CursorUsageCollector(pagingFailureClient).CollectDashboardAsync(now);
            AssertTrue(pagingFailure.Usage != null, "successful usage-summary must survive usage-events failure");
            AssertTrue(pagingFailure.TokenCost == null, "incomplete usage-events paging must not return partial token cost");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB", string.IsNullOrEmpty(previousDb) ? null : previousDb);
        }
    }

    /// <summary>
    /// Tests one forced OAuth refresh shared by Cursor usage-summary and usage-events.
    /// </summary>
    private static async Task TestCursorDashboardRefreshBudgetAsync()
    {
        using TempDirectory temp = new();
        string previousDb = Environment.GetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB") ?? string.Empty;
        string dbPath = Path.Combine(temp.Path, "state.vscdb");
        Environment.SetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB", dbPath);
        try
        {
            DateTimeOffset now = new(2026, 7, 15, 12, 0, 0, TimeSpan.FromHours(8));
            string oldAccess = CreateTestJwt("user_cursor_test", DateTimeOffset.UtcNow.AddHours(1));
            string newAccess = CreateTestJwt("user_cursor_test", DateTimeOffset.UtcNow.AddHours(2));
            CreateCursorStateDatabase(dbPath, oldAccess, "cursor-refresh");
            CursorDashboardHttpMessageHandler handler = new(
                CreateCursorUsageSummaryJson(10, 10, 10, DateTimeOffset.UtcNow.AddDays(30)),
                _ => JsonResponse(CreateCursorUsageEventsJson(0)),
                oldAccess,
                newAccess,
                "cursor-refresh");
            using HttpClient client = new(handler);
            CursorUsageDashboard dashboard = await new CursorUsageCollector(client).CollectDashboardAsync(now);

            AssertTrue(dashboard.Usage != null && dashboard.TokenCost != null, "refreshed Cursor dashboard should be available");
            AssertEqual(1, handler.RefreshCount, "Cursor dashboard should use one shared refresh budget");
            AssertEqual(false, dashboard.InitialCredentialRefreshUsed, "Cursor dashboard forced refresh should begin with a valid token");
            AssertEqual(true, dashboard.ForcedCredentialRefreshUsed, "Cursor dashboard should report its forced refresh");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("CODEXTRAY_CURSOR_STATE_VSCDB", string.IsNullOrEmpty(previousDb) ? null : previousDb);
        }
    }

    /// <summary>
    /// Tests that the local pricing resource includes supported Codex and Grok models.
    /// </summary>
    private static Task TestPublishedModelPricingAsync()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine("Resources", "model-pricing.json")));
        JsonElement pricing = document.RootElement.GetProperty("gpt-5.3-codex");
        AssertEqual(1.75m, pricing.GetProperty("input").GetDecimal(), "gpt-5.3-codex input price");
        AssertEqual(0.175m, pricing.GetProperty("cachedInput").GetDecimal(), "gpt-5.3-codex cached input price");
        AssertEqual(14.0m, pricing.GetProperty("output").GetDecimal(), "gpt-5.3-codex output price");
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
        JsonElement deepSeekFlashPricing = document.RootElement.GetProperty("deepseek-v4-flash");
        AssertEqual(0.14m, deepSeekFlashPricing.GetProperty("input").GetDecimal(), "deepseek-v4-flash input price");
        AssertEqual(0.0028m, deepSeekFlashPricing.GetProperty("cachedInput").GetDecimal(), "deepseek-v4-flash cached input price");
        AssertEqual(0.28m, deepSeekFlashPricing.GetProperty("output").GetDecimal(), "deepseek-v4-flash output price");
        JsonElement deepSeekPricing = document.RootElement.GetProperty("deepseek-v4-pro");
        AssertEqual(0.435m, deepSeekPricing.GetProperty("input").GetDecimal(), "deepseek-v4-pro input price");
        AssertEqual(0.003625m, deepSeekPricing.GetProperty("cachedInput").GetDecimal(), "deepseek-v4-pro cached input price");
        AssertEqual(0.87m, deepSeekPricing.GetProperty("output").GetDecimal(), "deepseek-v4-pro output price");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds a minimal gRPC-web billing body for Grok collector tests.
    /// </summary>
    private static byte[] CreateGrokBillingResponse(float usedPercent, long resetAt, string unrelatedText = "")
    {
        int bits = BitConverter.SingleToInt32Bits(usedPercent);
        List<byte> payload =
        [
            0x0D,
            (byte)bits,
            (byte)(bits >> 8),
            (byte)(bits >> 16),
            (byte)(bits >> 24),
            0x28,
        ];
        payload.AddRange(EncodeVarint(resetAt));
        if (unrelatedText.Length > 0)
        {
            byte[] tier = Encoding.UTF8.GetBytes(unrelatedText);
            payload.Add(0x7A);
            payload.AddRange(EncodeVarint(tier.Length));
            payload.AddRange(tier);
        }

        List<byte> response = [0, 0, 0, 0, (byte)payload.Count];
        response.AddRange(payload);
        return [.. response];
    }

    /// <summary>
    /// Creates a compact token-statistics fixture for popup layout checks.
    /// </summary>
    private static TokenCostStatistics CreateTokenCostStatistics(long multiplier)
    {
        DateTime today = DateTime.Today;
        return new TokenCostStatistics
        {
            Today = new TokenCostSummary { TotalTokens = multiplier * 100 },
            LastSevenDays = new TokenCostSummary { TotalTokens = multiplier * 700 },
            LastThirtyDays = new TokenCostSummary { TotalTokens = multiplier * 3_000 },
            Lifetime = new TokenCostSummary { TotalTokens = multiplier * 10_000 },
            LastSevenDaysDaily = Enumerable.Range(0, 7)
                .Select(index => new TokenCostDailySummary
                {
                    Date = today.AddDays(index - 6),
                    Summary = new TokenCostSummary { TotalTokens = multiplier * (index + 1) },
                })
                .ToArray(),
        };
    }

    /// <summary>
    /// Builds one Grok Build turn-completed usage entry with per-model counters.
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
        long reasoningTokens = 0)
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
                                costUsdTicks,
                            },
                        },
                    },
                },
            },
        });
    }

    /// <summary>
    /// Builds one Grok Build turn-completed usage entry without modelUsage details.
    /// </summary>
    private static string CreateGrokTopLevelTokenUpdate(string promptId, DateTimeOffset timestamp, long inputTokens, long cachedReadTokens, long outputTokens, long costUsdTicks)
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
                    },
                },
            },
        });
    }

    /// <summary>
    /// Builds a redacted Cursor usage-summary fixture for parser tests.
    /// </summary>
    private static string CreateCursorUsageSummaryJson(
        double monthlyUsedPercent,
        double firstPartyUsedPercent,
        double apiUsedPercent,
        DateTimeOffset billingCycleEnd)
    {
        return JsonSerializer.Serialize(new
        {
            billingCycleEnd = billingCycleEnd.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            membershipType = "pro_plus",
            individualUsage = new
            {
                plan = new
                {
                    totalPercentUsed = monthlyUsedPercent,
                    autoPercentUsed = firstPartyUsedPercent,
                    apiPercentUsed = apiUsedPercent,
                },
            },
        });
    }

    /// <summary>
    /// Builds one Cursor usage-events page fixture.
    /// </summary>
    private static string CreateCursorUsageEventsJson(int totalCount, params object[] displays)
    {
        return JsonSerializer.Serialize(new
        {
            totalUsageEventsCount = totalCount,
            usageEventsDisplay = displays,
        });
    }

    /// <summary>
    /// Collects a one-event Cursor token-cost fixture with a valid usage-summary response.
    /// </summary>
    private static async Task<CursorUsageDashboard> CollectCursorEventCostFixtureAsync(DateTimeOffset now, object usageEvent)
    {
        using HttpClient client = new(new CursorDashboardHttpMessageHandler(
            CreateCursorUsageSummaryJson(10, 10, 10, DateTimeOffset.UtcNow.AddDays(30)),
            _ => JsonResponse(CreateCursorUsageEventsJson(1, usageEvent))));
        return await new CursorUsageCollector(client).CollectDashboardAsync(now);
    }

    /// <summary>
    /// Builds one Cursor usage event fixture with optional token counters.
    /// </summary>
    private static object CreateCursorUsageEvent(
        DateTimeOffset timestamp,
        object? inputTokens,
        object? outputTokens,
        object? cacheReadTokens,
        object? cacheWriteTokens,
        object? totalCents,
        object chargedCents)
    {
        Dictionary<string, object?> display = new()
        {
            ["timestamp"] = timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["model"] = "gpt-5.3-codex",
            ["chargedCents"] = chargedCents,
        };
        if (totalCents != null)
        {
            display["tokenUsage"] = new Dictionary<string, object?>
            {
                ["inputTokens"] = inputTokens,
                ["outputTokens"] = outputTokens,
                ["cacheReadTokens"] = cacheReadTokens,
                ["cacheWriteTokens"] = cacheWriteTokens,
                ["totalCents"] = totalCents,
            };
        }

        return display;
    }

    /// <summary>
    /// Builds one Cursor event fixture with tokenUsage present and configurable totalCents presence.
    /// </summary>
    private static object CreateCursorTokenUsageEvent(
        DateTimeOffset timestamp,
        object inputTokens,
        object outputTokens,
        object cacheReadTokens,
        object cacheWriteTokens,
        bool includeTotalCents,
        object? totalCents = null)
    {
        Dictionary<string, object?> tokenUsage = new()
        {
            ["inputTokens"] = inputTokens,
            ["outputTokens"] = outputTokens,
            ["cacheReadTokens"] = cacheReadTokens,
            ["cacheWriteTokens"] = cacheWriteTokens,
        };
        if (includeTotalCents)
        {
            tokenUsage["totalCents"] = totalCents;
        }

        return new Dictionary<string, object?>
        {
            ["timestamp"] = timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["model"] = "gpt-test",
            ["tokenUsage"] = tokenUsage,
        };
    }

    /// <summary>
    /// Creates a JSON HTTP response for one fake Cursor endpoint request.
    /// </summary>
    private static HttpResponseMessage JsonResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    /// Builds an unsigned JWT carrying only the claims CursorUsageCollector reads.
    /// </summary>
    private static string CreateTestJwt(string subject, DateTimeOffset expiresAt)
    {
        static string Encode(object value)
        {
            string json = JsonSerializer.Serialize(value);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        return $"{Encode(new { alg = "none", typ = "JWT" })}.{Encode(new { sub = subject, exp = expiresAt.ToUnixTimeSeconds() })}.sig";
    }

    /// <summary>
    /// Creates a minimal Cursor state.vscdb with access and refresh tokens.
    /// </summary>
    private static void CreateCursorStateDatabase(string dbPath, string accessToken, string refreshToken)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        connection.Open();
        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);";
            create.ExecuteNonQuery();
        }

        UpsertCursorStateValue(connection, "cursorAuth/accessToken", accessToken);
        UpsertCursorStateValue(connection, "cursorAuth/refreshToken", refreshToken);
        UpsertCursorStateValue(connection, "unrelated/keep", "keep-me");
    }

    /// <summary>
    /// Inserts or replaces one Cursor state.vscdb ItemTable value for tests.
    /// </summary>
    private static void UpsertCursorStateValue(SqliteConnection connection, string key, string value)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads one Cursor state.vscdb ItemTable value for assertions.
    /// </summary>
    private static string? ReadCursorStateValue(SqliteConnection connection, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        object? value = command.ExecuteScalar();
        return value switch
        {
            null or DBNull => null,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Encodes a positive integer in protobuf varint form.
    /// </summary>
    private static byte[] EncodeVarint(long value)
    {
        List<byte> bytes = [];
        ulong remaining = (ulong)value;
        do
        {
            byte next = (byte)(remaining & 0x7F);
            remaining >>= 7;
            bytes.Add(remaining == 0 ? next : (byte)(next | 0x80));
        }
        while (remaining != 0);

        return [.. bytes];
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
    /// Verifies the compact token-cost rows and rolling chart display data.
    /// </summary>
    private static Task TestTokenCostChartViewModelAsync()
    {
        TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
        DateTime firstDate = new(2026, 8, 1);
        TokenCostDailySummary[] daily = Enumerable.Range(0, 7)
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
            Today = new TokenCostSummary { CostUsd = 7 },
            LastSevenDays = new TokenCostSummary { CostUsd = 13 },
            LastThirtyDays = new TokenCostSummary { CostUsd = 30 },
            Lifetime = new TokenCostSummary { CostUsd = 100 },
            LastSevenDaysDaily = daily,
        };
        viewModel.UpdateTokenCost(statistics);

        AssertEqual("Today|7d|30d|Lifetime", string.Join('|', viewModel.CodexTokenCostRows.Select(row => row.Title)), "Codex token cost row titles");
        AssertEqual("$7.00|$13.00|$30.00|$100.00", string.Join('|', viewModel.CodexTokenCostRows.Select(row => row.Display.Cost)), "Codex token cost row values");
        AssertEqual(7, viewModel.CodexTokenCostChartDays.Count, "Codex token cost chart day count");
        AssertEqual("F", viewModel.CodexTokenCostChartDays[6].Label, "Codex token cost chart today label");
        AssertEqual(96d, viewModel.CodexTokenCostChartDays[6].BarHeight, "Codex token cost chart maximum height");
        AssertEqual($"2026-08-07{Environment.NewLine}Tokens: 0.70K{Environment.NewLine}Cost: $7.00", viewModel.CodexTokenCostChartDays[6].Tooltip, "Codex token cost chart tooltip");

        viewModel.UpdateCursorDashboard(new CursorUsageDashboard(null, statistics, "N/A", string.Empty, DateTimeOffset.Now));
        AssertEqual("Today|7d|30d|Lifetime", string.Join('|', viewModel.CursorTokenCostRows.Select(row => row.Title)), "Cursor token cost row titles");
        AssertEqual("$7.00|$13.00|$30.00|$100.00", string.Join('|', viewModel.CursorTokenCostRows.Select(row => row.Display.Cost)), "Cursor token cost row values");
        AssertEqual(7, viewModel.CursorTokenCostChartDays.Count, "Cursor token cost chart day count");
        AssertEqual(viewModel.CodexTokenCostChartDays[6].Tooltip, viewModel.CursorTokenCostChartDays[6].Tooltip, "Cursor token cost chart tooltip");

        TokenCostStatistics grokStatistics = new()
        {
            Today = new TokenCostSummary { TotalTokens = 700 },
            LastSevenDays = new TokenCostSummary { TotalTokens = 2_800 },
            LastThirtyDays = new TokenCostSummary { TotalTokens = 2_800 },
            Lifetime = new TokenCostSummary { TotalTokens = 2_800 },
            LastSevenDaysDaily = daily
                .Select(day => new TokenCostDailySummary
                {
                    Date = day.Date,
                    Summary = new TokenCostSummary { TotalTokens = day.Summary.TotalTokens },
                })
                .ToArray(),
        };
        viewModel.UpdateGrokDashboard(new GrokUsageDashboard(null, "N/A", DateTimeOffset.Now), grokStatistics);
        AssertEqual("Today|7d|30d|Lifetime", string.Join('|', viewModel.GrokTokenCostRows.Select(row => row.Title)), "Grok token cost row titles");
        AssertEqual("N/A|N/A|N/A|N/A", string.Join('|', viewModel.GrokTokenCostRows.Select(row => row.Display.Cost)), "Grok unknown cost values");
        AssertEqual(96d, viewModel.GrokTokenCostChartDays[6].BarHeight, "Grok token chart maximum height");
        AssertEqual($"2026-08-07{Environment.NewLine}Tokens: 0.70K{Environment.NewLine}Cost: N/A", viewModel.GrokTokenCostChartDays[6].Tooltip, "Grok token chart tooltip");

        viewModel.UpdateTokenCost(null);
        AssertEqual(7, viewModel.CodexTokenCostChartDays.Count, "unavailable Codex token cost chart day count");
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
            (nameof(TrayPopupViewModel.ShowResetTimeInPlugins),
             viewModel => viewModel.ShowResetTimeInPlugins = !CodexTrayDefaults.ShowResetTimeInPlugins,
             viewModel => viewModel.ShowResetTimeInPlugins = CodexTrayDefaults.ShowResetTimeInPlugins),
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
            "BalanceTooltip|HasBalanceTooltip",
            string.Join('|', changedProperties.Order()),
            "balance tooltip notifications");
        changedProperties.Clear();
        viewModel.Update(result);
        AssertEqual(0, changedProperties.Count, "unchanged balance tooltip notifications");

        AssertTrue(viewModel.ProviderOptions.Contains(ApiMonitorSettings.OpenRouterProvider), "OpenRouter provider option");
        AssertTrue(viewModel.ProviderOptions.Contains(ApiMonitorSettings.NanoGptProvider), "NanoGPT provider option");
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
        viewModel.Update(new ApiUsageResult(viewModel.Id, true, "$12.35", string.Empty, string.Empty, DateTimeOffset.UtcNow));
        AssertTrue(!viewModel.HasSecondaryDisplay, "NanoGPT failed usage should hide secondary display");
        AssertTrue(changedProperties.Contains(nameof(ApiMonitorViewModel.HasSecondaryDisplay)), "NanoGPT secondary visibility notification");
        viewModel.BaseUrl = "https://custom.example";
        viewModel.Provider = ApiMonitorSettings.DeepSeekProvider;
        AssertEqual("https://custom.example", viewModel.BaseUrl, "custom base URL should be preserved");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests that a weekly window in the primary slot is not mistaken for a five hour window.
    /// </summary>
    private static Task TestLoneWeeklyQuotaAsync()
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
        CodexTrayCollector collector = new(() => now, client);

        UsageResponse response = collector.Collect(temp.Path);

        AssertTrue(response.Available, "response should be available");
        AssertEqual(0, response.Limits.Session.WindowMinutes, "session window should be absent");
        AssertEqual("N/A", response.Display.Session, "session display should be unavailable");
        AssertEqual(10080, response.Limits.Weekly.WindowMinutes, "weekly window duration");
        AssertEqual(42, response.Limits.Weekly.RemainingPercent, "weekly remaining percent");
        AssertEqual("42% 6d23h", response.Display.Weekly, "weekly display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests reset credit parsing and local expiry conversion.
    /// </summary>
    private static Task TestResetCreditsAsync()
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
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, now.AddHours(1).ToUnixTimeSeconds(), now.AddDays(2).ToUnixTimeSeconds(), 10.0, 20.0, out HttpClient client, resetCreditsBody);
        using HttpClient _ = client;

        UsageResponse response = collector.Collect(temp.Path);

        AssertTrue(response.ResetCredits.Available, "reset credits should be available");
        AssertEqual(3, response.ResetCredits.AvailableCount, "reset credit count");
        AssertEqual(nearestExpiry.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), response.ResetCredits.NearestExpiryLocal, "nearest local reset credit expiry");
        AssertEqual(
            $"{nearestExpiry.AddDays(1).ToLocalTime():MM-dd} · {nearestExpiry.AddDays(2).ToLocalTime():MM-dd}",
            response.ResetCredits.OtherExpiriesLocal,
            "other local reset credit expiries");
        return Task.CompletedTask;
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
            quota.Update(limit, hideInvalidProgressBars: false);
            System.Windows.Media.Color actualColor = ((System.Windows.Media.SolidColorBrush)quota.AccentBrush).Color;
            AssertEqual(expectedColor, actualColor, $"quota color at {remaining}%");
        }

        limit.WindowMinutes = 0;
        quota.Update(limit, hideInvalidProgressBars: false);
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
        AssertTrue(!viewModel.HasResetCredits, "zero reset credits should use the inactive color");

        response.ResetCredits.AvailableCount = 1;
        viewModel.UpdateStatus(isRunning: true, CodexTrayDefaults.Port, response, error: null);
        AssertTrue(viewModel.HasResetCredits, "positive reset credits should use the active color");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies cumulative token deltas and cached input pricing.
    /// </summary>
    private static Task TestTokenCostCollectorAsync()
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
            "{\"timestamp\":\"2026-07-10T10:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":500,\"cached_input_tokens\":100,\"output_tokens\":50}}}}",
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

        using FileStream activeWriter = new(sessionPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        TokenCostCollector collector = new(pricingPath);
        File.WriteAllLines(Path.Combine(sessions, "future.jsonl"),
        [
            "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}",
            "{\"timestamp\":\"2026-07-12T10:00:00+08:00\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":10000,\"cached_input_tokens\":0,\"output_tokens\":0}}}}",
        ]);

        string missingOpenCode = Path.Combine(temp.Path, "missing-opencode");
        TokenCostSummary summary = collector.Collect(temp.Path, new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8)), missingOpenCode).Today;
        AssertEqual(2900L, summary.TotalTokens, "today total tokens");
        AssertEqual(0.0062m, summary.CostUsd, "today API-equivalent cost");
        TokenCostStatistics statistics = collector.Collect(temp.Path, new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8)), missingOpenCode);
        AssertEqual(3520L, statistics.LastSevenDays.TotalTokens, "last 7 days total tokens");
        AssertEqual(3560L, statistics.LastThirtyDays.TotalTokens, "last 30 days total tokens");
        AssertEqual(0.00774m, statistics.LastThirtyDays.CostUsd, "last 30 days API-equivalent cost");
        AssertEqual(3660L, statistics.Lifetime.TotalTokens, "lifetime tokens");
        AssertEqual(0.00794m, statistics.Lifetime.CostUsd, "lifetime API-equivalent cost");
        AssertEqual(7, statistics.LastSevenDaysDaily.Count, "daily chart slot count");
        AssertEqual(new DateTime(2026, 7, 5), statistics.LastSevenDaysDaily[0].Date, "daily chart first date");
        AssertEqual(70L, statistics.LastSevenDaysDaily[0].Summary.TotalTokens, "daily chart first tokens");
        AssertEqual(550L, statistics.LastSevenDaysDaily[5].Summary.TotalTokens, "daily chart yesterday tokens");
        AssertEqual(new DateTime(2026, 7, 11), statistics.LastSevenDaysDaily[6].Date, "daily chart today date");
        AssertEqual(2900L, statistics.LastSevenDaysDaily[6].Summary.TotalTokens, "daily chart today tokens");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies that model aliases reuse the canonical pricing entry.
    /// </summary>
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
            .Collect(temp.Path, new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(8)), Path.Combine(temp.Path, "missing-opencode"))
            .Today;
        AssertEqual(1_050L, summary.TotalTokens, "alias pricing total tokens");
        AssertEqual(0.00213m, summary.CostUsd, "alias pricing cost");
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
        AssertEqual(2_831_472L, statistics.LastSevenDays.TotalTokens, "Grok last 7 days tokens");
        AssertEqual(2_831_472L, statistics.LastThirtyDays.TotalTokens, "Grok last 30 days tokens");
        AssertEqual(2_831_472L, statistics.Lifetime.TotalTokens, "Grok lifetime tokens");
        AssertEqual(0.8534805m, statistics.Today.CostUsd, "Grok reported, partial, aggregate, and alternate-model fallback cost");
        AssertEqual(0.8546555m, statistics.Lifetime.CostUsd, "Grok lifetime API-equivalent cost");
        AssertEqual(170L, statistics.LastSevenDaysDaily[5].Summary.TotalTokens, "Grok yesterday tokens");
        AssertEqual(2_831_302L, statistics.LastSevenDaysDaily[6].Summary.TotalTokens, "Grok daily today tokens");

        File.AppendAllLines(archivedPath, [CreateGrokTokenUpdate("prompt-unpriced", yesterday, 10, 0, 1).Replace("grok-4.5-build", "future-grok-model", StringComparison.Ordinal)]);
        TokenCostStatistics unpricedStatistics = new TokenCostCollector(pricingPath).CollectGrok(temp.Path, now);
        AssertEqual<decimal?>(null, unpricedStatistics.Lifetime.CostUsd, "unpriced Grok usage should make total cost unavailable");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies OpenCode OpenAI messages use cached and reasoning token pricing.
    /// </summary>
    private static Task TestOpenCodeTokenCostAsync()
    {
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{\"gpt-test\":{\"input\":2,\"cachedInput\":0.2,\"output\":10}}");
        string codexRoot = Path.Combine(temp.Path, "codex");
        string openCodeRoot = Path.Combine(temp.Path, "opencode");
        Directory.CreateDirectory(codexRoot);
        Directory.CreateDirectory(openCodeRoot);
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
            command.Parameters["$data"].Value = "{\"role\":\"assistant\",\"providerID\":\"deepseek\",\"modelID\":\"gpt-test\",\"tokens\":{\"input\":1000,\"output\":1000}}";
            command.ExecuteNonQuery();
        }

        TokenCostSummary summary = new TokenCostCollector(pricingPath)
            .Collect(codexRoot, new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8)), openCodeRoot)
            .Today;
        AssertEqual(1150L, summary.TotalTokens, "OpenCode total tokens");
        AssertEqual(0.00278m, summary.CostUsd, "OpenCode API-equivalent cost");
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
        TokenCostSummary lifetime = collector.Collect(
            temp.Path,
            new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8)),
            Path.Combine(temp.Path, "missing-opencode")).Lifetime;
        AssertEqual(2860L, lifetime.TotalTokens, "subagent lifetime excludes only matching replay prefix");
        AssertEqual(0.006m, lifetime.CostUsd, "subagent lifetime cost");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a collector backed by fake OAuth credentials and a fixed official quota response.
    /// </summary>
    private static CodexTrayCollector CreateOfficialCollector(string codexRoot, DateTimeOffset now, long sessionResetAt, long weeklyResetAt, double primaryUsed, double secondaryUsed, out HttpClient client, string? resetCreditsBody = null)
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
        return new CodexTrayCollector(() => now, client);
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
    private const string k_BillingEndpoint = "https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig";
    private const string k_TokenEndpoint = "https://auth.x.ai/oauth2/token";
    private readonly string m_OldAccessToken;
    private readonly string m_NewAccessToken;
    private readonly string m_ExpectedRefreshToken;
    private readonly string m_RotatedRefreshToken;
    private readonly byte[] m_BillingBody;

    /// <summary>
    /// Creates a handler that refreshes OAuth tokens and then serves billing usage.
    /// </summary>
    public GrokOAuthRefreshHttpMessageHandler(
        string oldAccessToken,
        string newAccessToken,
        string expectedRefreshToken,
        string rotatedRefreshToken,
        byte[] billingBody)
    {
        m_OldAccessToken = oldAccessToken;
        m_NewAccessToken = newAccessToken;
        m_ExpectedRefreshToken = expectedRefreshToken;
        m_RotatedRefreshToken = rotatedRefreshToken;
        m_BillingBody = billingBody;
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

        if (requestUri == k_BillingEndpoint)
        {
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
                Content = new ByteArrayContent(m_BillingBody),
            };
        }

        throw new InvalidOperationException($"unexpected Grok request URI: {requestUri}");
    }
}

internal sealed class CursorOAuthRefreshHttpMessageHandler : HttpMessageHandler
{
    private const string k_UsageEndpoint = "https://cursor.com/api/usage-summary";
    private const string k_TokenEndpoint = "https://api2.cursor.sh/oauth/token";
    private readonly string m_OldAccessToken;
    private readonly string m_NewAccessToken;
    private readonly string m_ExpectedRefreshToken;
    private readonly string m_RotatedRefreshToken;
    private readonly string m_UsageBody;

    /// <summary>
    /// Creates a handler that refreshes Cursor OAuth tokens and then serves usage-summary.
    /// </summary>
    public CursorOAuthRefreshHttpMessageHandler(
        string oldAccessToken,
        string newAccessToken,
        string expectedRefreshToken,
        string rotatedRefreshToken,
        string usageBody)
    {
        m_OldAccessToken = oldAccessToken;
        m_NewAccessToken = newAccessToken;
        m_ExpectedRefreshToken = expectedRefreshToken;
        m_RotatedRefreshToken = rotatedRefreshToken;
        m_UsageBody = usageBody;
    }

    /// <summary>
    /// Handles Cursor OAuth refresh and usage-summary requests for collector tests.
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
                throw new InvalidOperationException("missing Cursor refresh token");
            }

            if (!body.Contains("client_id=KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB", StringComparison.Ordinal) &&
                !body.Contains($"client_id={Uri.EscapeDataString("KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB")}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("missing Cursor client id");
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

        if (requestUri == k_UsageEndpoint)
        {
            if (!request.Headers.TryGetValues("Cookie", out IEnumerable<string>? cookies))
            {
                throw new InvalidOperationException("missing Cursor session cookie");
            }

            string cookie = string.Join(';', cookies);
            if (cookie.Contains(m_OldAccessToken, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (!cookie.Contains(m_NewAccessToken, StringComparison.Ordinal) ||
                !cookie.Contains("WorkosCursorSessionToken=user_cursor_test%3A%3A", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("unexpected Cursor session cookie");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(m_UsageBody, Encoding.UTF8, "application/json"),
            };
        }

        throw new InvalidOperationException($"unexpected Cursor request URI: {requestUri}");
    }
}

internal sealed class CursorDashboardHttpMessageHandler : HttpMessageHandler
{
    private const string k_UsageEndpoint = "https://cursor.com/api/usage-summary";
    private const string k_UsageEventsEndpoint = "https://cursor.com/api/dashboard/get-filtered-usage-events";
    private const string k_TokenEndpoint = "https://api2.cursor.sh/oauth/token";
    private readonly string m_UsageBody;
    private readonly Func<int, HttpResponseMessage> m_UsageEventsResponse;
    private readonly string? m_OldAccessToken;
    private readonly string? m_NewAccessToken;
    private readonly string? m_ExpectedRefreshToken;
    private readonly HttpStatusCode m_UsageStatus;

    public int RequestCount { get; private set; }

    public int RefreshCount { get; private set; }

    /// <summary>
    /// Creates a handler for Cursor dashboard requests with optional forced OAuth refresh.
    /// </summary>
    public CursorDashboardHttpMessageHandler(
        string usageBody,
        Func<int, HttpResponseMessage> usageEventsResponse,
        string? oldAccessToken = null,
        string? newAccessToken = null,
        string? expectedRefreshToken = null,
        HttpStatusCode usageStatus = HttpStatusCode.OK)
    {
        m_UsageBody = usageBody;
        m_UsageEventsResponse = usageEventsResponse;
        m_OldAccessToken = oldAccessToken;
        m_NewAccessToken = newAccessToken;
        m_ExpectedRefreshToken = expectedRefreshToken;
        m_UsageStatus = usageStatus;
    }

    /// <summary>
    /// Handles Cursor dashboard, OAuth refresh, and usage-events paging requests.
    /// </summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        string requestUri = request.RequestUri?.ToString() ?? string.Empty;
        if (requestUri == k_TokenEndpoint)
        {
            RefreshCount++;
            if (m_NewAccessToken == null || m_ExpectedRefreshToken == null)
            {
                throw new InvalidOperationException("unexpected Cursor OAuth refresh");
            }

            string body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!body.Contains($"refresh_token={Uri.EscapeDataString(m_ExpectedRefreshToken)}", StringComparison.Ordinal) &&
                !body.Contains($"refresh_token={m_ExpectedRefreshToken}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("missing Cursor dashboard refresh token");
            }

            return CreateJsonResponse(JsonSerializer.Serialize(new
            {
                access_token = m_NewAccessToken,
                refresh_token = "rotated-cursor-refresh",
                expires_in = 3600,
            }));
        }

        if (requestUri == k_UsageEndpoint)
        {
            if (ShouldRejectOldCookie(request))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (m_UsageStatus != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(m_UsageStatus);
            }

            ValidateNewCookie(request);
            return CreateJsonResponse(m_UsageBody);
        }

        if (requestUri == k_UsageEventsEndpoint)
        {
            if (ShouldRejectOldCookie(request))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (!request.Headers.TryGetValues("Origin", out IEnumerable<string>? origins) || origins.Single() != "https://cursor.com")
            {
                throw new InvalidOperationException("missing Cursor usage-events origin");
            }

            string body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            int page = document.RootElement.GetProperty("page").GetInt32();
            if (document.RootElement.GetProperty("pageSize").GetInt32() != 1000 ||
                document.RootElement.GetProperty("startDate").GetString() != "0" ||
                !long.TryParse(document.RootElement.GetProperty("endDate").GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long endDate) ||
                endDate <= 0 ||
                request.Content?.Headers.ContentType?.MediaType != "application/json" ||
                !request.Headers.Accept.Any(header => header.MediaType == "application/json"))
            {
                throw new InvalidOperationException("invalid Cursor usage-events pagination request");
            }
            ValidateNewCookie(request);
            return m_UsageEventsResponse(page);
        }

        throw new InvalidOperationException($"unexpected Cursor dashboard request URI: {requestUri}");
    }

    /// <summary>
    /// Returns whether the request still carries the initial access token.
    /// </summary>
    private bool ShouldRejectOldCookie(HttpRequestMessage request)
    {
        return m_OldAccessToken != null && request.Headers.TryGetValues("Cookie", out IEnumerable<string>? cookies) &&
            string.Join(';', cookies).Contains(m_OldAccessToken, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that forced-refresh requests use the rotated access token.
    /// </summary>
    private void ValidateNewCookie(HttpRequestMessage request)
    {
        if (m_NewAccessToken == null)
        {
            return;
        }

        if (!request.Headers.TryGetValues("Cookie", out IEnumerable<string>? cookies) ||
            !string.Join(';', cookies).Contains(m_NewAccessToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("missing refreshed Cursor dashboard cookie");
        }
    }

    /// <summary>
    /// Creates a successful JSON response for one fake Cursor dashboard request.
    /// </summary>
    private static HttpResponseMessage CreateJsonResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
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
