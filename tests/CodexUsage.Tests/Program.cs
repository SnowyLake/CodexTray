using CodexUsage.Core;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace CodexUsage.Tests;

internal static class Program
{
    private static int s_Failures;

    /// <summary>
    /// Runs all C# checks for the tray implementation.
    /// </summary>
    private static async Task<int> Main()
    {
        await RunAsync("collects limits and display labels", TestCollectsLimitsAndDisplayLabelsAsync);
        await RunAsync("uses clock label for weekly reset below 24 hours", TestWeeklyClockLabelAsync);
        await RunAsync("returns unavailable response without sessions", TestEmptyResponseAsync);
        await RunAsync("serves health and usage over HTTP", TestHttpServerAsync);
        await RunAsync("installs LiteMonitor plugin config", TestPluginInstallAsync);
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
        long reset5H = now.AddHours(2).ToUnixTimeSeconds();
        long resetWeekly = now.AddDays(3).ToUnixTimeSeconds();
        WriteTokenEvent(temp.Path, now, reset5H, resetWeekly, 12.0, 34.0);

        CodexUsageCollector collector = new(() => now);
        UsageResponse response = collector.Collect(temp.Path);

        AssertTrue(response.Available, "response should be available");
        AssertEqual(12, response.Limits.FiveHour.UsedPercent, "five hour used percent");
        AssertEqual(88, response.Limits.FiveHour.RemainingPercent, "five hour remaining percent");
        AssertEqual(34, response.Limits.Weekly.UsedPercent, "weekly used percent");
        AssertEqual(66, response.Limits.Weekly.RemainingPercent, "weekly remaining percent");
        AssertEqual("plus", response.PlanType, "plan type");
        AssertEqual("88%  14:00", response.Display.Codex5H, "five hour display");
        AssertEqual("66%  07-04", response.Display.CodexWeekly, "weekly display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests weekly reset labels below twenty four hours.
    /// </summary>
    private static Task TestWeeklyClockLabelAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8));
        long reset5H = now.AddHours(1).ToUnixTimeSeconds();
        long resetWeekly = now.AddHours(3).ToUnixTimeSeconds();
        WriteTokenEvent(temp.Path, now, reset5H, resetWeekly, 20.0, 40.0);

        CodexUsageCollector collector = new(() => now);
        UsageResponse response = collector.Collect(temp.Path);

        AssertEqual("60%  15:00", response.Display.CodexWeekly, "weekly clock display");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tests the empty response when no sessions exist.
    /// </summary>
    private static Task TestEmptyResponseAsync()
    {
        using TempDirectory temp = new();
        CodexUsageCollector collector = new(() => new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(8)));
        UsageResponse response = collector.Collect(temp.Path);

        AssertTrue(!response.Available, "response should be unavailable");
        AssertEqual("unavailable", response.Display.Codex5H, "five hour unavailable display");
        AssertEqual("Codex unavailable", response.Display.Summary, "summary unavailable display");
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
        CodexUsageCollector collector = new(() => now);
        using LightweightHttpServer server = new(collector, temp.Path, 0);
        server.Start();

        using HttpClient client = new();
        string health = await client.GetStringAsync($"http://127.0.0.1:{server.Port}/health");
        AssertTrue(health.Contains("\"ok\":true", StringComparison.Ordinal), "health response");

        string usageJson = await client.GetStringAsync($"http://127.0.0.1:{server.Port}/codex-usage");
        using JsonDocument document = JsonDocument.Parse(usageJson);
        string display = document.RootElement.GetProperty("display").GetProperty("codex_5h").GetString() ?? string.Empty;
        AssertEqual("90%  13:00", display, "HTTP five hour display");
    }

    /// <summary>
    /// Tests installing the plugin configuration file.
    /// </summary>
    private static Task TestPluginInstallAsync()
    {
        using TempDirectory temp = new();
        File.WriteAllText(Path.Combine(temp.Path, "LiteMonitor.exe"), string.Empty);
        Directory.CreateDirectory(Path.Combine(temp.Path, "resources", "plugins"));

        string targetPath = LiteMonitorPluginInstaller.Install(temp.Path);
        AssertTrue(File.Exists(targetPath), "plugin file should exist");
        string content = File.ReadAllText(targetPath);
        AssertTrue(content.Contains("Codex Weekly", StringComparison.Ordinal), "plugin content should include weekly output");
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
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexUsageTests", Guid.NewGuid().ToString("N"));

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
