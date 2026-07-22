using CodexTray.Core;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CodexTray.Tests;

internal static class Program
{
    private static int s_Failures;

    /// <summary>
    /// Runs all C# checks for the tray implementation.
    /// </summary>
    private static async Task<int> Main()
    {
        await RunAsync("collects limits and display labels", TestCollectsLimitsAndDisplayLabelsAsync);
        await RunAsync("uses countdown label for same-day seven day reset", TestSevenDayCountdownLabelAsync);
        await RunAsync("uses countdown label for next-day seven day reset", TestNextDaySevenDayCountdownLabelAsync);
        await RunAsync("returns unavailable response without OAuth credentials", TestEmptyResponseAsync);
        await RunAsync("collects official Codex quota", TestOfficialQuotaAsync);
        await RunAsync("classifies a lone weekly quota by window duration", TestLoneWeeklyQuotaAsync);
        await RunAsync("collects Codex reset credits", TestResetCreditsAsync);
        await RunAsync("omits reset suffix when disabled", TestDisplayWithoutResetSuffixAsync);
        await RunAsync("uses absolute reset time when enabled", TestAbsoluteResetTimeAsync);
        await RunAsync("serves health and usage over HTTP", TestHttpServerAsync);
        await RunAsync("installs LiteMonitor plugin config", TestPluginInstallAsync);
        await RunAsync("installs TrafficMonitor plugin", TestTrafficMonitorPluginInstallAsync);
        await RunAsync("stores settings beside the executable", TestSettingsStorePathAsync);
        await RunAsync("repairs missing settings fields", TestSettingsStoreRepairsMissingFieldsAsync);
        await RunAsync("normalizes settings refresh interval", TestSettingsNormalizeAsync);
        await RunAsync("persists API monitor settings", TestApiMonitorSettingsAsync);
        await RunAsync("collects DeepSeek and NewAPI balances", TestApiUsageCollectorAsync);
        await RunAsync("parses Grok billing protobuf", TestGrokUsageCollectorAsync);
        await RunAsync("refreshes expired Grok Build OAuth", TestGrokBuildOAuthRefreshAsync);
        await RunAsync("refreshes expired OpenCode OAuth", TestOpenCodeOAuthRefreshAsync);
        await RunAsync("summarizes API refresh statuses", TestApiUsageSummaryAsync);
        await RunAsync("collects exact Codex token cost", TestTokenCostCollectorAsync);
        await RunAsync("counts live subagent usage without replaying parent history", TestSubagentTokenCostAsync);
        Console.WriteLine(s_Failures == 0 ? "All C# tests passed." : $"C# tests failed: {s_Failures}");
        return s_Failures == 0 ? 0 : 1;
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
        long reset5H = now.AddHours(2).AddMinutes(5).ToUnixTimeSeconds();
        long resetSevenDay = now.AddDays(3).AddHours(4).ToUnixTimeSeconds();
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, reset5H, resetSevenDay, 12.0, 34.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = collector.Collect(temp.Path);

        AssertTrue(response.Available, "response should be available");
        AssertEqual(12, response.Limits.FiveHour.UsedPercent, "five hour used percent");
        AssertEqual(88, response.Limits.FiveHour.RemainingPercent, "five hour remaining percent");
        AssertEqual(34, response.Limits.SevenDay.UsedPercent, "seven day used percent");
        AssertEqual(66, response.Limits.SevenDay.RemainingPercent, "seven day remaining percent");
        AssertEqual("pro", response.PlanType, "plan type");
        AssertEqual("88% 2h05m", response.Display.Codex5H, "five hour display");
        AssertEqual("66% 3d04h", response.Display.Codex7D, "seven day display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests seven day countdown labels on the current day.
    /// </summary>
    private static Task TestSevenDayCountdownLabelAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long reset5H = now.AddHours(1).ToUnixTimeSeconds();
        long resetSevenDay = now.AddHours(3).ToUnixTimeSeconds();
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, reset5H, resetSevenDay, 20.0, 40.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = collector.Collect(temp.Path);

        AssertEqual("60% 0d03h", response.Display.Codex7D, "seven day countdown display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests seven day countdown labels on the next day even when below twenty four hours.
    /// </summary>
    private static Task TestNextDaySevenDayCountdownLabelAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 23, 0, 0, TimeSpan.FromHours(8));
        long reset5H = now.AddHours(1).ToUnixTimeSeconds();
        DateTimeOffset resetSevenDay = new(2026, 7, 2, 2, 0, 0, TimeSpan.FromHours(8));
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, reset5H, resetSevenDay.ToUnixTimeSeconds(), 20.0, 40.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = collector.Collect(temp.Path);

        AssertEqual("60% 0d03h", response.Display.Codex7D, "seven day next-day countdown display");
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
        AssertEqual("N/A", response.Display.Codex5H, "five hour unavailable display");
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
        AssertEqual(75, response.Limits.FiveHour.RemainingPercent, "official five hour remaining percent");
        AssertEqual(60, response.Limits.SevenDay.RemainingPercent, "official seven day remaining percent");
        AssertEqual("75% 1h15m", response.Display.Codex5H, "official five hour display");
        AssertEqual("60% 2d12h", response.Display.Codex7D, "official seven day display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests that plugin display values drop the reset suffix when the option is disabled.
    /// </summary>
    private static Task TestDisplayWithoutResetSuffixAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long reset5H = now.AddHours(1).AddMinutes(15).ToUnixTimeSeconds();
        long resetSevenDay = now.AddDays(2).AddHours(12).ToUnixTimeSeconds();
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, reset5H, resetSevenDay, 25.0, 40.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = collector.Collect(temp.Path, showResetTimeInPlugins: false);

        AssertEqual("75%", response.Display.Codex5H, "five hour display without reset suffix");
        AssertEqual("60%", response.Display.Codex7D, "seven day display without reset suffix");
        AssertEqual("1h15m", response.Limits.FiveHour.ResetLabel, "reset label remains available for the panel");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests that reset labels use absolute clock and date when the option is enabled.
    /// </summary>
    private static Task TestAbsoluteResetTimeAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long reset5H = now.AddHours(1).AddMinutes(15).ToUnixTimeSeconds();
        long resetSevenDay = now.AddDays(2).AddHours(12).ToUnixTimeSeconds();
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, reset5H, resetSevenDay, 25.0, 40.0, out HttpClient client);
        using HttpClient _ = client;

        UsageResponse response = collector.Collect(temp.Path, showResetTimeInPlugins: true, useAbsoluteResetTime: true);

        AssertEqual("13:15", response.Limits.FiveHour.ResetLabel, "five hour absolute reset clock");
        AssertEqual("07-04", response.Limits.SevenDay.ResetLabel, "seven day absolute reset date");
        AssertEqual("75% 13:15", response.Display.Codex5H, "five hour display with absolute reset");
        AssertEqual("60% 07-04", response.Display.Codex7D, "seven day display with absolute reset");
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
        usageCache.Update(collector.Collect(temp.Path));
        using LightweightHttpServer server = new(usageCache, 0);
        server.Start();

        using HttpClient client = new();
        string health = await client.GetStringAsync($"http://{CodexTrayDefaults.Host}:{server.Port}{CodexTrayDefaults.HealthEndpointPath}");
        AssertTrue(health.Contains("\"ok\":true", StringComparison.Ordinal), "health response");

        string usageJson = await client.GetStringAsync($"http://{CodexTrayDefaults.Host}:{server.Port}{CodexTrayDefaults.UsageEndpointPath}");
        using JsonDocument document = JsonDocument.Parse(usageJson);
        string display = document.RootElement.GetProperty("display").GetProperty("codex_5h").GetString() ?? string.Empty;
        AssertEqual("90% 1h00m", display, "HTTP five hour display");

        string usageText = await client.GetStringAsync($"http://{CodexTrayDefaults.Host}:{server.Port}{CodexTrayDefaults.UsageTextEndpointPath}");
        AssertTrue(usageText.Contains("90% 1h00m", StringComparison.Ordinal), "text endpoint should include five hour display");
        AssertTrue(usageText.Contains("80% 2d00h", StringComparison.Ordinal), "text endpoint should include seven day display");
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
        AssertTrue(content.Contains("\"format_val\": \"{{codex_5h_display}}\"", StringComparison.Ordinal), "plugin content should include five hour value");
        AssertTrue(content.Contains("\"format_val\": \"{{codex_7d_display}}\"", StringComparison.Ordinal), "plugin content should include seven day value");
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
        };

        store.Save(settings);

        string expectedPath = Path.Combine(temp.Path, CodexTrayDefaults.SettingsFileName);
        AssertEqual(expectedPath, store.SettingsPath, "settings path");
        AssertTrue(File.Exists(expectedPath), "settings file should exist beside executable");
        AssertTrue(!Directory.Exists(Path.Combine(temp.Path, "CodexTray")), "settings directory should not exist");
        AssertEqual(17997, store.Load().Port, "saved settings port");
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
        AssertEqual(AppSettings.ThemeModeSystem, settings.ThemeMode, "default theme mode");
        AssertEqual(AppSettings.TokenUnitEnglish, settings.TokenUnit, "default token unit");
        AssertEqual(TokenCostItem.All, settings.TokenCostItems, "default token cost items");
        AssertEqual(0, settings.ApiMonitors.Count, "default API monitors");

        string repairedJson = File.ReadAllText(store.SettingsPath);
        using JsonDocument document = JsonDocument.Parse(repairedJson);
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.LiteMonitorDir), out _), "repaired settings should include LiteMonitor path");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.TrafficMonitorDir), out _), "repaired settings should include TrafficMonitor path");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.RefreshIntervalMinutes), out _), "repaired settings should include refresh interval");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.ThemeMode), out _), "repaired settings should include theme mode");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.TokenUnit), out _), "repaired settings should include token unit");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.TokenCostItems), out _), "repaired settings should include token cost items");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.ApiMonitors), out _), "repaired settings should include API monitors");
        AssertTrue(!document.RootElement.TryGetProperty("FirstRunCompleted", out _), "repaired settings should not include first-run flag");
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
            TokenCostItems = TokenCostItem.Today | (TokenCostItem)(1 << 10),
        };

        settings.Normalize();

        AssertEqual(CodexTrayDefaults.Port, settings.Port, "default port");
        AssertEqual(CodexTrayDefaults.RefreshIntervalMinutes, settings.RefreshIntervalMinutes, "default refresh interval");
        AssertEqual(AppSettings.ThemeModeSystem, settings.ThemeMode, "default theme mode");
        AssertEqual(AppSettings.TokenUnitChinese, settings.TokenUnit, "Chinese token unit");
        AssertEqual(TokenCostItem.Today, settings.TokenCostItems, "supported token cost items");
        settings.TokenUnit = "M/B";
        settings.Normalize();
        AssertEqual(AppSettings.TokenUnitEnglish, settings.TokenUnit, "legacy English token unit");
        settings.TokenUnit = "万/亿";
        settings.Normalize();
        AssertEqual(AppSettings.TokenUnitChinese, settings.TokenUnit, "legacy Chinese token unit");
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
            ApiMonitors =
            [
                new ApiMonitorSettings
                {
                    Name = "Personal DeepSeek",
                    ApiKey = "deepseek-secret-token",
                },
                new ApiMonitorSettings
                {
                    Name = "OpenCode Grok",
                    Provider = ApiMonitorSettings.GrokProvider,
                    GrokOAuthSource = ApiMonitorSettings.OpenCodeOAuthSource,
                },
            ],
        };

        store.Save(settings);

        string json = File.ReadAllText(store.SettingsPath);
        AssertTrue(json.Contains("deepseek-secret-token", StringComparison.Ordinal), "settings should contain the API key");
        AppSettings loaded = store.Load();
        ApiMonitorSettings deepSeek = loaded.ApiMonitors.Single(monitor => monitor.Provider == ApiMonitorSettings.DeepSeekProvider);
        ApiMonitorSettings grok = loaded.ApiMonitors.Single(monitor => monitor.Provider == ApiMonitorSettings.GrokProvider);
        AssertEqual("deepseek-secret-token", deepSeek.ApiKey, "saved API key");
        AssertEqual("Personal DeepSeek", deepSeek.Name, "API monitor name");
        AssertEqual(ApiMonitorSettings.OpenCodeOAuthSource, grok.GrokOAuthSource, "saved Grok OAuth source");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests DeepSeek CNY balance and NewAPI USD quota parsing.
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

        IReadOnlyList<ApiUsageResult> results = await collector.CollectAsync([deepSeek, newApi]);

        AssertEqual("¥110.00", results[0].BalanceDisplay, "DeepSeek CNY balance");
        AssertEqual("$10.00", results[1].BalanceDisplay, "NewAPI remaining USD quota");
        AssertEqual("$5.00", results[1].UsedDisplay, "NewAPI used USD quota");
    }

    /// <summary>
    /// Tests protobuf parsing for Grok's consumed percentage and reset timestamp.
    /// </summary>
    private static Task TestGrokUsageCollectorAsync()
    {
        const long resetAt = 1_802_592_000;
        byte[] response = CreateGrokBillingResponse(42.5f, resetAt);
        GrokUsageSnapshot snapshot = GrokUsageCollector.ParseGrpcWebResponse(response, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));

        AssertEqual(42.5, snapshot.UsedPercent, "Grok used percentage");
        AssertEqual(resetAt, snapshot.ResetsAt, "Grok reset timestamp");
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
                    email = "keep-me@example.com",
                },
            }));

            using HttpClient client = new(new GrokOAuthRefreshHttpMessageHandler(
                "old-grok-access",
                "new-grok-access",
                "grok-refresh",
                "rotated-grok-refresh",
                CreateGrokBillingResponse(12.5f, 1_802_592_000)));
            GrokUsageCollector collector = new(client);
            GrokUsageSnapshot snapshot = await collector.CollectAsync(ApiMonitorSettings.GrokBuildOAuthSource);

            AssertEqual(12.5, snapshot.UsedPercent, "refreshed Grok Build used percentage");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(authPath));
            JsonElement entry = document.RootElement.GetProperty(entryKey);
            AssertEqual("new-grok-access", entry.GetProperty("key").GetString(), "Grok Build access token write-back");
            AssertEqual("rotated-grok-refresh", entry.GetProperty("refresh_token").GetString(), "Grok Build refresh token write-back");
            AssertEqual("keep-me@example.com", entry.GetProperty("email").GetString(), "Grok Build unrelated field preservation");
            AssertTrue(
                DateTimeOffset.Parse(entry.GetProperty("expires_at").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) >
                DateTimeOffset.UtcNow,
                "Grok Build expires_at should be in the future");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", string.IsNullOrEmpty(previousGrokHome) ? null : previousGrokHome);
        }
    }

    /// <summary>
    /// Tests that an expired OpenCode token is refreshed and written back to auth.json.
    /// </summary>
    private static async Task TestOpenCodeOAuthRefreshAsync()
    {
        using TempDirectory temp = new();
        string previousXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? string.Empty;
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", temp.Path);
        try
        {
            string authDirectory = Path.Combine(temp.Path, "opencode");
            Directory.CreateDirectory(authDirectory);
            string authPath = Path.Combine(authDirectory, "auth.json");
            File.WriteAllText(authPath, JsonSerializer.Serialize(new
            {
                anthropic = new { type = "api", key = "keep-other-provider" },
                xai = new
                {
                    type = "oauth",
                    access = "old-opencode-access",
                    refresh = "opencode-refresh",
                    expires = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds(),
                },
            }));

            using HttpClient client = new(new GrokOAuthRefreshHttpMessageHandler(
                "old-opencode-access",
                "new-opencode-access",
                "opencode-refresh",
                "rotated-opencode-refresh",
                CreateGrokBillingResponse(33f, 1_802_592_000)));
            GrokUsageCollector collector = new(client);
            GrokUsageSnapshot snapshot = await collector.CollectAsync(ApiMonitorSettings.OpenCodeOAuthSource);

            AssertEqual(33, snapshot.UsedPercent, "refreshed OpenCode used percentage");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(authPath));
            AssertEqual("keep-other-provider", document.RootElement.GetProperty("anthropic").GetProperty("key").GetString(), "OpenCode unrelated provider preservation");
            JsonElement xai = document.RootElement.GetProperty("xai");
            AssertEqual("new-opencode-access", xai.GetProperty("access").GetString(), "OpenCode access token write-back");
            AssertEqual("rotated-opencode-refresh", xai.GetProperty("refresh").GetString(), "OpenCode refresh token write-back");
            AssertTrue(xai.GetProperty("expires").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "OpenCode expires should be in the future");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", string.IsNullOrEmpty(previousXdg) ? null : previousXdg);
        }
    }

    /// <summary>
    /// Builds a minimal gRPC-web billing body for Grok collector tests.
    /// </summary>
    private static byte[] CreateGrokBillingResponse(float usedPercent, long resetAt)
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
        List<byte> response = [0, 0, 0, 0, (byte)payload.Count];
        response.AddRange(payload);
        return [.. response];
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
        AssertEqual(0, response.Limits.FiveHour.WindowMinutes, "five hour window should be absent");
        AssertEqual("N/A", response.Display.Codex5H, "five hour display should be unavailable");
        AssertEqual(10080, response.Limits.SevenDay.WindowMinutes, "weekly window duration");
        AssertEqual(42, response.Limits.SevenDay.RemainingPercent, "weekly remaining percent");
        AssertEqual("42% 6d23h", response.Display.Codex7D, "weekly display");
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
            available_count = 2,
            credits = new[]
            {
                new { status = "available", expires_at = nearestExpiry.AddDays(1).ToString("O") },
                new { status = "redeemed", expires_at = nearestExpiry.AddHours(-1).ToString("O") },
                new { status = "available", expires_at = nearestExpiry.ToString("O") },
            },
        });
        CodexTrayCollector collector = CreateOfficialCollector(temp.Path, now, now.AddHours(1).ToUnixTimeSeconds(), now.AddDays(2).ToUnixTimeSeconds(), 10.0, 20.0, out HttpClient client, resetCreditsBody);
        using HttpClient _ = client;

        UsageResponse response = collector.Collect(temp.Path);

        AssertTrue(response.ResetCredits.Available, "reset credits should be available");
        AssertEqual(2, response.ResetCredits.AvailableCount, "reset credit count");
        AssertEqual(nearestExpiry.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), response.ResetCredits.NearestExpiryLocal, "nearest local reset credit expiry");
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
        TokenCostSummary summary = collector.CollectToday(temp.Path, new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8)));
        AssertEqual(2900L, summary.TotalTokens, "today total tokens");
        AssertEqual(0.0062m, summary.CostUsd, "today API-equivalent cost");
        TokenCostStatistics statistics = collector.Collect(temp.Path, new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8)));
        AssertEqual(550L, statistics.Yesterday.TotalTokens, "yesterday total tokens");
        AssertEqual(3450L, statistics.Week.TotalTokens, "calendar week total tokens");
        AssertEqual(3520L, statistics.Month.TotalTokens, "calendar month total tokens");
        AssertEqual(3520L, statistics.SevenDay.TotalTokens, "seven day total tokens");
        AssertEqual(3560L, statistics.ThirtyDay.TotalTokens, "thirty day total tokens");
        AssertEqual(0.00774m, statistics.ThirtyDay.CostUsd, "thirty day API-equivalent cost");
        AssertEqual(3660L, statistics.Total.TotalTokens, "historical total tokens");
        AssertEqual(0.00794m, statistics.Total.CostUsd, "historical API-equivalent cost");
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
        TokenCostSummary total = collector.Collect(temp.Path, new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(8))).Total;
        AssertEqual(2860L, total.TotalTokens, "subagent total excludes only matching replay prefix");
        AssertEqual(0.006m, total.CostUsd, "subagent total cost");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a collector backed by fake OAuth credentials and a fixed official quota response.
    /// </summary>
    private static CodexTrayCollector CreateOfficialCollector(string codexRoot, DateTimeOffset now, long reset5H, long resetSevenDay, double primaryUsed, double secondaryUsed, out HttpClient client, string? resetCreditsBody = null)
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
                    reset_at = reset5H,
                },
                secondary_window = new
                {
                    used_percent = secondaryUsed,
                    limit_window_seconds = 604800,
                    reset_at = resetSevenDay,
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
    /// Returns fixed DeepSeek and NewAPI account responses.
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string host = request.RequestUri?.Host ?? string.Empty;
        if (request.Headers.Authorization?.Scheme != "Bearer")
        {
            throw new InvalidOperationException("missing API authorization header");
        }

        string body;
        if (host == "deepseek.example")
        {
            AssertRequest(request, "/user/balance", "deepseek-key");
            body = "{\"is_available\":true,\"balance_infos\":[{\"currency\":\"USD\",\"total_balance\":\"15.00\"},{\"currency\":\"CNY\",\"total_balance\":\"110.00\"}]}";
        }
        else if (host == "newapi.example")
        {
            AssertRequest(request, "/api/user/self", "newapi-token");
            if (!request.Headers.TryGetValues("New-Api-User", out IEnumerable<string>? userIds) || userIds.Single() != "42")
            {
                throw new InvalidOperationException("missing NewAPI user header");
            }

            body = "{\"success\":true,\"data\":{\"group\":\"default\",\"quota\":5000000,\"used_quota\":2500000}}";
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
    private static void AssertRequest(HttpRequestMessage request, string path, string token)
    {
        if (request.RequestUri?.AbsolutePath != path || request.Headers.Authorization?.Parameter != token)
        {
            throw new InvalidOperationException("invalid API usage request");
        }
    }
}
