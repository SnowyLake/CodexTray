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
        string agyUsageJson = JsonSerializer.Serialize(new
        {
            conversation_id = "conv-test",
            status = "SUCCESS",
            response = "ignored text",
            num_turns = 0,
            command = new
            {
                name = "usage",
                data = JsonDocument.Parse(quotaJson).RootElement,
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

        // 3. Agy CLI helper updates the Credential Manager store and restores full Cloud Code quota + tier across two expiry boundaries.
        now = expiry.AddMinutes(10);
        store = BuildStore("ya29.expired-1", expiry);
        int versionCalls = 0;
        int usageCalls = 0;
        string expectedToken = "ya29.refreshed-1";
        using (HttpClient client = new(new Handler((request, _) =>
        {
            Check(request.RequestUri!.Host != "oauth2.googleapis.com", "agy recovery must not send direct OAuth POST");
            Check(request.Headers.Authorization?.Parameter == expectedToken, "Cloud Code uses refreshed token written by agy");
            string body = request.RequestUri.AbsolutePath.EndsWith("loadCodeAssist", StringComparison.Ordinal)
                ? "{\"cloudaicompanionProject\":\"p1\",\"paidTier\":{\"id\":\"PRO\"}}"
                : quotaJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        })))
        {
            GeminiUsageCollector collector = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                allowAgyHelper: true,
                agyExecutablePath: @"C:\fake\agy.exe",
                agyCliRunner: (_, args, _, _, _) =>
                {
                    if (args.Length == 1 && args[0] == "--version")
                    {
                        versionCalls++;
                        return Task.FromResult(new GeminiUsageCollector.AgyCliResult(0, "1.5.12\n", false, false));
                    }

                    usageCalls++;
                    store = BuildStore(expectedToken, now.AddHours(1));
                    return Task.FromResult(new GeminiUsageCollector.AgyCliResult(0, agyUsageJson, false, false));
                });

            GeminiUsageDashboard first = await collector.CollectDashboardAsync();
            Check(first.Snapshot.IsAvailable && first.Snapshot.SubscriptionTier == "PRO" && first.Snapshot.Source == "cloud_code",
                "agy store update restores full Cloud Code quota and subscription tier");
            Check(versionCalls == 1 && usageCalls == 1, "agy version and usage each ran once");

            // Subsequent minute poll reuses refreshed store without invoking agy again.
            now = now.AddMinutes(1);
            GeminiUsageDashboard nextMinute = await collector.CollectDashboardAsync();
            Check(nextMinute.Snapshot.IsAvailable && versionCalls == 1 && usageCalls == 1, "fresh store skips agy helper");

            // Second expiry boundary triggers agy again while reusing cached version.
            now = now.AddHours(1);
            expectedToken = "ya29.refreshed-2";
            GeminiUsageDashboard secondCycle = await collector.CollectDashboardAsync();
            Check(secondCycle.Snapshot.IsAvailable && versionCalls == 1 && usageCalls == 2, "second expiry recovers cleanly with cached version");
        }

        // 4. Verified zero-turn JSON fallback when store is unchanged; non-zero turn report is rejected.
        store = BuildStore("ya29.expired-readonly", now.AddHours(-1));
        using (HttpClient client = new(new Handler((_, _) => throw new InvalidOperationException("fallback should not hit HTTP when store unchanged"))))
        {
            GeminiUsageCollector fallbackCollector = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                allowAgyHelper: true,
                agyExecutablePath: @"C:\fake\agy.exe",
                agyCliRunner: (_, args, _, _, _) => Task.FromResult(args[0] == "--version"
                    ? new GeminiUsageCollector.AgyCliResult(0, "1.5.12", false, false)
                    : new GeminiUsageCollector.AgyCliResult(0, agyUsageJson, false, false)));
            GeminiUsageDashboard fallback = await fallbackCollector.CollectDashboardAsync();
            Check(fallback.Snapshot.IsAvailable && fallback.Snapshot.Source == "agy_cli" && fallback.Snapshot.GeminiFiveHour!.RemainingPercent == 80,
                "zero-turn agy usage JSON acts as bounded fallback");

            string nonZeroTurnJson = agyUsageJson.Replace("\"num_turns\":0", "\"num_turns\":1", StringComparison.Ordinal);
            GeminiUsageCollector rejectedCollector = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                allowAgyHelper: true,
                agyExecutablePath: @"C:\fake\agy.exe",
                agyCliRunner: (_, args, _, _, _) => Task.FromResult(args[0] == "--version"
                    ? new GeminiUsageCollector.AgyCliResult(0, "1.5.12", false, false)
                    : new GeminiUsageCollector.AgyCliResult(0, nonZeroTurnJson, false, false)));
            GeminiUsageDashboard rejected = await rejectedCollector.CollectDashboardAsync();
            Check(!rejected.Snapshot.IsAvailable && rejected.FailureKind == GeminiUsageFailureKind.OwnerRefreshRequired,
                "non-zero turn CLI output is rejected");
        }

        // 5. Safety gates: disableSlashCommands, unsupported version, auth_method mismatch, logout during helper, and timeout backoff.
        string settingsPath = Path.Combine(agyDir, "settings.json");
        File.WriteAllText(settingsPath, "{\"disableSlashCommands\":true}");
        int blockedCalls = 0;
        using (HttpClient client = new(new Handler((_, _) => new TaskCompletionSource<HttpResponseMessage>().Task)))
        {
            GeminiUsageCollector blocked = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                allowAgyHelper: true,
                agyExecutablePath: @"C:\fake\agy.exe",
                agyCliRunner: (_, _, _, _, _) => { blockedCalls++; return Task.FromResult(new GeminiUsageCollector.AgyCliResult(0, "1.5.12", false, false)); });
            GeminiUsageDashboard blockedResult = await blocked.CollectDashboardAsync();
            Check(!blockedResult.Snapshot.IsAvailable && blockedCalls == 0 && blockedResult.FailureKind == GeminiUsageFailureKind.OwnerRefreshRequired,
                "disableSlashCommands blocks launching agy");
        }
        File.Delete(settingsPath);

        int oldVersionUsageCalls = 0;
        using (HttpClient client = new(new Handler((_, _) => new TaskCompletionSource<HttpResponseMessage>().Task)))
        {
            GeminiUsageCollector oldCli = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                allowAgyHelper: true,
                agyExecutablePath: @"C:\fake\agy.exe",
                agyCliRunner: (_, args, _, _, _) =>
                {
                    if (args[0] != "--version")
                    {
                        oldVersionUsageCalls++;
                    }
                    return Task.FromResult(new GeminiUsageCollector.AgyCliResult(0, "1.1.10", false, false));
                });
            GeminiUsageDashboard oldResult = await oldCli.CollectDashboardAsync();
            Check(!oldResult.Snapshot.IsAvailable && oldVersionUsageCalls == 0, "agy below 1.1.11 never runs /usage");
        }

        int timeoutUsageCalls = 0;
        using (HttpClient client = new(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) }))))
        {
            GeminiUsageCollector timeoutCollector = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                allowAgyHelper: true,
                agyExecutablePath: @"C:\fake\agy.exe",
                agyCliRunner: (_, args, _, _, _) =>
                {
                    if (args[0] == "--version")
                    {
                        return Task.FromResult(new GeminiUsageCollector.AgyCliResult(0, "1.5.12", false, false));
                    }
                    timeoutUsageCalls++;
                    return Task.FromResult(new GeminiUsageCollector.AgyCliResult(-1, string.Empty, TimedOut: true, OutputTruncated: false));
                });
            GeminiUsageDashboard t1 = await timeoutCollector.CollectDashboardAsync();
            Check(!t1.Snapshot.IsAvailable && t1.FailureKind == GeminiUsageFailureKind.Timeout && timeoutUsageCalls == 1, "agy timeout is classified");
            now = now.AddMinutes(1);
            GeminiUsageDashboard t2 = await timeoutCollector.CollectDashboardAsync();
            Check(!t2.Snapshot.IsAvailable && timeoutUsageCalls == 1, "backoff suppresses immediate minute retry");
            GeminiUsageDashboard tManual = await timeoutCollector.CollectDashboardAsync(forceRefresh: true);
            Check(!tManual.Snapshot.IsAvailable && timeoutUsageCalls == 2, "manual refresh bypasses backoff");
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
        string agyUsageJson = JsonSerializer.Serialize(new
        {
            status = "SUCCESS",
            num_turns = 0,
            command = new
            {
                name = "usage",
                data = JsonDocument.Parse(quotaJson).RootElement,
            },
        });

        // P1-1: Stale local file must never accept agy stdout fallback when the file itself was not updated.
        string agyDir = Path.Combine(temp.Path, "antigravity-cli");
        Directory.CreateDirectory(agyDir);
        string filePath = Path.Combine(agyDir, "antigravity-oauth-token");
        File.WriteAllText(filePath, BuildStore("ya29.account-a-expired", now.AddHours(-1), tier: "PRO"));
        using (HttpClient client = new(new Handler((_, _) => throw new InvalidOperationException("must not query Cloud Code for stale unchanged file"))))
        {
            GeminiUsageCollector collector = new(
                client,
                temp.Path,
                nowProvider: () => now,
                allowAgyHelper: true,
                agyExecutablePath: @"C:\fake\agy.exe",
                agyCliRunner: (_, args, _, _, _) => Task.FromResult(args[0] == "--version"
                    ? new GeminiUsageCollector.AgyCliResult(0, "1.5.12", false, false)
                    : new GeminiUsageCollector.AgyCliResult(0, agyUsageJson, false, false)));
            GeminiUsageDashboard rejected = await collector.CollectDashboardAsync();
            Check(!rejected.Snapshot.IsAvailable && rejected.FailureKind == GeminiUsageFailureKind.OwnerRefreshRequired,
                "stale local file rejects agy stdout fallback to prevent cross-account attribution");
        }
        File.Delete(filePath);

        // P1-2: ResetState never blocks on s_AuthLock and prevents stale in-flight 403s from poisoning post-reset collections.
        string store = BuildStore("ya29.in-flight", now.AddHours(1));
        TaskCompletionSource enteredHelper = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseHelper = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (HttpClient client = new(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) }))))
        {
            string expiredStore = BuildStore("ya29.expired-lock", now.AddHours(-1));
            GeminiUsageCollector collector = new(
                client,
                temp.Path,
                credentialStoreReader: () => expiredStore,
                nowProvider: () => now,
                allowAgyHelper: true,
                agyExecutablePath: @"C:\fake\agy.exe",
                agyCliRunner: async (_, args, _, _, _) =>
                {
                    if (args[0] == "--version")
                    {
                        return new GeminiUsageCollector.AgyCliResult(0, "1.5.12", false, false);
                    }

                    enteredHelper.TrySetResult();
                    await releaseHelper.Task;
                    return new GeminiUsageCollector.AgyCliResult(-1, string.Empty, false, false);
                });

            Task<GeminiUsageDashboard> blockedRun = collector.CollectDashboardAsync();
            await enteredHelper.Task;
            Task resetTask = Task.Run(collector.ResetState);
            Check(await Task.WhenAny(resetTask, Task.Delay(500)) == resetTask, "ResetState returns immediately while auth lock is held");
            releaseHelper.TrySetResult();
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

        // P1-3: Ancestor directory junction is rejected by IsTrustedLocalExecutable.
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
            Check(GeminiUsageCollector.IsTrustedLocalExecutable(realExe), "normal executable path is trusted");
            Check(!GeminiUsageCollector.IsTrustedLocalExecutable(Path.Combine(junctionRoot, "bin", "agy.exe")), "grandparent junction is rejected");
        }
        finally
        {
            if (Directory.Exists(junctionRoot))
            {
                Directory.Delete(junctionRoot);
            }
        }

        // P2-4a: Transient --version probe failure is not cached as null and recovers after backoff.
        store = BuildStore("ya29.probe-expired", now.AddHours(-1));
        int probeCount = 0;
        using (HttpClient client = new(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) }))))
        {
            GeminiUsageCollector collector = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                allowAgyHelper: true,
                agyExecutablePath: @"C:\fake\agy.exe",
                agyCliRunner: (_, args, _, _, _) =>
                {
                    if (args[0] == "--version")
                    {
                        probeCount++;
                        return Task.FromResult(probeCount == 1
                            ? new GeminiUsageCollector.AgyCliResult(1, string.Empty, false, false)
                            : new GeminiUsageCollector.AgyCliResult(0, "1.5.12", false, false));
                    }

                    store = BuildStore("ya29.probe-recovered", now.AddHours(1));
                    return Task.FromResult(new GeminiUsageCollector.AgyCliResult(0, agyUsageJson, false, false));
                });

            GeminiUsageDashboard fail1 = await collector.CollectDashboardAsync();
            Check(!fail1.Snapshot.IsAvailable && probeCount == 1, "first version probe failure fails cleanly");
            now = now.AddMinutes(3);
            GeminiUsageDashboard ok2 = await collector.CollectDashboardAsync();
            Check(ok2.Snapshot.IsAvailable && probeCount == 2, "version probe retries after transient backoff instead of caching null");
        }

        // P2-4b: Temporarily locked local credential file uses transient backoff and recovers once unlocked.
        string cliPath = Path.Combine(temp.Path, "oauth_creds.json");
        File.WriteAllText(cliPath, JsonSerializer.Serialize(new
        {
            access_token = "ya29.locked-old",
            refresh_token = "1//locked-refresh",
            client_id = "fixture-client",
            client_secret = "fixture-secret",
            expiry_date = now.AddHours(-1).ToUnixTimeMilliseconds(),
        }));
        using (HttpClient client = new(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"ya29.unlocked-new\",\"expires_in\":3600}"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(quotaJson) });
        })))
        {
            GeminiUsageCollector collector = new(client, temp.Path, nowProvider: () => now);
            using (FileStream hold = new(cliPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                GeminiUsageDashboard lockedResult = await collector.CollectDashboardAsync();
                Check(!lockedResult.Snapshot.IsAvailable && lockedResult.FailureKind == GeminiUsageFailureKind.OwnerRefreshRequired,
                    "locked file reports writeback failure");
            }

            now = now.AddMinutes(3);
            GeminiUsageDashboard unlockedResult = await collector.CollectDashboardAsync();
            Check(unlockedResult.Snapshot.IsAvailable, "unlocked file recovers automatically after transient backoff");
        }
        File.Delete(cliPath);

        // P2-5: Real process helper captures stdout cleanly and aborts/cleans up on timeout.
        string cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        GeminiUsageCollector.AgyCliResult echoResult = await GeminiUsageCollector.ExecuteAgyProcessAsync(cmdPath, ["/c", "echo 1.5.12"], TimeSpan.FromSeconds(5), 1024, default);
        Check(echoResult.ExitCode == 0 && !echoResult.TimedOut && echoResult.StdOut.Contains("1.5.12", StringComparison.Ordinal), "real process stdout read succeeds");
        GeminiUsageCollector.AgyCliResult slowResult = await GeminiUsageCollector.ExecuteAgyProcessAsync(cmdPath, ["/c", "ping -n 10 127.0.0.1 >nul"], TimeSpan.FromMilliseconds(200), 1024, default);
        Check(slowResult.TimedOut, "real process timeout kills process tree and cleans up streams");

        // P2-6: Retry-After from primary host 429 is preserved when fallback host returns 503.
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

        // P2-7: Manual refresh on another page must not bypass Gemini backoff.
        RefreshRequests requests = new();
        requests.Add(PageItem.All, manual: false);
        requests.Add(PageItem.Apis, "card-1", manual: true);
        RefreshRequest batch = requests.Take()!;
        Check((batch.ManualPages & PageItem.Apis) != 0 && (batch.ManualPages & PageItem.Gemini) == 0,
            "manual refresh flag is tracked per page");

        // Additional gate & 403 coverage: auth_method mismatch, logout during helper, and 403 with agy helper enabled.
        store = BuildStore("ya29.api-key", now.AddHours(-1), authMethod: "api_key");
        int apiKeyHelperCalls = 0;
        using (HttpClient client = new(new Handler((_, _) => new TaskCompletionSource<HttpResponseMessage>().Task)))
        {
            GeminiUsageCollector collector = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                allowAgyHelper: true,
                agyExecutablePath: @"C:\fake\agy.exe",
                agyCliRunner: (_, _, _, _, _) => { apiKeyHelperCalls++; return Task.FromResult(new GeminiUsageCollector.AgyCliResult(0, "1.5.12", false, false)); });
            GeminiUsageDashboard blocked = await collector.CollectDashboardAsync();
            Check(!blocked.Snapshot.IsAvailable && apiKeyHelperCalls == 0, "non-consumer auth_method blocks agy helper");
        }

        string? logoutStore = BuildStore("ya29.logout-before", now.AddHours(-1));
        using (HttpClient client = new(new Handler((_, _) => new TaskCompletionSource<HttpResponseMessage>().Task)))
        {
            GeminiUsageCollector collector = new(
                client,
                temp.Path,
                credentialStoreReader: () => logoutStore,
                nowProvider: () => now,
                allowAgyHelper: true,
                agyExecutablePath: @"C:\fake\agy.exe",
                agyCliRunner: (_, args, _, _, _) =>
                {
                    if (args[0] == "--version")
                    {
                        return Task.FromResult(new GeminiUsageCollector.AgyCliResult(0, "1.5.12", false, false));
                    }
                    logoutStore = null;
                    return Task.FromResult(new GeminiUsageCollector.AgyCliResult(0, agyUsageJson, false, false));
                });
            GeminiUsageDashboard loggedOut = await collector.CollectDashboardAsync();
            Check(!loggedOut.Snapshot.IsAvailable && loggedOut.FailureKind == GeminiUsageFailureKind.CredentialChanged,
                "logout during agy helper discards output as CredentialChanged");
        }

        store = BuildStore("ya29.403-v1", now.AddHours(1));
        int forbiddenHelperCalls = 0;
        using (HttpClient client = new(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))))
        {
            GeminiUsageCollector collector = new(
                client,
                temp.Path,
                credentialStoreReader: () => store,
                nowProvider: () => now,
                allowAgyHelper: true,
                agyExecutablePath: @"C:\fake\agy.exe",
                agyCliRunner: (_, args, _, _, _) =>
                {
                    if (args[0] == "--version")
                    {
                        return Task.FromResult(new GeminiUsageCollector.AgyCliResult(0, "1.5.12", false, false));
                    }
                    forbiddenHelperCalls++;
                    store = BuildStore("ya29.403-v2", now.AddHours(1));
                    return Task.FromResult(new GeminiUsageCollector.AgyCliResult(0, agyUsageJson, false, false));
                });

            GeminiUsageDashboard afterRefresh403 = await collector.CollectDashboardAsync();
            Check(!afterRefresh403.Snapshot.IsAvailable && afterRefresh403.FailureKind == GeminiUsageFailureKind.AccessDenied && forbiddenHelperCalls == 1,
                "403 after agy refresh maps to AccessDenied after one attempt");
            now = now.AddMinutes(5);
            GeminiUsageDashboard suppressed = await collector.CollectDashboardAsync();
            Check(!suppressed.Snapshot.IsAvailable && forbiddenHelperCalls == 1,
                "new token version that still returns 403 waits for credential change");
        }

        // P2-8: AutomationProperties.HelpText is exposed on the TextBlock automation peer.
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
