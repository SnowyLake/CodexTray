using CodexTray.Core;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace CodexTray.Tests;

internal static class LocalHttpSecurityTests
{
    private const int k_MaximumHeaderBytes = 16 * 1024;
    private const int k_MaximumClients = 32;

    internal static async Task RunAsync()
    {
        UsageCache cache = new();
        cache.UpdateCodex(new UsageResponse
        {
            Available = true,
            Error = "private-auth-file",
            Source = "private-codex-directory",
            PlanType = "private-plan",
            UpdatedAt = "private-refresh-time",
            Limits = new UsageLimits { Weekly = new UsageLimit { RemainingPercent = 80 } },
            Display = new UsageDisplay { Weekly = "80%" },
        });
        cache.UpdateGrok(new GrokPluginUsage(new UsageLimit { RemainingPercent = 60 }, "60%"));
        cache.UpdateGemini(new GeminiPluginUsage(new UsageLimit { RemainingPercent = 75 }, "75%"));
        cache.UpdateOpenCode(new OpenCodePluginUsage(new UsageLimit { RemainingPercent = 90 }, "90%"));
        using LightweightHttpServer server = new(cache, 0);
        server.Start();

        try
        {
            await CheckProtocolAsync(server);
            await CheckPluginResponsesAsync(server);
            await CheckClientLimitAndRestartAsync(server);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task CheckProtocolAsync(LightweightHttpServer server)
    {
        string host = $"localhost:{server.Port}";
        await AssertStatusAsync(server.Port, $"GET /health HTTP/1.1\r\nHost: {host}\r\n\r\n", 200);
        await AssertStatusAsync(server.Port, $"GET /health?check=1 HTTP/1.0\r\nhost: 127.0.0.1:{server.Port}\r\n\r\n", 200);
        await AssertStatusAsync(server.Port, $"GET /health HTTP/1.1\r\nHost: LOCALHOST:{server.Port}\r\n\r\n", 200);
        await AssertStatusAsync(server.Port, $"POST /health HTTP/1.1\r\nHost: {host}\r\n\r\n", 405);
        await AssertStatusAsync(server.Port, $"GET /missing HTTP/1.1\r\nHost: {host}\r\n\r\n", 404);
        await AssertStatusAsync(server.Port, "GET /health HTTP/1.1\r\n\r\n", 400);
        await AssertStatusAsync(server.Port, "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n", 400);
        await AssertStatusAsync(server.Port, $"GET /health HTTP/1.1\r\nHost: localhost:{server.Port + 1}\r\n\r\n", 400);
        await AssertStatusAsync(server.Port, $"GET /health HTTP/1.1\r\nHost: example.test:{server.Port}\r\n\r\n", 400);
        await AssertStatusAsync(server.Port, $"GET /health HTTP/1.1\r\nHost: {host}\r\nHost: {host}\r\n\r\n", 400);
        await AssertStatusAsync(server.Port, $"GET /health HTTP/1.1\r\nHost : {host}\r\n\r\n", 400);
        await AssertStatusAsync(server.Port, $"GET /health HTTP/1.1\r\nHost: {host}\r\n", 400);
        await AssertStatusAsync(server.Port, $"GET /health\r\nHost: {host}\r\n\r\n", 400);

        string headerPrefix = $"GET /health HTTP/1.1\r\nHost: {host}\r\nX-Pad: ";
        const string headerEnd = "\r\n\r\n";
        string maximumHeader = headerPrefix + new string('x', k_MaximumHeaderBytes - headerPrefix.Length - headerEnd.Length) + headerEnd;
        await AssertStatusAsync(server.Port, maximumHeader, 200);
        await AssertStatusAsync(server.Port, maximumHeader.Insert(headerPrefix.Length, "x")[..k_MaximumHeaderBytes], 431);

        string fields = string.Concat(Enumerable.Repeat("X-Test: value\r\n", 31));
        await AssertStatusAsync(server.Port, $"GET /health HTTP/1.1\r\nHost: {host}\r\n{fields}\r\n", 200);
        await AssertStatusAsync(server.Port, $"GET /health HTTP/1.1\r\nHost: {host}\r\n{fields}X-Extra: value\r\n\r\n", 431);

        using TcpClient client = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        await client.ConnectAsync(CodexTrayDefaults.Host, server.Port, timeout.Token);
        await using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /health HTTP/1.1\r\nHost: {host}\r\n"), timeout.Token);
        Task<int> response = stream.ReadAsync(new byte[1], timeout.Token).AsTask();
        await Task.Delay(100, timeout.Token);
        Assert(!response.IsCompleted, "server responded before the header was complete");
        await stream.WriteAsync("\r\n"u8.ToArray(), timeout.Token);
        Assert(await response == 1, "complete request did not receive a response");
    }

    private static async Task CheckPluginResponsesAsync(LightweightHttpServer server)
    {
        using HttpClient client = new();
        string url = $"http://{CodexTrayDefaults.Host}:{server.Port}";
        string json = await client.GetStringAsync(url + CodexTrayDefaults.UsageEndpointPath);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert(root.EnumerateObject().Select(property => property.Name).SequenceEqual(["limits", "display"]), "plugin JSON exposed internal fields");
        Assert(!json.Contains("private-", StringComparison.Ordinal), "plugin JSON exposed internal values");
        JsonElement display = root.GetProperty("display");
        JsonElement limits = root.GetProperty("limits");
        Assert(display.EnumerateObject().Select(property => property.Name).SequenceEqual(["codex_weekly", "grok_weekly", "gemini_session", "opencode_session"]), "plugin display fields changed");
        Assert(limits.EnumerateObject().Select(property => property.Name).SequenceEqual(["codex_weekly", "grok_weekly", "gemini_session", "opencode_session"]), "plugin limit fields changed");
        Assert(limits.EnumerateObject().All(property => property.Value.EnumerateObject().Select(field => field.Name).SequenceEqual(["remaining_percent"])), "plugin limit exposed internal fields");
        Assert(display.GetProperty("codex_weekly").GetString() == "80%" && limits.GetProperty("codex_weekly").GetProperty("remaining_percent").GetInt32() == 80, "Codex plugin fields changed");
        Assert(display.GetProperty("grok_weekly").GetString() == "60%" && limits.GetProperty("grok_weekly").GetProperty("remaining_percent").GetInt32() == 60, "Grok plugin fields changed");
        Assert(display.GetProperty("gemini_session").GetString() == "75%" && limits.GetProperty("gemini_session").GetProperty("remaining_percent").GetInt32() == 75, "Gemini plugin fields changed");
        Assert(display.GetProperty("opencode_session").GetString() == "90%" && limits.GetProperty("opencode_session").GetProperty("remaining_percent").GetInt32() == 90, "OpenCode plugin fields changed");

        string text = await client.GetStringAsync(url + CodexTrayDefaults.UsageTextEndpointPath);
        Assert(text.Split(Environment.NewLine).SequenceEqual(["80%", "60%", "75%", "90%"]), "TrafficMonitor response changed");
    }

    private static async Task CheckClientLimitAndRestartAsync(LightweightHttpServer server)
    {
        PropertyInfo activeClients = typeof(LightweightHttpServer).GetProperty("ActiveClientCount", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("active client count property was not found");
        List<TcpClient> clients = [];
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            while ((int)activeClients.GetValue(server)! != 0)
            {
                await Task.Delay(10, timeout.Token);
            }

            for (int index = 0; index < k_MaximumClients; index++)
            {
                TcpClient client = new();
                clients.Add(client);
                await client.ConnectAsync(CodexTrayDefaults.Host, server.Port);
            }

            while ((int)activeClients.GetValue(server)! != k_MaximumClients)
            {
                await Task.Delay(10, timeout.Token);
            }

            using TcpClient overflow = new();
            await overflow.ConnectAsync(CodexTrayDefaults.Host, server.Port);
            try
            {
                int read = await overflow.GetStream().ReadAsync(new byte[1], timeout.Token);
                Assert(read == 0, "connection beyond the limit remained active");
            }
            catch (IOException)
            {
            }

            Assert((int)activeClients.GetValue(server)! <= k_MaximumClients, "tracked clients exceeded the limit");
            await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert((int)activeClients.GetValue(server)! == 0, "stopping did not drain clients");
            server.Start();
            await AssertStatusAsync(server.Port, $"GET /health HTTP/1.1\r\nHost: localhost:{server.Port}\r\n\r\n", 200);
        }
        finally
        {
            foreach (TcpClient client in clients)
            {
                client.Dispose();
            }
        }
    }

    private static async Task AssertStatusAsync(int port, string request, int expectedStatus)
    {
        using TcpClient client = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        await client.ConnectAsync(CodexTrayDefaults.Host, port, timeout.Token);
        await using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        client.Client.Shutdown(SocketShutdown.Send);
        using StreamReader reader = new(stream, Encoding.UTF8);
        string? statusLine = await reader.ReadLineAsync(timeout.Token);
        Assert(statusLine?.StartsWith($"HTTP/1.1 {expectedStatus} ", StringComparison.Ordinal) == true, $"expected HTTP {expectedStatus}, got {statusLine ?? "no response"}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
