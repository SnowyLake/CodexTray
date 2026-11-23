using CodexTray.App;
using CodexTray.Core;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace CodexTray.Tests;

internal static class GeminiCredentialTests
{
    internal static Task EncodingsAsync()
    {
        const string accessToken = "ya29.test-access-token";
        const string refreshToken = "1//test-refresh-token";
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

    internal static async Task RefreshBufferAndOwnerRecoveryAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 11, 19, 15, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset expiry = now.AddMinutes(6);
        string BuildStore(string accessToken, DateTimeOffset tokenExpiry, string authMethod = "consumer") => JsonSerializer.Serialize(new
        {
            auth_method = authMethod,
            id_token_email = "user@example.test",
            token = new
            {
                access_token = accessToken,
                token_type = "Bearer",
                refresh_token = "1//fixture-refresh",
                expiry = tokenExpiry.ToString("O"),
                expires_in = 3600,
            },
        });

        string quotaJson = JsonSerializer.Serialize(new
        {
            groups = new object[]
            {
                new
                {
                    name = "Gemini",
                    buckets = new object[]
                    {
                        new { id = "gemini-5h", window = "5h", remaining_fraction = 0.8, reset_time = expiry.AddHours(4).ToUniversalTime().ToString("O") },
                        new { id = "gemini-weekly", window = "weekly", remaining_fraction = 0.9, reset_time = expiry.AddDays(5).ToUniversalTime().ToString("O") },
                    },
                },
            },
        });

        // 1. Production Credential Manager shape without client pair: succeeds through the 5-minute buffer with zero OAuth POSTs, fails at actual expiry.
        string store = BuildStore("ya29.token-v1", expiry);
        int tokenPosts = 0;
        int quotaGets = 0;
        using (HttpClient client = new(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                tokenPosts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }
            quotaGets++;
            string body = request.RequestUri.AbsolutePath.EndsWith("loadCodeAssist", StringComparison.Ordinal)
                ? "{\"cloudaicompanionProject\":\"p1\",\"paidTier\":{\"id\":\"PRO\"}}"
                : quotaJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        })))
        {
            GeminiUsageCollector collector = new(client, temp.Path, credentialStoreReader: () => store, nowProvider: () => now);
            foreach (DateTimeOffset probe in new[] { expiry.AddMinutes(-6), expiry.AddMinutes(-5), expiry.AddSeconds(-1) })
            {
                now = probe;
                GeminiUsageDashboard ok = await collector.CollectDashboardAsync();
                Check(ok.Snapshot.IsAvailable && ok.Snapshot.SubscriptionTier == "PRO", $"valid token stays usable at {probe:O}");
            }
            Check(tokenPosts == 0 && quotaGets >= 3, "no direct token POST during refresh buffer");

            now = expiry;
            GeminiUsageDashboard expiredResult = await collector.CollectDashboardAsync();
            Check(!expiredResult.Snapshot.IsAvailable && expiredResult.FailureKind == GeminiUsageFailureKind.OwnerRefreshRequired
                && expiredResult.CredentialOwner == GeminiCredentialOwner.AntigravityCli && tokenPosts == 0,
                "expired store reports owner refresh without direct OAuth POST");
        }

        // 2. Local file without client pair also keeps working inside the 5-minute buffer.
        string agyDir = Path.Combine(temp.Path, "antigravity-cli");
        Directory.CreateDirectory(agyDir);
        string filePath = Path.Combine(agyDir, "antigravity-oauth-token");
        File.WriteAllText(filePath, BuildStore("ya29.file-token", expiry));
        now = expiry.AddMinutes(-2);
        using (HttpClient client = new(new Handler((request, _) =>
        {
            Check(request.RequestUri!.Host != "oauth2.googleapis.com", "file without client pair must not POST in buffer");
            string body = request.RequestUri.AbsolutePath.EndsWith("loadCodeAssist", StringComparison.Ordinal)
                ? "{\"cloudaicompanionProject\":\"p1\",\"paidTier\":{\"id\":\"ULTRA\"}}"
                : quotaJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        })))
        {
            GeminiUsageDashboard bufferedFile = await new GeminiUsageCollector(client, temp.Path, nowProvider: () => now).CollectDashboardAsync();
            Check(bufferedFile.Snapshot.IsAvailable && bufferedFile.Snapshot.SubscriptionTier == "ULTRA", "file login stays usable during 5-minute buffer");
        }
        File.Delete(filePath);

        // 3. Read-only external store refreshes silently using discovered local OAuth client pair, caches in memory, and refreshes again across expiry boundaries without mutating the store.
        string syntheticClientId = ScannerFixtureClientId('a');
        string syntheticClientSecret = ScannerFixtureClientSecret('a');
        string fakeBinaryPath = Path.Combine(temp.Path, "fake-agy.bin");
        File.WriteAllText(fakeBinaryPath, $"const id=\"{syntheticClientId}\";const sec=\"{syntheticClientSecret}\";");

        now = expiry.AddMinutes(10);
        store = BuildStore("ya29.expired-1", expiry);
        string originalStore = store;
        int refreshPosts = 0;
        string expectedToken = "ya29.refreshed-1";
        using (HttpClient client = new(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                refreshPosts++;
                string form = await request.Content!.ReadAsStringAsync(token);
                Check(form.Contains("client_id=" + Uri.EscapeDataString(syntheticClientId), StringComparison.Ordinal)
                    && form.Contains("client_secret=" + Uri.EscapeDataString(syntheticClientSecret), StringComparison.Ordinal)
                    && form.Contains("refresh_token=" + Uri.EscapeDataString("1//fixture-refresh"), StringComparison.Ordinal),
                    "direct silent refresh uses discovered OAuth client pair and read-only refresh token");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { access_token = expectedToken, expires_in = 3600 })),
                };
            }

            Check(request.Headers.Authorization?.Parameter == expectedToken, "Cloud Code uses in-memory refreshed access token");
            string body = request.RequestUri.AbsolutePath.EndsWith("loadCodeAssist", StringComparison.Ordinal)
                ? "{\"cloudaicompanionProject\":\"p1\",\"paidTier\":{\"id\":\"PRO\"}}"
                : quotaJson;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        })))
        {
            GeminiUsageCollector collector = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                clientDiscoveryPaths: [fakeBinaryPath]);

            GeminiUsageDashboard first = await collector.CollectDashboardAsync();
            Check(first.Snapshot.IsAvailable && first.Snapshot.SubscriptionTier == "PRO" && first.Snapshot.Source == "cloud_code",
                "read-only external store restores full Cloud Code quota and subscription tier via direct silent refresh");
            Check(refreshPosts == 1 && store == originalStore, "external store stays untouched while refreshing once");

            // Subsequent minute poll reuses private in-memory token without another OAuth POST.
            now = now.AddMinutes(1);
            GeminiUsageDashboard nextMinute = await collector.CollectDashboardAsync();
            Check(nextMinute.Snapshot.IsAvailable && refreshPosts == 1, "in-memory token cache prevents redundant OAuth refreshes");

            // Proactive refresh at the 5-minute buffer before expiry fetches a new token cleanly.
            now = now.AddMinutes(55);
            expectedToken = "ya29.refreshed-2";
            GeminiUsageDashboard secondCycle = await collector.CollectDashboardAsync();
            Check(secondCycle.Snapshot.IsAvailable && refreshPosts == 2, "proactive refresh renews cached token at 5-minute buffer");

            // Switching accounts in the external store immediately evicts the cached token and uses the new account.
            store = BuildStore("ya29.new-account-valid", now.AddHours(1));
            expectedToken = "ya29.new-account-valid";
            GeminiUsageDashboard switched = await collector.CollectDashboardAsync();
            Check(switched.Snapshot.IsAvailable && refreshPosts == 2, "account change evicts old in-memory cached token");
        }

        // 4. Antigravity ACP file (antigravity-acp/acp_token.json) is discovered, refreshed, and reports AntigravityAcp owner on failure.
        string acpDir = Path.Combine(temp.Path, "antigravity-acp");
        Directory.CreateDirectory(acpDir);
        string acpPath = Path.Combine(acpDir, "acp_token.json");
        File.WriteAllText(acpPath, JsonSerializer.Serialize(new
        {
            client_id = syntheticClientId,
            client_secret = syntheticClientSecret,
            access_token = "ya29.acp-expired",
            refresh_token = "1//acp-refresh",
            expiry = now.AddHours(-1).ToString("O"),
        }));
        using (HttpClient client = new(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"ya29.acp-fresh\",\"expires_in\":3600}"),
                });
            }

            Check(request.Headers.Authorization?.Parameter == "ya29.acp-fresh", "ACP credential refreshes with its own client pair");
            string body = request.RequestUri.AbsolutePath.EndsWith("loadCodeAssist", StringComparison.Ordinal)
                ? "{\"cloudaicompanionProject\":\"p-acp\",\"paidTier\":{\"id\":\"ULTRA\"}}"
                : quotaJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        })))
        {
            GeminiUsageDashboard acpOk = await new GeminiUsageCollector(client, temp.Path, nowProvider: () => now).CollectDashboardAsync();
            Check(acpOk.Snapshot.IsAvailable && acpOk.Snapshot.SubscriptionTier == "ULTRA", "antigravity-acp/acp_token.json refreshes and loads quota");
            using JsonDocument savedAcp = JsonDocument.Parse(File.ReadAllText(acpPath));
            Check(savedAcp.RootElement.GetProperty("access_token").GetString() == "ya29.acp-fresh"
                && savedAcp.RootElement.TryGetProperty("expiry", out _), "ACP token file preserves RFC3339 expiry field");
        }

        File.WriteAllText(acpPath, JsonSerializer.Serialize(new
        {
            access_token = "ya29.acp-no-refresh",
            expiry = now.AddHours(-1).ToString("O"),
        }));
        using (HttpClient client = new(new Handler((_, _) => new TaskCompletionSource<HttpResponseMessage>().Task)))
        {
            GeminiUsageDashboard acpExpired = await new GeminiUsageCollector(client, temp.Path, nowProvider: () => now).CollectDashboardAsync();
            Check(!acpExpired.Snapshot.IsAvailable && acpExpired.FailureKind == GeminiUsageFailureKind.OwnerRefreshRequired
                && acpExpired.CredentialOwner == GeminiCredentialOwner.AntigravityAcp,
                "expired ACP login without refresh token maps to AntigravityAcp owner");
            TrayPopupViewModel acpVm = new(new AppSettings(), () => Task.CompletedTask);
            acpVm.UpdateGeminiDashboard(acpExpired);
            Check(acpVm.GeminiUpdatedAtDisplay == "Open Antigravity ACP", "ACP failure status text identifies Antigravity ACP");
        }
        File.Delete(acpPath);

        // 5. Transient refresh timeout backs off automatically and recovers on manual refresh or credential update.
        store = BuildStore("ya29.timeout-expired", now.AddHours(-1));
        int timeoutRefreshCalls = 0;
        using (HttpClient client = new(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                timeoutRefreshCalls++;
                throw new TaskCanceledException("simulated OAuth refresh timeout");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) });
        })))
        {
            GeminiUsageCollector timeoutCollector = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                clientDiscoveryPaths: [fakeBinaryPath]);
            GeminiUsageDashboard t1 = await timeoutCollector.CollectDashboardAsync();
            Check(!t1.Snapshot.IsAvailable && t1.FailureKind == GeminiUsageFailureKind.Timeout && timeoutRefreshCalls == 1, "OAuth refresh timeout is classified");
            now = now.AddMinutes(1);
            GeminiUsageDashboard t2 = await timeoutCollector.CollectDashboardAsync();
            Check(!t2.Snapshot.IsAvailable && timeoutRefreshCalls == 1, "backoff suppresses immediate minute retry");
            GeminiUsageDashboard tManual = await timeoutCollector.CollectDashboardAsync(forceRefresh: true);
            Check(!tManual.Snapshot.IsAvailable && timeoutRefreshCalls == 2, "manual refresh bypasses backoff");
            store = BuildStore("ya29.user-renewed", now.AddHours(1));
            GeminiUsageDashboard recovered = await timeoutCollector.CollectDashboardAsync();
            Check(recovered.Snapshot.IsAvailable, "updated credential immediately clears backoff");
        }

        // 6. HTTP 403 maps to AccessDenied, invalid_grant redacts error_description, and UI status/tooltip/diagnostics stay safe.
        const string sentinel = "SENSITIVE_OAUTH_LEAK_TOKEN";
        File.WriteAllText(filePath, JsonSerializer.Serialize(new
        {
            client_id = "fixture-client",
            client_secret = "fixture-secret",
            token = new { access_token = "ya29.old", refresh_token = "1//revoked", expiry = now.AddHours(-1).ToString("O") },
        }));
        using (HttpClient client = new(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"" + sentinel + "\"}"),
        }))))
        {
            GeminiUsageDashboard revoked = await new GeminiUsageCollector(client, temp.Path, nowProvider: () => now).CollectDashboardAsync();
            Check(!revoked.Snapshot.IsAvailable && revoked.FailureKind == GeminiUsageFailureKind.OwnerRefreshRequired
                && !revoked.StatusMessage.Contains(sentinel, StringComparison.Ordinal),
                "invalid_grant maps to owner refresh without leaking error_description");

            TrayPopupViewModel vm = new(new AppSettings(), () => Task.CompletedTask);
            vm.UpdateGeminiTokenCost(new TokenCostStatistics { LastTwentyFourHours = new TokenCostSummary { TotalTokens = 100 } });
            vm.UpdateGeminiDashboard(revoked);
            Check(vm.GeminiUpdatedAtDisplay == "Open Antigravity CLI · Cost updated", "actionable Gemini status line when cost is updated");
            Check(vm.GeminiStatusTooltip != null && !vm.GeminiStatusTooltip.Contains(sentinel, StringComparison.Ordinal), "failure tooltip is enabled and redacted");
            string diagnostics = vm.BuildDiagnostics(PageItem.Gemini);
            Check(diagnostics.Contains("Authentication") && !diagnostics.Contains(sentinel, StringComparison.Ordinal), "copied diagnostics classify auth failure safely");

            vm.UpdateGeminiDashboard(new GeminiUsageDashboard
            {
                Snapshot = new GeminiUsageSnapshot { Source = "cloud_code", UpdatedAt = now, IsAvailable = true, SubscriptionTier = "PRO" },
                StatusMessage = string.Empty,
                FailureKind = GeminiUsageFailureKind.None,
            });
            Check(vm.GeminiStatusTooltip == null, "successful Gemini refresh clears failure tooltip");
        }
        File.Delete(filePath);

        store = BuildStore("ya29.forbidden-token", now.AddHours(1));
        using (HttpClient client = new(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { ReasonPhrase = sentinel }))))
        {
            GeminiUsageDashboard denied = await new GeminiUsageCollector(client, temp.Path, credentialStoreReader: () => store, nowProvider: () => now).CollectDashboardAsync();
            Check(!denied.Snapshot.IsAvailable && denied.FailureKind == GeminiUsageFailureKind.AccessDenied
                && !denied.StatusMessage.Contains(sentinel, StringComparison.Ordinal),
                "HTTP 403 maps to AccessDenied without leaking reason phrase");
        }
    }

    internal static async Task ReviewRegressionsAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 11, 19, 17, 0, 0, TimeSpan.FromHours(8));
        string syntheticClientId = ScannerFixtureClientId('a');
        string syntheticClientSecret = ScannerFixtureClientSecret('a');
        string BuildStore(string accessToken, DateTimeOffset tokenExpiry, string authMethod = "consumer", string? tier = null) => JsonSerializer.Serialize(new
        {
            auth_method = authMethod,
            tier,
            token = new
            {
                access_token = accessToken,
                token_type = "Bearer",
                refresh_token = "1//fixture-refresh",
                expiry = tokenExpiry.ToString("O"),
                expires_in = 3600,
            },
        });
        string quotaJson = JsonSerializer.Serialize(new
        {
            groups = new object[]
            {
                new
                {
                    name = "Gemini",
                    buckets = new object[]
                    {
                        new { id = "gemini-5h", window = "5h", remaining_fraction = 0.75, reset_time = now.AddHours(4).ToUniversalTime().ToString("O") },
                    },
                },
            },
        });

        // 1. Streaming binary scanner handles single tokens straddling 1 MiB chunk boundaries, packed Go .rodata strings, multi-client Go binaries, and JS proximity selection.
        string goBinaryPath = Path.Combine(temp.Path, "packed-rodata.exe");
        using (FileStream fs = File.Create(goBinaryPath))
        {
            // Place syntheticClientId directly straddling the 1 MiB chunk boundary and syntheticClientSecret straddling the 2 MiB boundary.
            const int chunk = 1024 * 1024;
            byte[] filler1 = new byte[chunk - 30];
            Array.Fill(filler1, (byte)'X');
            fs.Write(filler1);
            byte[] idBytes = Encoding.ASCII.GetBytes(syntheticClientId + "trailingString");
            fs.Write(idBytes);
            int toSecondChunk = (2 * chunk) - (int)fs.Position - 12;
            byte[] filler2 = new byte[toSecondChunk];
            Array.Fill(filler2, (byte)'Y');
            fs.Write(filler2);
            fs.Write(Encoding.ASCII.GetBytes(syntheticClientSecret + "Bar\0"));
        }
        Check(GeminiUsageCollector.TryScanOAuthClientPairFromFile(goBinaryPath, out string scannedId, out string scannedSecret)
            && scannedId == syntheticClientId && scannedSecret == syntheticClientSecret,
            "streaming scanner extracts single client ID and GOCSPX secret straddling 1 MiB chunk boundaries without swallowing packed neighbors");

        string secondClientId = ScannerFixtureClientId('b');
        string secondClientSecret = ScannerFixtureClientSecret('b');
        string multiGoBinaryPath = Path.Combine(temp.Path, "multi-rodata.exe");
        using (FileStream fs = File.Create(multiGoBinaryPath))
        {
            byte[] pad = new byte[10000];
            Array.Fill(pad, (byte)'Z');
            fs.Write(Encoding.ASCII.GetBytes("foo" + syntheticClientId + "bar" + secondClientId + "baz"));
            fs.Write(pad);
            fs.Write(Encoding.ASCII.GetBytes("a" + syntheticClientSecret + "xyz" + secondClientSecret + "uvw"));
        }
        IReadOnlyList<GeminiUsageCollector.OAuthClientPair> multiPairs = GeminiUsageCollector.ScanOAuthClientPairsFromFile(multiGoBinaryPath);
        Check(multiPairs.Count == 4, $"multi-client Go binary yields bounded cross-product pairs (actual {multiPairs.Count})");

        string multiStore = BuildStore("ya29.multi-expired", now.AddHours(-1));
        int multiRefreshAttempts = 0;
        using (HttpClient client = new(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                multiRefreshAttempts++;
                string form = await request.Content!.ReadAsStringAsync(token);
                if (form.Contains("client_id=" + Uri.EscapeDataString(secondClientId), StringComparison.Ordinal)
                    && form.Contains("client_secret=" + Uri.EscapeDataString(secondClientSecret), StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"access_token\":\"ya29.multi-ok\",\"expires_in\":3600}"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"invalid_client\"}"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) };
        })))
        {
            GeminiUsageCollector multiCollector = new(
                client,
                temp.Path,
                credentialStoreReader: () => multiStore,
                nowProvider: () => now,
                clientDiscoveryPaths: [multiGoBinaryPath]);
            GeminiUsageDashboard firstMulti = await multiCollector.CollectDashboardAsync();
            Check(firstMulti.Snapshot.IsAvailable && multiRefreshAttempts == 4, "multi-client Go binary finds the matching OAuth pair across candidates");
            int attemptsBeforeSecond = multiRefreshAttempts;
            now = now.AddMinutes(56);
            GeminiUsageDashboard secondMulti = await multiCollector.CollectDashboardAsync();
            Check(secondMulti.Snapshot.IsAvailable && multiRefreshAttempts == attemptsBeforeSecond + 1, "verified OAuth client pair is cached and succeeds on first attempt next cycle");
        }

        string jsBundlePath = Path.Combine(temp.Path, "bundle.js");
        string unrelatedClientId = ScannerFixtureClientId('c');
        File.WriteAllText(jsBundlePath,
            $"const other=\"{unrelatedClientId}\";" + new string(' ', 6000) + $"const id=\"{syntheticClientId}\",secret=\"{syntheticClientSecret}\";");
        Check(GeminiUsageCollector.TryScanOAuthClientPairFromFile(jsBundlePath, out string nearbyId, out string nearbySecret)
            && nearbyId == syntheticClientId && nearbySecret == syntheticClientSecret,
            "scanner prefers the client ID adjacent to the GOCSPX secret over distant unrelated IDs");

        // 1b. Multi-file discovery tries subsequent files if the first file's client pair does not match, and transient scan locks are not cached as empty.
        string secondFilePath = Path.Combine(temp.Path, "second-bundle.js");
        File.WriteAllText(secondFilePath, $"const id=\"{secondClientId}\",secret=\"{secondClientSecret}\";");
        using (HttpClient client = new(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                string form = await request.Content!.ReadAsStringAsync(token);
                if (form.Contains("client_id=" + Uri.EscapeDataString(secondClientId), StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"ya29.second-file\",\"expires_in\":3600}") };
                }
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"invalid_client\"}") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) };
        })))
        {
            GeminiUsageCollector multiFileCollector = new(
                client,
                temp.Path,
                credentialStoreReader: () => multiStore,
                nowProvider: () => now,
                clientDiscoveryPaths: [jsBundlePath, secondFilePath]);
            GeminiUsageDashboard multiFileOk = await multiFileCollector.CollectDashboardAsync();
            Check(multiFileOk.Snapshot.IsAvailable, "discovery continues to subsequent candidate files when the first file's client pair returns invalid_client");
        }

        string lockedScanFile = Path.Combine(temp.Path, "locked-scan.js");
        File.WriteAllText(lockedScanFile, $"const id=\"{syntheticClientId}\",secret=\"{syntheticClientSecret}\";");
        using (HttpClient client = new(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"ya29.after-unlock\",\"expires_in\":3600}") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) });
        })))
        {
            GeminiUsageCollector lockScanCollector = new(
                client,
                temp.Path,
                credentialStoreReader: () => multiStore,
                nowProvider: () => now,
                clientDiscoveryPaths: [lockedScanFile]);
            using (FileStream exclusiveLock = new(lockedScanFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                GeminiUsageDashboard lockedFail = await lockScanCollector.CollectDashboardAsync();
                Check(!lockedFail.Snapshot.IsAvailable && lockedFail.FailureKind == GeminiUsageFailureKind.LocalDataError,
                    "transient client-discovery I/O error reports LocalDataError instead of poisoning cache");
            }

            GeminiUsageDashboard unlockedOk = await lockScanCollector.CollectDashboardAsync(forceRefresh: true);
            Check(unlockedOk.Snapshot.IsAvailable, "manual refresh after unlocking discovery file succeeds without caching empty scan");
        }

        // 1c. ResetState during an in-flight OAuth 200 OK blocks both local file writeback and follow-up quota requests; in-flight TxF lock falls back to in-memory cache; alternate expiry fields update in place.
        string resetAcpDir = Path.Combine(temp.Path, "antigravity-acp");
        Directory.CreateDirectory(resetAcpDir);
        string resetAcpFile = Path.Combine(resetAcpDir, "acp_token.json");
        string originalResetAcp = JsonSerializer.Serialize(new
        {
            client_id = syntheticClientId,
            client_secret = syntheticClientSecret,
            access_token = "ya29.before-reset",
            refresh_token = "1//reset-refresh",
            expires_at = now.AddHours(-1).ToString("O"),
        });
        File.WriteAllText(resetAcpFile, originalResetAcp);
        TaskCompletionSource enteredPostResetRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releasePostResetRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int postResetQuotaCalls = 0;
        using (HttpClient client = new(new Handler(async (request, _) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                enteredPostResetRefresh.TrySetResult();
                await releasePostResetRefresh.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"ya29.must-not-write\",\"expires_in\":3600}") };
            }
            postResetQuotaCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) };
        })))
        {
            GeminiUsageCollector postResetCollector = new(client, temp.Path, nowProvider: () => now);
            Task<GeminiUsageDashboard> staleWriteRun = postResetCollector.CollectDashboardAsync();
            await enteredPostResetRefresh.Task;
            postResetCollector.ResetState();
            releasePostResetRefresh.TrySetResult();
            GeminiUsageDashboard staleWriteResult = await staleWriteRun;
            Check(!staleWriteResult.Snapshot.IsAvailable && postResetQuotaCalls == 0 && File.ReadAllText(resetAcpFile) == originalResetAcp,
                "ResetState while OAuth 200 OK is in flight skips file writeback and follow-up quota requests");
        }

        // Alternate expiry fields (expires_at ISO string and expiryDate ms epoch) update in place so the next collect performs 0 OAuth POSTs.
        foreach (string expiryFixture in new[]
        {
            JsonSerializer.Serialize(new { client_id = syntheticClientId, client_secret = syntheticClientSecret, access_token = "ya29.old", refresh_token = "1//r", expires_at = now.AddHours(-1).ToString("O") }),
            JsonSerializer.Serialize(new { client_id = syntheticClientId, client_secret = syntheticClientSecret, access_token = "ya29.old", refresh_token = "1//r", expiryDate = now.AddHours(-1).ToUnixTimeMilliseconds() }),
        })
        {
            File.WriteAllText(resetAcpFile, expiryFixture);
            int altRefreshCalls = 0;
            using HttpClient client = new(new Handler((request, _) =>
            {
                if (request.RequestUri!.Host == "oauth2.googleapis.com")
                {
                    altRefreshCalls++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"ya29.alt-fresh\",\"expires_in\":3600}") });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) });
            }));
            GeminiUsageCollector altCollector = new(client, temp.Path, nowProvider: () => now);
            GeminiUsageDashboard alt1 = await altCollector.CollectDashboardAsync();
            GeminiUsageDashboard alt2 = await altCollector.CollectDashboardAsync();
            Check(alt1.Snapshot.IsAvailable && alt2.Snapshot.IsAvailable && altRefreshCalls == 1,
                "persisting refreshed tokens updates existing expires_at/expiryDate fields in place without re-refreshing");
        }

        // In-flight TxF lock acquired after OAuth starts falls back to in-memory cache without reporting CredentialChanged.
        File.WriteAllText(resetAcpFile, originalResetAcp);
        FileStream? inFlightReadLock = null;
        int inFlightLockRefreshes = 0;
        try
        {
            using HttpClient client = new(new Handler((request, _) =>
            {
                if (request.RequestUri!.Host == "oauth2.googleapis.com")
                {
                    inFlightLockRefreshes++;
                    inFlightReadLock = new FileStream(resetAcpFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"ya29.inflight-mem\",\"expires_in\":3600}") });
                }
                Check(request.Headers.Authorization?.Parameter == "ya29.inflight-mem", "in-flight TxF lock falls back to in-memory access token");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) });
            }));
            GeminiUsageCollector inFlightLockCollector = new(client, temp.Path, nowProvider: () => now);
            GeminiUsageDashboard lockedCommit1 = await inFlightLockCollector.CollectDashboardAsync();
            GeminiUsageDashboard lockedCommit2 = await inFlightLockCollector.CollectDashboardAsync();
            Check(lockedCommit1.Snapshot.IsAvailable && lockedCommit2.Snapshot.IsAvailable && inFlightLockRefreshes == 1
                && File.ReadAllText(resetAcpFile) == originalResetAcp,
                "TxF commit failure caused by an in-flight read lock falls back to in-memory cache while preserving the file");
        }
        finally
        {
            inFlightReadLock?.Dispose();
            File.Delete(resetAcpFile);
        }

        // 2. ResetState never blocks on s_AuthLock and prevents stale in-flight 403s from poisoning post-reset collections.
        string store = BuildStore("ya29.in-flight", now.AddHours(1));
        TaskCompletionSource enteredRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (HttpClient client = new(new Handler(async (request, _) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                enteredRefresh.TrySetResult();
                await releaseRefresh.Task;
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) };
        })))
        {
            string expiredStore = BuildStore("ya29.expired-lock", now.AddHours(-1));
            GeminiUsageCollector collector = new(
                client,
                temp.Path,
                credentialStoreReader: () => expiredStore,
                nowProvider: () => now,
                clientDiscoveryPaths: [jsBundlePath]);

            Task<GeminiUsageDashboard> blockedRun = collector.CollectDashboardAsync();
            await enteredRefresh.Task;
            Task resetTask = Task.Run(collector.ResetState);
            Check(await Task.WhenAny(resetTask, Task.Delay(500)) == resetTask, "ResetState returns immediately while auth lock is held");
            releaseRefresh.TrySetResult();
            await blockedRun;
        }

        TaskCompletionSource enteredQuota = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseQuota = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int quotaAttempts = 0;
        using (HttpClient client = new(new Handler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("loadCodeAssist", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }

            int attempt = Interlocked.Increment(ref quotaAttempts);
            if (attempt == 1)
            {
                enteredQuota.TrySetResult();
                await releaseQuota.Task;
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) };
        })))
        {
            GeminiUsageCollector collector = new(client, temp.Path, credentialStoreReader: () => store, nowProvider: () => now);
            Task<GeminiUsageDashboard> staleRun = collector.CollectDashboardAsync();
            await enteredQuota.Task;
            collector.ResetState();
            releaseQuota.TrySetResult();
            GeminiUsageDashboard staleResult = await staleRun;
            Check(!staleResult.Snapshot.IsAvailable && staleResult.FailureKind == GeminiUsageFailureKind.AccessDenied, "in-flight 403 still returns AccessDenied");

            GeminiUsageDashboard freshRun = await collector.CollectDashboardAsync();
            Check(freshRun.Snapshot.IsAvailable && quotaAttempts == 2, "stale 403 before ResetState must not block subsequent automatic refreshes");
        }

        // 3. Ancestor directory junction is rejected by IsTrustedLocalFile.
        string realRoot = Path.Combine(temp.Path, "real-agy");
        string realBin = Path.Combine(realRoot, "bin");
        Directory.CreateDirectory(realBin);
        string realExe = Path.Combine(realBin, "agy.exe");
        File.WriteAllText(realExe, "stub");
        string junctionRoot = Path.Combine(temp.Path, "junction-agy");
        try
        {
            using Process? mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionRoot}\" \"{realRoot}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            mklink?.WaitForExit(5000);
            Check(Directory.Exists(junctionRoot), "test junction created");
            Check(GeminiUsageCollector.IsTrustedLocalFile(realExe), "normal local file path is trusted");
            Check(!GeminiUsageCollector.IsTrustedLocalFile(Path.Combine(junctionRoot, "bin", "agy.exe")), "grandparent junction is rejected");
        }
        finally
        {
            if (Directory.Exists(junctionRoot))
            {
                Directory.Delete(junctionRoot);
            }
        }

        // 4. Retry-After from primary host 429 is preserved when fallback host returns 503.
        store = BuildStore("ya29.rate-limited", now.AddHours(1));
        int rateLimitRequests = 0;
        using (HttpClient client = new(new Handler((request, _) =>
        {
            rateLimitRequests++;
            if (request.RequestUri!.Host.StartsWith("daily-", StringComparison.Ordinal))
            {
                HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        })))
        {
            GeminiUsageCollector collector = new(client, temp.Path, credentialStoreReader: () => store, nowProvider: () => now);
            GeminiUsageDashboard failed = await collector.CollectDashboardAsync();
            Check(!failed.Snapshot.IsAvailable, "primary 429 + fallback 503 fails");
            int afterFirst = rateLimitRequests;
            now = now.AddMinutes(30);
            GeminiUsageDashboard stillBackedOff = await collector.CollectDashboardAsync();
            Check(!stillBackedOff.Snapshot.IsAvailable && rateLimitRequests == afterFirst,
                "primary 429 Retry-After survives fallback 503 and suppresses requests for 1 hour");
        }

        // 5. Manual refresh on another page must not bypass Gemini backoff.
        RefreshRequests requests = new();
        requests.Add(PageItem.All, manual: false);
        requests.Add(PageItem.Apis, "card-1", manual: true);
        RefreshRequest batch = requests.Take()!;
        Check((batch.ManualPages & PageItem.Apis) != 0 && (batch.ManualPages & PageItem.Gemini) == 0,
            "manual refresh flag is tracked per page");

        // 6. 403 triggers at most one silent refresh, then maps to AccessDenied and waits for credential change; 401 on cached token evicts cache and recovers once.
        store = BuildStore("ya29.403-v1", now.AddHours(1));
        int forbiddenRefreshCalls = 0;
        using (HttpClient client = new(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                forbiddenRefreshCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"ya29.403-v2\",\"expires_in\":3600}"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        })))
        {
            GeminiUsageCollector collector = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                clientDiscoveryPaths: [jsBundlePath]);

            GeminiUsageDashboard afterRefresh403 = await collector.CollectDashboardAsync();
            Check(!afterRefresh403.Snapshot.IsAvailable && afterRefresh403.FailureKind == GeminiUsageFailureKind.AccessDenied && forbiddenRefreshCalls == 1,
                "403 after silent refresh maps to AccessDenied after one attempt");
            now = now.AddMinutes(5);
            GeminiUsageDashboard suppressed = await collector.CollectDashboardAsync();
            Check(!suppressed.Snapshot.IsAvailable && forbiddenRefreshCalls == 1,
                "token version that still returns 403 waits for credential change");
        }

        store = BuildStore("ya29.stale-cached-source", now.AddHours(-1));
        int cachedRecoveryRefreshes = 0;
        bool rejectCachedToken = false;
        using (HttpClient client = new(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                int call = ++cachedRecoveryRefreshes;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"access_token\":\"ya29.cached-{call}\",\"expires_in\":3600}}"),
                });
            }

            if (rejectCachedToken && request.Headers.Authorization?.Parameter == "ya29.cached-1")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) });
        })))
        {
            GeminiUsageCollector collector = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                clientDiscoveryPaths: [jsBundlePath]);
            GeminiUsageDashboard initialOk = await collector.CollectDashboardAsync();
            Check(initialOk.Snapshot.IsAvailable && cachedRecoveryRefreshes == 1, "initial expired store populates in-memory token cache");
            rejectCachedToken = true;
            now = now.AddMinutes(5);
            GeminiUsageDashboard recoveredFrom401 = await collector.CollectDashboardAsync();
            Check(recoveredFrom401.Snapshot.IsAvailable && cachedRecoveryRefreshes == 2, "401 on cached token evicts cache and refreshes once");
        }

        // 7. AutomationProperties.HelpText is exposed on the TextBlock automation peer.
        Exception? staError = null;
        Thread sta = new(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                TrayPopupViewModel vm = new(new AppSettings(), () => Task.CompletedTask);
                vm.ShowGemini();
                TrayPopupWindow window = new(vm);
                FrameworkElement content = (FrameworkElement)window.Content;
                content.Measure(new Size(window.Width, window.Height));
                content.Arrange(new Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                TextBlock statusText = (TextBlock)window.FindName("GeminiUpdatedAtTextBlock")!;
                vm.UpdateGeminiDashboard(new GeminiUsageDashboard
                {
                    Snapshot = new GeminiUsageSnapshot { Source = "gemini", UpdatedAt = now, IsAvailable = false },
                    StatusMessage = "Open Antigravity CLI (agy) to renew your Gemini login, then refresh.",
                    FailureKind = GeminiUsageFailureKind.OwnerRefreshRequired,
                    CredentialOwner = GeminiCredentialOwner.AntigravityCli,
                });
                content.UpdateLayout();
                AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(statusText);
                string helpText = peer?.GetHelpText() ?? string.Empty;
                Check(helpText.Contains("Open Antigravity CLI", StringComparison.Ordinal), $"status TextBlock automation peer exposes failure HelpText (actual: '{helpText}')");

                vm.UpdateGeminiDashboard(new GeminiUsageDashboard
                {
                    Snapshot = new GeminiUsageSnapshot { Source = "cloud_code", UpdatedAt = now, IsAvailable = true, SubscriptionTier = "PRO" },
                    StatusMessage = string.Empty,
                    FailureKind = GeminiUsageFailureKind.None,
                });
                content.UpdateLayout();
                Check(string.IsNullOrEmpty(peer?.GetHelpText()), "status TextBlock automation peer clears HelpText on success");
                window.Close();
            }
            catch (Exception exception)
            {
                staError = exception;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        if (staError != null)
        {
            throw staError;
        }
    }

    // Only scanner fixtures need Google-shaped values. Build them from zero and repeated markers, never from a registered client.
    private static string ScannerFixtureClientId(char marker) => $"000000-{new string(marker, 32)}.apps.googleusercontent.com";

    private static string ScannerFixtureClientSecret(char marker) => $"GOCSPX-{new string(marker, 28)}";

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
