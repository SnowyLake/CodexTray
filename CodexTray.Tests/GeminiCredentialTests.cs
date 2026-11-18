using CodexTray.Core;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CodexTray.Tests;

internal static class GeminiCredentialTests
{
    internal static Task EncodingsAsync()
    {
        const string accessToken = "ya29.synthetic-access-token-for-tests";
        const string refreshToken = "1//synthetic-refresh-token-for-tests";
        string json = JsonSerializer.Serialize(new { access_token = accessToken, refresh_token = refreshToken });
        foreach (Encoding encoding in new[] { Encoding.UTF8, Encoding.Unicode })
        {
            string decoded = GeminiUsageCollector.DecodeWindowsCredential(encoding.GetBytes(json + '\0'));
            Check(GeminiUsageCollector.TryParseCredentialPayload(decoded, null, false, out var credential), "Windows credential encoding");
            Check(credential.AccessToken == accessToken && credential.RefreshToken == refreshToken, "Windows credential fields");
        }

        byte[] oauth = [.. Field(1, Encoding.UTF8.GetBytes(accessToken)), .. Field(2, Encoding.UTF8.GetBytes("Bearer")), .. Field(3, Encoding.UTF8.GetBytes(refreshToken))];
        byte[] row = Field(1, Encoding.UTF8.GetBytes(Convert.ToBase64String(oauth)));
        byte[] entry = [.. Field(1, Encoding.UTF8.GetBytes("oauthTokenInfoSentinelKey")), .. Field(2, row)];
        byte[] topic = Field(1, entry);
        Check(GeminiUsageCollector.TryParseCredentialPayload(Convert.ToBase64String(topic), null, false, out var ide), "IDE Topic/Entry/Row credential");
        Check(ide.AccessToken == accessToken && ide.RefreshToken == refreshToken, "IDE credential fields");
        Check(GeminiUsageCollector.TryParseCredentialPayload(Convert.ToBase64String(oauth), null, false, out var flat), "flat protobuf remains supported");
        Check(flat.AccessToken == accessToken, "flat protobuf token");
        Check(!GeminiUsageCollector.TryParseCredentialPayload(Convert.ToBase64String(topic[..^4]), null, false, out _), "truncated IDE blob is rejected");
        Check(!GeminiUsageCollector.TryParseCredentialPayload(Convert.ToBase64String(new byte[] { 10, 255, 255, 255, 255, 127 }), null, false, out _), "invalid protobuf length is rejected");
        return Task.CompletedTask;
    }

    internal static async Task RefreshPersistenceAsync()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "antigravity-cli");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "antigravity-oauth-token");
        DateTimeOffset now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

        foreach ((bool expired, bool rejectRefreshed) in new[] { (true, false), (true, true), (false, true) })
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                auth_method = "consumer",
                client_id = "fixture-client",
                client_secret = "fixture-secret",
                token = new { access_token = "old-token", refresh_token = "old-refresh", expiry = now.AddHours(expired ? -1 : 1).ToString("O"), token_type = "Bearer" },
            }));
            int refreshCount = 0;
            using HttpClient client = new(new Handler((request, _) =>
            {
                if (request.RequestUri!.Host == "oauth2.googleapis.com")
                {
                    refreshCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"access_token\":\"new-token\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}"),
                    });
                }
                if (rejectRefreshed)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }
                Check(request.Headers.Authorization?.Parameter == "new-token", "expired nested credentials refresh before quota requests");
                string body = request.RequestUri.AbsolutePath.EndsWith("loadCodeAssist", StringComparison.Ordinal)
                    ? "{\"cloudaicompanionProject\":\"test-project\",\"currentTier\":{\"id\":\"PRO\"}}"
                    : "{\"buckets\":[{\"bucketId\":\"gemini-5h\",\"remainingFraction\":0.5}]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }));
            GeminiUsageCollector collector = new(client, temp.Path, nowProvider: () => now);
            GeminiUsageDashboard dashboard = await collector.CollectDashboardAsync();
            Check(refreshCount == 1, "each collection refreshes OAuth at most once");
            Check(dashboard.Snapshot.IsAvailable == !rejectRefreshed, "quota availability after refresh");

            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = saved.RootElement;
            JsonElement token = root.GetProperty("token");
            Check(root.GetProperty("auth_method").GetString() == "consumer" && token.GetProperty("token_type").GetString() == "Bearer", "unrelated credential fields survive");
            Check(!root.TryGetProperty("access_token", out _) && !root.TryGetProperty("expiry_date", out _), "no shadow root-level credentials");
            Check(token.GetProperty("access_token").GetString() == "new-token" && token.GetProperty("refresh_token").GetString() == "new-refresh", "rotated tokens update original node");
            Check(token.GetProperty("expiry").GetDateTimeOffset() == now.AddHours(1), "CLI expiry format stays readable");
            Check(GeminiUsageCollector.TryParseCredentialPayload(root.GetRawText(), path, false, out var reloaded) && reloaded.ExpiresAt == now.AddHours(1), "saved credentials reload with new expiry");
        }
    }

    internal static async Task RefreshConfigurationAsync()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "oauth_creds.json");
        const string idVariable = "CODEXTRAY_GEMINI_CLI_CLIENT_ID";
        const string secretVariable = "CODEXTRAY_GEMINI_CLI_CLIENT_SECRET";
        string? originalId = Environment.GetEnvironmentVariable(idVariable);
        string? originalSecret = Environment.GetEnvironmentVariable(secretVariable);
        try
        {
            Environment.SetEnvironmentVariable(idVariable, "fixture-client");
            Environment.SetEnvironmentVariable(secretVariable, "fixture-secret");
            int requests = 0;
            using HttpClient client = new(new Handler(async (request, token) =>
            {
                requests++;
                string body = await request.Content!.ReadAsStringAsync(token);
                Check(request.RequestUri!.Host == "oauth2.googleapis.com" && body.Contains("client_id=fixture-client") && body.Contains("client_secret=fixture-secret"), "use the configured CLI pair");
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }));
            GeminiUsageCollector collector = new(client, temp.Path);
            foreach (string? fileClientId in new string?[] { "different-client", null })
            {
                string json = JsonSerializer.Serialize(new { client_id = fileClientId, refresh_token = "fixture-refresh" });
                File.WriteAllText(path, json);
                GeminiUsageDashboard result = await collector.CollectDashboardAsync();
                Check(!result.Snapshot.IsAvailable && requests == (fileClientId == null ? 1 : 0), "partial file configuration must not mix with environment credentials");
                Check(File.ReadAllText(path) == json, "failed or skipped refresh preserves the original login");
            }
            Environment.SetEnvironmentVariable(secretVariable, null);
            GeminiUsageDashboard missing = await collector.CollectDashboardAsync();
            Check(!missing.Snapshot.IsAvailable && requests == 1, "an incomplete environment pair must not send a refresh token");
        }
        finally
        {
            Environment.SetEnvironmentVariable(idVariable, originalId);
            Environment.SetEnvironmentVariable(secretVariable, originalSecret);
        }
    }

    private static byte[] Field(int number, byte[] payload)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write7BitEncodedInt((number << 3) | 2);
        writer.Write7BitEncodedInt(payload.Length);
        writer.Write(payload);
        return stream.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
