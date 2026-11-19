using CodexTray.Core;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexTray.Tests;

internal static class OAuthCredentialConcurrencyTests
{
    internal static async Task RunAsync()
    {
        await GeminiChangesAsync();
        await GeminiRootPersistenceAsync();
        await GrokChangesAsync();
        await UnavailableWritebackAsync();
        await GrokChangedBeforeForcedRefreshAsync();
        AtomicReplacement();
    }

    private static async Task GeminiChangesAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        string directory = Path.Combine(temp.Path, "antigravity-cli");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "antigravity-oauth-token");
        string initial = GeminiCredential(now.AddHours(-1));

        foreach (string change in new[] { "switch", "delete", "entry", "quota" })
        {
            File.WriteAllText(path, initial);
            string? external = null;
            int quotaRequests = 0;
            using HttpClient client = new(new Handler((request, _) =>
            {
                if (request.RequestUri!.Host == "oauth2.googleapis.com")
                {
                    if (change == "delete")
                    {
                        File.Delete(path);
                    }
                    else if (change != "quota")
                    {
                        JsonObject root = JsonNode.Parse(initial)!.AsObject();
                        JsonObject token = root["token"]!.AsObject();
                        token[change == "switch" ? "access_token" : "token_type"] = change == "switch" ? "other-account" : "changed";
                        external = root.ToJsonString();
                        File.WriteAllText(path, external);
                    }

                    return Task.FromResult(JsonResponse("{\"access_token\":\"refreshed\",\"refresh_token\":\"rotated\",\"expires_in\":3600}"));
                }

                quotaRequests++;
                if (change == "quota" && request.RequestUri.AbsolutePath.EndsWith("retrieveUserQuotaSummary", StringComparison.Ordinal))
                {
                    external = GeminiCredential(now.AddHours(1)).Replace("old-access", "other-account", StringComparison.Ordinal);
                    File.WriteAllText(path, external);
                }
                return Task.FromResult(GeminiQuotaResponse(request));
            }));
            GeminiUsageDashboard result = await new GeminiUsageCollector(client, temp.Path, nowProvider: () => now).CollectDashboardAsync();
            Check(!result.Snapshot.IsAvailable, $"Gemini {change}: old account usage is not published");
            Check(change == "quota" ? quotaRequests > 0 : quotaRequests == 0, $"Gemini {change}: no quota request after a refresh conflict");
            Check(change == "delete" ? !File.Exists(path) : File.ReadAllText(path) == external, $"Gemini {change}: external credentials remain intact");
        }

        File.WriteAllText(path, GeminiCredential(now.AddHours(1)));
        int refreshRequests = 0;
        using (HttpClient client = new(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                refreshRequests++;
                return Task.FromResult(JsonResponse("{\"access_token\":\"refreshed\",\"expires_in\":3600}"));
            }

            File.Delete(path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        })))
        {
            GeminiUsageDashboard result = await new GeminiUsageCollector(client, temp.Path, nowProvider: () => now).CollectDashboardAsync();
            Check(!result.Snapshot.IsAvailable && refreshRequests == 0 && !File.Exists(path), "Gemini rejects a changed login before a forced refresh");
        }

        string store = GeminiCredential(now.AddHours(-1));
        int expiredStoreRequests = 0;
        using (HttpClient client = new(new Handler((_, _) =>
        {
            expiredStoreRequests++;
            return Task.FromResult(JsonResponse("{\"access_token\":\"refreshed\",\"expires_in\":3600}"));
        })))
        {
            GeminiUsageDashboard result = await new GeminiUsageCollector(client, temp.Path, credentialStoreReader: () => store, nowProvider: () => now).CollectDashboardAsync();
            Check(!result.Snapshot.IsAvailable && expiredStoreRequests == 0 && result.FailureKind == GeminiUsageFailureKind.OwnerRefreshRequired,
                "expired non-file store does not send direct OAuth refresh requests");
        }

        store = GeminiCredential(now.AddHours(1));
        int activeStoreQuotaRequests = 0;
        using (HttpClient client = new(new Handler((request, _) =>
        {
            activeStoreQuotaRequests++;
            if (request.RequestUri!.AbsolutePath.EndsWith("retrieveUserQuotaSummary", StringComparison.Ordinal))
            {
                store = store.Replace("old-access", "other-account", StringComparison.Ordinal);
            }
            return Task.FromResult(GeminiQuotaResponse(request));
        })))
        {
            GeminiUsageDashboard result = await new GeminiUsageCollector(client, temp.Path, credentialStoreReader: () => store, nowProvider: () => now).CollectDashboardAsync();
            Check(!result.Snapshot.IsAvailable && activeStoreQuotaRequests > 0 && result.FailureKind == GeminiUsageFailureKind.CredentialChanged,
                "Gemini detects a changed credential-store login during quota collection");
        }
    }

    private static async Task GeminiRootPersistenceAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        string path = Path.Combine(temp.Path, "oauth_creds.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            access_token = "old-access",
            refresh_token = "old-refresh",
            expiry_date = now.AddHours(-1).ToUnixTimeMilliseconds(),
            client_id = "fixture-client",
            client_secret = "fixture-secret",
            marker = "preserved",
        }));
        using HttpClient client = new(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "oauth2.googleapis.com"
            ? JsonResponse("{\"access_token\":\"refreshed\",\"refresh_token\":\"rotated\",\"expires_in\":3600}")
            : GeminiQuotaResponse(request))));
        GeminiUsageDashboard result = await new GeminiUsageCollector(client, temp.Path, nowProvider: () => now).CollectDashboardAsync();
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
        Check(result.Snapshot.IsAvailable, "unchanged Gemini CLI credentials refresh successfully");
        Check(saved.RootElement.GetProperty("access_token").GetString() == "refreshed" && saved.RootElement.GetProperty("refresh_token").GetString() == "rotated",
              "Gemini CLI tokens rotate at the root");
        Check(saved.RootElement.GetProperty("expiry_date").GetInt64() == now.AddHours(1).ToUnixTimeMilliseconds() && saved.RootElement.GetProperty("marker").GetString() == "preserved",
              "Gemini CLI expiry and unrelated fields survive");
        Check(!saved.RootElement.TryGetProperty("token", out _), "Gemini CLI does not gain a nested token");
    }

    private static async Task GrokChangesAsync()
    {
        using TempDirectory temp = new();
        string? previous = Environment.GetEnvironmentVariable("GROK_HOME");
        Environment.SetEnvironmentVariable("GROK_HOME", temp.Path);
        try
        {
            const string entryKey = "https://auth.x.ai::fixture-client";
            string path = Path.Combine(temp.Path, "auth.json");
            string initial = GrokDocument(entryKey);
            foreach (string change in new[] { "switch", "delete", "entry", "remove-entry", "selected-entry", "quota", "other-entry" })
            {
                File.WriteAllText(path, initial);
                string? external = null;
                int quotaRequests = 0;
                using HttpClient client = new(new Handler((request, _) =>
                {
                    if (request.RequestUri!.Host == "auth.x.ai")
                    {
                        if (change == "delete")
                        {
                            File.Delete(path);
                        }
                        else if (change != "quota")
                        {
                            JsonObject root = JsonNode.Parse(initial)!.AsObject();
                            if (change == "remove-entry")
                            {
                                root.Remove(entryKey);
                            }
                            else if (change == "selected-entry")
                            {
                                JsonObject replacement = new() { ["https://auth.x.ai::other"] = new JsonObject { ["key"] = "other-account" } };
                                foreach (var property in root.ToArray())
                                {
                                    replacement[property.Key] = property.Value?.DeepClone();
                                }
                                root = replacement;
                            }
                            else if (change == "other-entry")
                            {
                                root["https://example.test/sign-in"]!["marker"] = "other-entry-updated";
                            }
                            else
                            {
                                root[entryKey]![change == "switch" ? "key" : "marker"] = change == "switch" ? "other-account" : "entry-updated";
                            }
                            external = root.ToJsonString();
                            File.WriteAllText(path, external);
                        }

                        return Task.FromResult(JsonResponse("{\"access_token\":\"refreshed\",\"refresh_token\":\"rotated\",\"id_token\":\"new-id\",\"expires_in\":3600}"));
                    }

                    if (request.RequestUri.AbsolutePath.EndsWith("billing", StringComparison.Ordinal))
                    {
                        quotaRequests++;
                        if (change == "quota")
                        {
                            external = initial.Replace("old-access", "other-account", StringComparison.Ordinal);
                            File.WriteAllText(path, external);
                        }
                        return Task.FromResult(JsonResponse("{\"config\":{\"creditUsagePercent\":20,\"currentPeriod\":{\"end\":\"2027-01-01T00:00:00Z\"}}}"));
                    }
                    return Task.FromResult(JsonResponse("{\"subscription_tier_display\":\"Free\"}"));
                }));
                GrokUsageDashboard result = await new GrokUsageCollector(client).CollectDashboardAsync();
                if (change == "other-entry")
                {
                    using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
                    JsonElement selected = saved.RootElement.GetProperty(entryKey);
                    Check(result.Usage != null && quotaRequests == 1, "Grok refresh succeeds after an unrelated entry changes");
                    Check(selected.GetProperty("key").GetString() == "refreshed" && selected.GetProperty("refresh_token").GetString() == "rotated", "Grok rotates tokens in the selected entry");
                    Check(selected.GetProperty("marker").GetString() == "preserved" &&
                          saved.RootElement.GetProperty("https://example.test/sign-in").GetProperty("marker").GetString() == "other-entry-updated",
                          "Grok preserves selected metadata and an unrelated entry update");
                }
                else
                {
                    Check(result.Usage == null, $"Grok {change}: old account usage is not published");
                    Check(change == "quota" ? quotaRequests == 1 : quotaRequests == 0, $"Grok {change}: no quota request after a refresh conflict");
                    Check(change == "delete" ? !File.Exists(path) : File.ReadAllText(path) == external, $"Grok {change}: external credentials remain intact");
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", previous);
        }
    }

    private static void AtomicReplacement()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "credentials.json");
        File.WriteAllText(path, "before");
        Check(AtomicFile.CanReplaceAllText(path, "before") && File.ReadAllText(path) == "before", "transaction probe rolls back the original data");
        Check(!AtomicFile.TryReplaceAllText(path, "different", "updated") && File.ReadAllText(path) == "before", "atomic replacement rejects changed contents");
        using (FileStream conflictingReader = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check(!AtomicFile.CanReplaceAllText(path, "before") && !AtomicFile.TryReplaceAllText(path, "before", "changed"), "transaction refuses an incompatible external file handle");
        }
        Check(File.ReadAllText(path) == "before", "failed transaction preserves original data");
        Check(AtomicFile.TryReplaceAllText(path, "before", "updated") && File.ReadAllText(path) == "updated", "atomic replacement commits unchanged contents");
        File.Delete(path);
        Check(!AtomicFile.CanReplaceAllText(path, "updated") && !AtomicFile.TryReplaceAllText(path, "updated", "recreated") && !File.Exists(path), "atomic replacement never recreates a deleted file");
        Check(!Directory.EnumerateFiles(temp.Path).Any(), "atomic replacement cleans temporary files");
    }

    private static async Task UnavailableWritebackAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        string directory = Path.Combine(temp.Path, "antigravity-cli");
        Directory.CreateDirectory(directory);
        string geminiPath = Path.Combine(directory, "antigravity-oauth-token");
        foreach (bool expired in new[] { true, false })
        {
            string original = GeminiCredential(now.AddMinutes(expired ? -1 : 2));
            File.WriteAllText(geminiPath, original);
            int refreshRequests = 0;
            using FileStream conflictingReader = new(geminiPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using HttpClient client = new(new Handler((request, _) =>
            {
                if (request.RequestUri!.Host == "oauth2.googleapis.com")
                {
                    refreshRequests++;
                }
                return Task.FromResult(GeminiQuotaResponse(request));
            }));
            GeminiUsageDashboard result = await new GeminiUsageCollector(client, temp.Path, nowProvider: () => now).CollectDashboardAsync();
            Check(result.Snapshot.IsAvailable == !expired && refreshRequests == 0, "Gemini avoids unsafe rotation but retains a still-valid access token");
            Check(File.ReadAllText(geminiPath) == original, "unavailable Gemini writeback leaves credentials intact");
        }

        string? previous = Environment.GetEnvironmentVariable("GROK_HOME");
        Environment.SetEnvironmentVariable("GROK_HOME", temp.Path);
        try
        {
            string grokPath = Path.Combine(temp.Path, "auth.json");
            foreach (bool expired in new[] { true, false })
            {
                string original = GrokDocument("https://auth.x.ai::fixture-client", DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 2));
                File.WriteAllText(grokPath, original);
                int refreshRequests = 0;
                using FileStream conflictingReader = new(grokPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using HttpClient client = new(new Handler((request, _) =>
                {
                    if (request.RequestUri!.Host == "auth.x.ai")
                    {
                        refreshRequests++;
                    }
                    return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("billing", StringComparison.Ordinal)
                        ? JsonResponse("{\"config\":{\"creditUsagePercent\":20,\"currentPeriod\":{\"end\":\"2027-01-01T00:00:00Z\"}}}")
                        : JsonResponse("{\"subscription_tier_display\":\"Free\"}"));
                }));
                GrokUsageDashboard result = await new GrokUsageCollector(client).CollectDashboardAsync();
                Check((result.Usage != null) == !expired && refreshRequests == 0, "Grok avoids unsafe rotation but retains a still-valid access token");
                Check(File.ReadAllText(grokPath) == original, "unavailable Grok writeback leaves credentials intact");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", previous);
        }
    }

    private static async Task GrokChangedBeforeForcedRefreshAsync()
    {
        using TempDirectory temp = new();
        string? previous = Environment.GetEnvironmentVariable("GROK_HOME");
        Environment.SetEnvironmentVariable("GROK_HOME", temp.Path);
        try
        {
            string path = Path.Combine(temp.Path, "auth.json");
            string original = GrokDocument("https://auth.x.ai::fixture-client", DateTimeOffset.UtcNow.AddHours(1));
            string external = original.Replace("old-access", "other-account", StringComparison.Ordinal);
            File.WriteAllText(path, original);
            int refreshRequests = 0;
            using HttpClient client = new(new Handler((request, _) =>
            {
                if (request.RequestUri!.Host == "auth.x.ai")
                {
                    refreshRequests++;
                    return Task.FromResult(JsonResponse("{\"access_token\":\"refreshed\",\"expires_in\":3600}"));
                }
                File.WriteAllText(path, external);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }));
            GrokUsageDashboard result = await new GrokUsageCollector(client).CollectDashboardAsync();
            Check(result.Usage == null && refreshRequests == 0 && File.ReadAllText(path) == external, "Grok never refreshes a different account after an old account gets 401");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", previous);
        }
    }

    private static string GeminiCredential(DateTimeOffset expiry) => JsonSerializer.Serialize(new
    {
        client_id = "fixture-client",
        client_secret = "fixture-secret",
        token = new { access_token = "old-access", refresh_token = "old-refresh", expiry = expiry.ToString("O"), token_type = "Bearer" },
    });

    private static string GrokDocument(string entryKey, DateTimeOffset? expiresAt = null) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        [entryKey] = new { key = "old-access", refresh_token = "old-refresh", expires_at = (expiresAt ?? DateTimeOffset.UtcNow.AddHours(-1)).ToString("O"),
                           user_id = "old-user", marker = "preserved" },
        ["https://example.test/sign-in"] = new { key = "other-entry", marker = "untouched" },
    });

    private static HttpResponseMessage GeminiQuotaResponse(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("loadCodeAssist", StringComparison.Ordinal)
        ? JsonResponse("{\"cloudaicompanionProject\":\"test-project\"}")
        : JsonResponse("{\"buckets\":[{\"bucketId\":\"gemini-5h\",\"remainingFraction\":0.5}]}");

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

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
