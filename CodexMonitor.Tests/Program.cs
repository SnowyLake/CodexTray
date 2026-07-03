using CodexMonitor.Core;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CodexMonitor.Tests;

internal static class Program
{
    private static int s_Failures;

    /// <summary>
    /// Runs all C# checks for the tray implementation.
    /// </summary>
    private static async Task<int> Main()
    {
        await RunAsync("collects limits and display labels", TestCollectsLimitsAndDisplayLabelsAsync);
        await RunAsync("uses countdown label for same-day weekly reset", TestWeeklyCountdownLabelAsync);
        await RunAsync("uses countdown label for next-day weekly reset", TestNextDayWeeklyCountdownLabelAsync);
        await RunAsync("returns unavailable response without sessions", TestEmptyResponseAsync);
        await RunAsync("collects official Codex quota", TestOfficialQuotaAsync);
        await RunAsync("serves health and usage over HTTP", TestHttpServerAsync);
        await RunAsync("installs LiteMonitor plugin config", TestPluginInstallAsync);
        await RunAsync("installs TrafficMonitor plugin", TestTrafficMonitorPluginInstallAsync);
        await RunAsync("stores settings beside the executable", TestSettingsStorePathAsync);
        await RunAsync("repairs missing settings fields", TestSettingsStoreRepairsMissingFieldsAsync);
        await RunAsync("normalizes settings refresh interval", TestSettingsNormalizeAsync);
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
        long resetWeekly = now.AddDays(3).AddHours(4).ToUnixTimeSeconds();
        WriteTokenEvent(temp.Path, now, reset5H, resetWeekly, 12.0, 34.0);

        CodexMonitorCollector collector = new(() => now);
        UsageResponse response = collector.Collect(temp.Path);

        AssertTrue(response.Available, "response should be available");
        AssertEqual(12, response.Limits.FiveHour.UsedPercent, "five hour used percent");
        AssertEqual(88, response.Limits.FiveHour.RemainingPercent, "five hour remaining percent");
        AssertEqual(34, response.Limits.Weekly.UsedPercent, "weekly used percent");
        AssertEqual(66, response.Limits.Weekly.RemainingPercent, "weekly remaining percent");
        AssertEqual("plus", response.PlanType, "plan type");
        AssertEqual("88% [2h 05m]", response.Display.Codex5H, "five hour display");
        AssertEqual("66% [3d 04h]", response.Display.CodexWeekly, "weekly display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests weekly countdown labels on the current day.
    /// </summary>
    private static Task TestWeeklyCountdownLabelAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long reset5H = now.AddHours(1).ToUnixTimeSeconds();
        long resetWeekly = now.AddHours(3).ToUnixTimeSeconds();
        WriteTokenEvent(temp.Path, now, reset5H, resetWeekly, 20.0, 40.0);

        CodexMonitorCollector collector = new(() => now);
        UsageResponse response = collector.Collect(temp.Path);

        AssertEqual("60% [0d 03h]", response.Display.CodexWeekly, "weekly countdown display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests weekly countdown labels on the next day even when below twenty four hours.
    /// </summary>
    private static Task TestNextDayWeeklyCountdownLabelAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 23, 0, 0, TimeSpan.FromHours(8));
        long reset5H = now.AddHours(1).ToUnixTimeSeconds();
        DateTimeOffset resetWeekly = new(2026, 7, 2, 2, 0, 0, TimeSpan.FromHours(8));
        WriteTokenEvent(temp.Path, now, reset5H, resetWeekly.ToUnixTimeSeconds(), 20.0, 40.0);

        CodexMonitorCollector collector = new(() => now);
        UsageResponse response = collector.Collect(temp.Path);

        AssertEqual("60% [0d 03h]", response.Display.CodexWeekly, "weekly next-day countdown display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests the empty response when no sessions exist.
    /// </summary>
    private static Task TestEmptyResponseAsync()
    {
        using TempDirectory temp = new();
        CodexMonitorCollector collector = new(() => new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8)));
        UsageResponse response = collector.Collect(temp.Path);

        AssertTrue(!response.Available, "response should be unavailable");
        AssertEqual("unavailable", response.Display.Codex5H, "five hour unavailable display");
        AssertEqual("Codex unavailable", response.Display.Summary, "summary unavailable display");
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
        CodexMonitorCollector collector = new(() => now, client);

        UsageResponse response = collector.Collect(temp.Path);

        AssertTrue(response.Available, "official response should be available");
        AssertEqual("official_api", response.Source, "official source");
        AssertEqual(75, response.Limits.FiveHour.RemainingPercent, "official five hour remaining percent");
        AssertEqual(60, response.Limits.Weekly.RemainingPercent, "official weekly remaining percent");
        AssertEqual("75% [1h 15m]", response.Display.Codex5H, "official five hour display");
        AssertEqual("60% [2d 12h]", response.Display.CodexWeekly, "official weekly display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests the lightweight HTTP server endpoints.
    /// </summary>
    private static async Task TestHttpServerAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        WriteTokenEvent(temp.Path, now, now.AddHours(1).ToUnixTimeSeconds(), now.AddDays(2).ToUnixTimeSeconds(), 10.0, 20.0);
        CodexMonitorCollector collector = new(() => now);
        UsageCache usageCache = new();
        usageCache.Update(collector.Collect(temp.Path));
        using LightweightHttpServer server = new(usageCache, 0);
        server.Start();

        using HttpClient client = new();
        string health = await client.GetStringAsync($"http://127.0.0.1:{server.Port}/health");
        AssertTrue(health.Contains("\"ok\":true", StringComparison.Ordinal), "health response");

        string usageJson = await client.GetStringAsync($"http://127.0.0.1:{server.Port}{CodexMonitorDefaults.UsageEndpointPath}");
        using JsonDocument document = JsonDocument.Parse(usageJson);
        string display = document.RootElement.GetProperty("display").GetProperty("codex_5h").GetString() ?? string.Empty;
        AssertEqual("90% [1h 00m]", display, "HTTP five hour display");
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
        AssertTrue(content.Contains("\"format_val\": \"     {{codex_5h_display}}\"", StringComparison.Ordinal), "plugin content should include five hour padding");
        AssertTrue(content.Contains("\"format_val\": \" {{codex_weekly_display}}\"", StringComparison.Ordinal), "plugin content should include weekly padding");
        AssertTrue(content.Contains("http://127.0.0.1:17998/codex-monitor", StringComparison.Ordinal), "plugin content should include bridge URL");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests installing the TrafficMonitor plugin files.
    /// </summary>
    private static Task TestTrafficMonitorPluginInstallAsync()
    {
        using TempDirectory temp = new();
        File.WriteAllText(Path.Combine(temp.Path, "TrafficMonitor.exe"), string.Empty);
        string sourcePath = Path.Combine(temp.Path, CodexMonitorDefaults.TrafficMonitorPluginFileName);
        File.WriteAllText(sourcePath, "fake dll");

        string targetPath = TrafficMonitorPluginInstaller.Install(temp.Path, 17999, sourcePath);
        AssertTrue(File.Exists(targetPath), "plugin dll should exist");
        string configPath = Path.Combine(temp.Path, "plugins", CodexMonitorDefaults.TrafficMonitorPluginConfigFileName);
        AssertTrue(File.Exists(configPath), "plugin config should exist");
        string content = File.ReadAllText(configPath);
        AssertTrue(content.Contains("http://127.0.0.1:17999/codex-monitor", StringComparison.Ordinal), "plugin config should include bridge URL");
        AssertTrue(content.Contains("RequestIntervalSeconds=60", StringComparison.Ordinal), "plugin config should include request interval");
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

        string expectedPath = Path.Combine(temp.Path, CodexMonitorDefaults.SettingsFileName);
        AssertEqual(expectedPath, store.SettingsPath, "settings path");
        AssertTrue(File.Exists(expectedPath), "settings file should exist beside executable");
        AssertTrue(!Directory.Exists(Path.Combine(temp.Path, "CodexMonitor")), "settings directory should not exist");
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
        AssertEqual(CodexMonitorDefaults.RefreshIntervalMinutes, settings.RefreshIntervalMinutes, "default refresh interval");

        string repairedJson = File.ReadAllText(store.SettingsPath);
        using JsonDocument document = JsonDocument.Parse(repairedJson);
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.LiteMonitorDir), out _), "repaired settings should include LiteMonitor path");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.TrafficMonitorDir), out _), "repaired settings should include TrafficMonitor path");
        AssertTrue(document.RootElement.TryGetProperty(nameof(AppSettings.RefreshIntervalMinutes), out _), "repaired settings should include refresh interval");
        AssertTrue(!document.RootElement.TryGetProperty("FirstRunCompleted", out _), "repaired settings should not include first-run flag");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests default refresh interval normalization.
    /// </summary>
    private static Task TestSettingsNormalizeAsync()
    {
        AppSettings settings = new()
        {
            Port = -1,
            RefreshIntervalMinutes = 0,
        };

        settings.Normalize();

        AssertEqual(CodexMonitorDefaults.Port, settings.Port, "default port");
        AssertEqual(CodexMonitorDefaults.RefreshIntervalMinutes, settings.RefreshIntervalMinutes, "default refresh interval");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes a sample Codex token_count session event.
    /// </summary>
    private static void WriteTokenEvent(string codexRoot, DateTimeOffset timestamp, long reset5H, long resetWeekly, double primaryUsed, double secondaryUsed)
    {
        string sessions = Path.Combine(codexRoot, "sessions", "2026", "07", "01");
        Directory.CreateDirectory(sessions);
        string path = Path.Combine(sessions, "rollout-2026-07-01T10-00-00-test.jsonl");
        object payload = new
        {
            timestamp = timestamp.ToString("O"),
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                rate_limits = new
                {
                    primary = new
                    {
                        used_percent = primaryUsed,
                        window_minutes = 300,
                        resets_at = reset5H,
                    },
                    secondary = new
                    {
                        used_percent = secondaryUsed,
                        window_minutes = 10080,
                        resets_at = resetWeekly,
                    },
                    plan_type = "plus",
                },
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
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
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexMonitorTests", Guid.NewGuid().ToString("N"));

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

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly string m_Body;

    /// <summary>
    /// Creates a handler that returns a fixed JSON response.
    /// </summary>
    public FakeHttpMessageHandler(string body)
    {
        m_Body = body;
    }

    /// <summary>
    /// Sends a fake HTTP response for collector tests.
    /// </summary>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.ToString() != "https://chatgpt.com/backend-api/wham/usage")
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

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(m_Body, Encoding.UTF8, "application/json"),
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
