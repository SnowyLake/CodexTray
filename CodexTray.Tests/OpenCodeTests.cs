using CodexTray.App;
using CodexTray.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace CodexTray.Tests;

internal static class OpenCodeTests
{
    internal static async Task QuotaAndCredentialsAsync()
    {
        DateTimeOffset now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

        // 1. No local login -> Free with 100% across Session, Weekly, Monthly.
        using (TempDirectory emptyDir = new())
        {
            int requests = 0;
            using HttpClient client = new(new StubHandler((_, _) =>
            {
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }));
            OpenCodeUsageCollector collector = new(client, emptyDir.Path, nowProvider: () => now);
            OpenCodeUsageDashboard dashboard = await collector.CollectDashboardAsync();
            Check(requests == 0, "unauthenticated OpenCode should not call remote APIs");
            Check(dashboard.Snapshot.IsAvailable, "unauthenticated OpenCode should produce an available Free snapshot");
            Check(dashboard.Snapshot.SubscriptionTier == "FREE", "unauthenticated OpenCode badge should be FREE");
            Check(dashboard.Snapshot.Session?.RemainingPercent == 100, "Free Session remaining should be 100%");
            Check(dashboard.Snapshot.Weekly?.RemainingPercent == 100, "Free Weekly remaining should be 100%");
            Check(dashboard.Snapshot.Monthly?.RemainingPercent == 100, "Free Monthly remaining should be 100%");
            OpenCodePluginUsage plugin = OpenCodeUsageCollector.BuildPluginUsage(dashboard);
            Check(plugin.Display == "100%" && plugin.Session.RemainingPercent == 100, "Free OpenCode plugin output should be 100%");
        }

        // 2. Existing v2 credential table without active opencode/opencode-go prevents legacy auth.json fallback -> Free.
        using (TempDirectory v2LoggedOut = new())
        {
            string dbPath = Path.Combine(v2LoggedOut.Path, "opencode.db");
            CreateCredentialDatabase(dbPath);
            InsertCredential(dbPath, "openai", active: true, "{\"type\":\"oauth\",\"access\":\"fake-openai\"}");
            InsertCredential(dbPath, "opencode", active: false, "{\"type\":\"key\",\"key\":\"inactive-key\"}");
            File.WriteAllText(Path.Combine(v2LoggedOut.Path, "auth.json"), "{\"opencode-go\":{\"type\":\"api\",\"key\":\"stale-legacy-key\"}}");

            int requests = 0;
            using HttpClient client = new(new StubHandler((_, _) =>
            {
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }));
            OpenCodeUsageCollector collector = new(client, v2LoggedOut.Path, nowProvider: () => now);
            OpenCodeUsageDashboard dashboard = await collector.CollectDashboardAsync();
            Check(requests == 0 && dashboard.Snapshot.IsAvailable && dashboard.Snapshot.SubscriptionTier == "FREE",
                "v2 credential table without active OpenCode login should return Free without falling back to stale auth.json");
        }

        // 3. Logged in, 403 EntitlementError -> Free with 100% across all three windows.
        using (TempDirectory unsubscribed = new())
        {
            string dbPath = Path.Combine(unsubscribed.Path, "opencode.db");
            CreateCredentialDatabase(dbPath);
            long futureExpiryMs = now.AddHours(1).ToUnixTimeMilliseconds();
            InsertCredential(dbPath, "opencode", active: true,
                $"{{\"type\":\"oauth\",\"access\":\"fake-console-token\",\"expires\":{futureExpiryMs},\"metadata\":{{\"orgID\":\"wrk_01\"}}}}");

            using HttpClient client = new(new StubHandler((request, _) =>
            {
                Check(request.Headers.Authorization?.Parameter == "fake-console-token", "bearer token header");
                Check(request.Headers.GetValues("x-opencode-org-id").Single() == "wrk_01", "usage org header");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"EntitlementError\",\"message\":\"OpenCode Go subscription required.\"}}"),
                });
            }));
            OpenCodeUsageCollector collector = new(client, unsubscribed.Path, nowProvider: () => now);
            OpenCodeUsageDashboard dashboard = await collector.CollectDashboardAsync();
            Check(dashboard.Snapshot.IsAvailable && dashboard.Snapshot.SubscriptionTier == "FREE", "EntitlementError should fall back to FREE");
            Check(dashboard.Snapshot.Session?.RemainingPercent == 100 && dashboard.Snapshot.Weekly?.RemainingPercent == 100 && dashboard.Snapshot.Monthly?.RemainingPercent == 100,
                "EntitlementError Free fallback should set all three quotas to 100%");
        }

        // 4. Logged in with Go Plus subscription, placeholder rolling reset dropped, weekly/monthly preserved.
        using (TempDirectory subscribed = new())
        {
            string dbPath = Path.Combine(subscribed.Path, "opencode.db");
            CreateCredentialDatabase(dbPath);
            long futureExpiryMs = now.AddHours(2).ToUnixTimeMilliseconds();
            InsertCredential(dbPath, "opencode", active: true,
                $"{{\"type\":\"oauth\",\"access\":\"fake-go-token\",\"expires\":{futureExpiryMs},\"metadata\":{{\"orgID\":\"wrk_99\"}}}}");

            List<HttpRequestMessage> recorded = [];
            DateTimeOffset placeholderReset = now.AddHours(5).AddMilliseconds(800);
            DateTimeOffset weeklyReset = now.AddDays(3);
            DateTimeOffset monthlyReset = now.AddDays(20);
            string usageJson = JsonSerializer.Serialize(new
            {
                usage = new
                {
                    rolling = new { status = "ok", percent = 0, resetsAt = placeholderReset.ToString("O", CultureInfo.InvariantCulture) },
                    weekly = new { status = "ok", percent = 18.6, resetsAt = weeklyReset.ToString("O", CultureInfo.InvariantCulture) },
                    monthly = new { status = "ok", percent = 35, resetsAt = monthlyReset.ToString("O", CultureInfo.InvariantCulture) },
                },
            });

            using HttpClient client = new(new StubHandler((request, _) =>
            {
                recorded.Add(request);
                if (request.RequestUri!.AbsolutePath.EndsWith("/go/status", StringComparison.Ordinal))
                {
                    Check(request.Headers.GetValues("x-org-id").Single() == "wrk_99", "go status x-org-id header");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"product\":\"go-plus\"}"),
                    });
                }

                HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StringContent(usageJson) };
                response.Headers.Date = now;
                return Task.FromResult(response);
            }));

            OpenCodeUsageCollector collector = new(client, subscribed.Path, nowProvider: () => now);
            OpenCodeUsageDashboard dashboard = await collector.CollectDashboardAsync(useAbsoluteResetTime: false);
            Check(recorded.Count == 2, "OAuth login should query both usage and go/status");
            Check(dashboard.Snapshot.IsAvailable && dashboard.Snapshot.SubscriptionTier == "GO PLUS", "go-plus maps to GO PLUS");
            Check(dashboard.Snapshot.Session?.RemainingPercent == 100 && dashboard.Snapshot.Session.ResetsAt == 0 && dashboard.Snapshot.Session.ResetLabel == "unknown",
                "untouched rolling window drops placeholder reset");
            Check(dashboard.Snapshot.Weekly is not null && dashboard.Snapshot.Monthly is not null, "weekly and monthly quotas should be present");
            Check(dashboard.Snapshot.Weekly!.RemainingPercent == 81 && dashboard.Snapshot.Weekly.ResetsAt == weeklyReset.ToUnixTimeSeconds(),
                "weekly percent rounds away from zero and preserves reset");
            Check(dashboard.Snapshot.Monthly!.RemainingPercent == 65 && dashboard.Snapshot.Monthly.ResetsAt == monthlyReset.ToUnixTimeSeconds(),
                "monthly percent and reset preserved");
            Check(OpenCodeUsageCollector.FormatResetLabel(dashboard.Snapshot.Weekly, useAbsoluteResetTime: false, now) == "3d00h", "weekly countdown label");
            Check(OpenCodeUsageCollector.FormatResetLabel(dashboard.Snapshot.Monthly, useAbsoluteResetTime: true, now) == "08-01", "monthly absolute date label");
        }

        // 5. Sub-1% anchored rolling session keeps its reset and API key defaults tier to GO.
        using (TempDirectory apiKeyDir = new())
        {
            DateTimeOffset sessionReset = now.AddHours(3).AddMinutes(30);
            string usageJson = JsonSerializer.Serialize(new
            {
                usage = new
                {
                    rolling = new { status = "ok", percent = 0.4, resetsAt = sessionReset.ToString("O", CultureInfo.InvariantCulture) },
                    weekly = new { status = "ok", percent = 10, resetsAt = now.AddDays(5).ToString("O", CultureInfo.InvariantCulture) },
                    monthly = new { status = "ok", percent = 20, resetsAt = now.AddDays(15).ToString("O", CultureInfo.InvariantCulture) },
                },
            });

            int calls = 0;
            using HttpClient client = new(new StubHandler((request, _) =>
            {
                calls++;
                Check(request.Headers.Authorization?.Parameter == "fake-env-api-key", "OPENCODE_API_KEY takes precedence");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(usageJson) });
            }));
            OpenCodeUsageCollector collector = new(client, apiKeyDir.Path, key => key == "OPENCODE_API_KEY" ? "fake-env-api-key" : null, nowProvider: () => now);
            OpenCodeUsageDashboard dashboard = await collector.CollectDashboardAsync(useAbsoluteResetTime: true);
            Check(calls == 1 && dashboard.Snapshot.SubscriptionTier == "GO", "API key skips go/status and shows GO");
            Check(dashboard.Snapshot.Session is not null && dashboard.Snapshot.Session.RemainingPercent == 100 && dashboard.Snapshot.Session.ResetsAt == sessionReset.ToUnixTimeSeconds(),
                "anchored rolling reset is preserved");
            Check(dashboard.Snapshot.Session!.ResetLabel == "15:30", "session absolute clock label");
        }

        // 6. Legacy auth.json works when opencode.db does not exist.
        using (TempDirectory legacyDir = new())
        {
            File.WriteAllText(Path.Combine(legacyDir.Path, "auth.json"), "{\"opencode-go\":{\"type\":\"api\",\"key\":\"legacy-go-key\"}}");
            string usageJson = JsonSerializer.Serialize(new
            {
                usage = new
                {
                    rolling = new { status = "ok", percent = 25, resetsAt = now.AddHours(2).ToString("O", CultureInfo.InvariantCulture) },
                    weekly = new { status = "ok", percent = 50, resetsAt = now.AddDays(2).ToString("O", CultureInfo.InvariantCulture) },
                    monthly = new { status = "ok", percent = 75, resetsAt = now.AddDays(10).ToString("O", CultureInfo.InvariantCulture) },
                },
            });
            using HttpClient client = new(new StubHandler((request, _) =>
            {
                Check(request.Headers.Authorization?.Parameter == "legacy-go-key", "legacy auth.json key");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(usageJson) });
            }));
            OpenCodeUsageCollector collector = new(client, legacyDir.Path, nowProvider: () => now);
            OpenCodeUsageDashboard dashboard = await collector.CollectDashboardAsync();
            Check(dashboard.Snapshot.IsAvailable && dashboard.Snapshot.Session?.RemainingPercent == 75, "legacy auth.json quota");
            OpenCodePluginUsage plugin = OpenCodeUsageCollector.BuildPluginUsage(dashboard);
            Check(plugin.Display == "75%" && plugin.Session.RemainingPercent == 75, "plugin outputs OpenCode Session percent");
        }

        // 7. Expired OAuth token fails with auth category without modifying opencode.db, and duplicate active rows fail closed.
        using (TempDirectory expiredDir = new())
        {
            string dbPath = Path.Combine(expiredDir.Path, "opencode.db");
            CreateCredentialDatabase(dbPath);
            long expiredMs = now.AddMinutes(-5).ToUnixTimeMilliseconds();
            InsertCredential(dbPath, "opencode", active: true, $"{{\"type\":\"oauth\",\"access\":\"expired-token\",\"expires\":{expiredMs}}}");
            byte[] beforeBytes = File.ReadAllBytes(dbPath);

            using HttpClient client = new(new StubHandler((_, _) => throw new InvalidOperationException("expired token should not send HTTP request")));
            OpenCodeUsageCollector collector = new(client, expiredDir.Path, nowProvider: () => now);
            OpenCodeUsageDashboard dashboard = await collector.CollectDashboardAsync();
            Check(!dashboard.Snapshot.IsAvailable && UsageDiagnostics.Category(dashboard.StatusMessage) == "Authentication",
                "expired OAuth token reports Authentication failure");
            Check(OpenCodeUsageCollector.BuildPluginUsage(dashboard).Display == "N/A", "failed OpenCode reports N/A to plugins");
            Check(File.ReadAllBytes(dbPath).SequenceEqual(beforeBytes), "opencode.db must remain strictly read-only");

            // Duplicate active opencode credentials fail closed.
            InsertCredential(dbPath, "opencode", active: true, $"{{\"type\":\"oauth\",\"access\":\"second-token\",\"expires\":{now.AddHours(1).ToUnixTimeMilliseconds()}}}");
            OpenCodeUsageDashboard ambiguous = await collector.CollectDashboardAsync();
            Check(!ambiguous.Snapshot.IsAvailable && UsageDiagnostics.Category(ambiguous.StatusMessage) == "Authentication",
                "ambiguous active OpenCode rows must fail closed");
        }

        // 8. Custom Console server fails closed with zero HTTP requests; retired official console.opencode.ai is accepted.
        using (TempDirectory serverDir = new())
        {
            string dbPath = Path.Combine(serverDir.Path, "opencode.db");
            CreateCredentialDatabase(dbPath);
            InsertCredential(dbPath, "opencode", active: true,
                $"{{\"type\":\"oauth\",\"access\":\"custom-server-token\",\"expires\":{now.AddHours(1).ToUnixTimeMilliseconds()},\"metadata\":{{\"server\":\"https://console.internal.example\",\"orgID\":\"org_custom\"}}}}");

            int requestsSent = 0;
            using HttpClient client = new(new StubHandler((_, _) =>
            {
                requestsSent++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }));
            OpenCodeUsageCollector collector = new(client, serverDir.Path, nowProvider: () => now);
            OpenCodeUsageDashboard customResult = await collector.CollectDashboardAsync();
            Check(requestsSent == 0 && !customResult.Snapshot.IsAvailable,
                "custom Console server must not send credentials to official opencode.ai");
            Check(!customResult.StatusMessage.Contains("console.internal.example", StringComparison.OrdinalIgnoreCase),
                "custom server status must not echo server URL");
            Check(OpenCodeUsageCollector.BuildPluginUsage(customResult).Display == "N/A",
                "custom server failure reports N/A to plugins");

            // Retired official host console.opencode.ai is accepted.
            Check(OpenCodeUsageCollector.IsOfficialConsoleServer("https://opencode.ai/console"), "default official console accepted");
            Check(OpenCodeUsageCollector.IsOfficialConsoleServer("https://console.opencode.ai"), "retired official console accepted");
            Check(OpenCodeUsageCollector.IsOfficialConsoleServer("https://console.opencode.ai/"), "retired official console with slash accepted");
            Check(!OpenCodeUsageCollector.IsOfficialConsoleServer("http://opencode.ai/console"), "non-HTTPS console rejected");
            Check(!OpenCodeUsageCollector.IsOfficialConsoleServer("https://console.internal.example"), "custom console rejected");
        }

        // 9. Generic 403 + malformed/unrecognized go/status must NOT fall back to FREE/100%; explicit null product does.
        using (TempDirectory statusDir = new())
        {
            string dbPath = Path.Combine(statusDir.Path, "opencode.db");
            CreateCredentialDatabase(dbPath);
            InsertCredential(dbPath, "opencode", active: true,
                $"{{\"type\":\"oauth\",\"access\":\"oauth-token\",\"expires\":{now.AddHours(1).ToUnixTimeMilliseconds()},\"metadata\":{{\"server\":\"https://console.opencode.ai\"}}}}");

            string statusBody = "{\"product\":123}";
            using HttpClient client = new(new StubHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("{\"error\":{\"type\":\"Forbidden\"}}"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(statusBody),
                });
            }));

            OpenCodeUsageCollector collector = new(client, statusDir.Path, nowProvider: () => now);
            OpenCodeUsageDashboard malformed = await collector.CollectDashboardAsync();
            Check(!malformed.Snapshot.IsAvailable && UsageDiagnostics.Category(malformed.StatusMessage) == "Authentication",
                "malformed go/status product must not fall back to FREE");

            statusBody = "{}";
            OpenCodeUsageDashboard missingProduct = await collector.CollectDashboardAsync();
            Check(!missingProduct.Snapshot.IsAvailable, "empty go/status object must not fall back to FREE");

            statusBody = "{\"product\":null}";
            OpenCodeUsageDashboard confirmedFree = await collector.CollectDashboardAsync();
            Check(confirmedFree.Snapshot.IsAvailable && confirmedFree.Snapshot.SubscriptionTier == "FREE" && confirmedFree.Snapshot.Session?.RemainingPercent == 100,
                "explicit null product confirms unsubscribed FREE fallback");
        }

        // 10. Response without Date header uses post-response receive clock for untouched rolling window tolerance.
        using (TempDirectory latencyDir = new())
        {
            DateTimeOffset clock = now;
            using HttpClient client = new(new StubHandler((_, _) =>
            {
                clock = clock.AddSeconds(4);
                string json = JsonSerializer.Serialize(new
                {
                    usage = new
                    {
                        rolling = new { status = "ok", percent = 0, resetsAt = clock.AddHours(5).ToString("O", CultureInfo.InvariantCulture) },
                        weekly = new { status = "ok", percent = 10, resetsAt = clock.AddDays(4).ToString("O", CultureInfo.InvariantCulture) },
                        monthly = new { status = "ok", percent = 20, resetsAt = clock.AddDays(15).ToString("O", CultureInfo.InvariantCulture) },
                    },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }));
            OpenCodeUsageCollector collector = new(client, latencyDir.Path, key => key == "OPENCODE_API_KEY" ? "fake-key" : null, nowProvider: () => clock);
            OpenCodeUsageDashboard dashboard = await collector.CollectDashboardAsync(useAbsoluteResetTime: false);
            Check(dashboard.Snapshot.Session?.RemainingPercent == 100 && dashboard.Snapshot.Session.ResetsAt == 0 && dashboard.Snapshot.Session.ResetLabel == "unknown",
                "untouched rolling window without Date header should measure against receive time");
        }

        // 11. Quiescent WAL opencode.db opened read-only must not create -wal or -shm sidecar files, while active WAL is still read.
        using (TempDirectory walDir = new())
        {
            string dbPath = Path.Combine(walDir.Path, "opencode.db");
            CreateCredentialDatabase(dbPath);
            ExecuteSql(dbPath, "PRAGMA journal_mode=WAL; PRAGMA wal_checkpoint(TRUNCATE);");
            Check(!File.Exists(dbPath + "-wal") && !File.Exists(dbPath + "-shm"), "quiescent WAL database starts without sidecar files");

            using HttpClient noopClient = new(new StubHandler((_, _) => throw new InvalidOperationException("unexpected request")));
            OpenCodeUsageCollector collector = new(noopClient, walDir.Path, nowProvider: () => now);
            OpenCodeUsageDashboard quiescent = await collector.CollectDashboardAsync();
            Check(quiescent.Snapshot.IsAvailable && quiescent.Snapshot.SubscriptionTier == "FREE", "quiescent WAL DB reads cleanly");
            Check(!File.Exists(dbPath + "-wal") && !File.Exists(dbPath + "-shm"), "reading quiescent WAL DB must not create -wal or -shm files");
        }
    }

    internal static Task TokenCostAndIsolationAsync()
    {
        DateTimeOffset now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        using TempDirectory temp = new();
        string pricingPath = Path.Combine(temp.Path, "model-pricing.json");
        File.WriteAllText(pricingPath, """
        {
          "gpt-6.2-sol": { "input": 2.0, "cachedInput": 0.1, "output": 10.0 },
          "claude-opus-4.8": { "input": 5.0, "cachedInput": 0.5, "output": 25.0, "aliases": ["claude-opus-4-8"] },
          "grok-4.7": { "input": 2.0, "cachedInput": 0.5, "output": 6.0 }
        }
        """);

        string openCodeDir = Path.Combine(temp.Path, "opencode");
        Directory.CreateDirectory(openCodeDir);
        string dbPath = Path.Combine(openCodeDir, "opencode.db");
        CreateUsageDatabase(dbPath);

        long t1 = now.AddMinutes(-30).ToUnixTimeMilliseconds();
        long t2 = now.AddMinutes(-20).ToUnixTimeMilliseconds();
        long t3 = now.AddMinutes(-10).ToUnixTimeMilliseconds();
        long t4 = now.AddMinutes(-5).ToUnixTimeMilliseconds();
        long t5 = now.AddMinutes(-2).ToUnixTimeMilliseconds();

        // v2 session_message: OpenAI model (priced from model-pricing.json even when recorded cost == 0) + timing
        InsertSessionMessage(dbPath, "msg_overlap", "assistant", t1, $$"""
        {
          "model": { "providerID": "openai", "id": "gpt-6.2-sol" },
          "cost": 0,
          "time": { "created": {{t1}}, "streamed": {{t1 + 4000}}, "completed": {{t1 + 4200}} },
          "tokens": { "input": 1000, "output": 300, "reasoning": 100, "cache": { "read": 5000, "write": 500 } }
        }
        """);

        // Duplicate id in v1 message table -> MUST be deduplicated and not counted twice!
        InsertLegacyMessage(dbPath, "msg_overlap", t1, $$"""
        {
          "role": "assistant",
          "providerID": "openai",
          "modelID": "gpt-6.2-sol",
          "cost": 0,
          "time": { "created": {{t1}}, "completed": {{t1 + 4200}} },
          "tokens": { "input": 1000, "output": 300, "reasoning": 100, "cache": { "read": 5000, "write": 500 } }
        }
        """);

        // v2 session_message: Anthropic/Claude via custom provider + Grok via xAI
        InsertSessionMessage(dbPath, "msg_claude", "assistant", t2, $$"""
        {
          "model": { "providerID": "custom-claude", "id": "claude-opus-4-8" },
          "cost": 0.99,
          "time": { "created": {{t2}}, "completed": {{t2 + 2000}} },
          "tokens": { "input": 1000, "output": 200, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);

        // v2 session_message: uncataloged third-party model with OpenCode reported positive cost -> fallback to reported cost
        InsertSessionMessage(dbPath, "msg_custom_priced", "assistant", t3, $$"""
        {
          "model": { "providerID": "vercel", "id": "meta/muse-spark-1.2-contributor" },
          "cost": 0.05,
          "time": { "created": {{t3}}, "completed": {{t3 + 1500}} },
          "tokens": { "input": 500, "output": 500, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);

        // v2 session_message: uncataloged model with cost == 0 -> counts tokens and flags HasUnpricedUsage
        InsertSessionMessage(dbPath, "msg_unpriced", "assistant", t4, $$"""
        {
          "model": { "providerID": "local", "id": "unknown-open-model" },
          "cost": 0,
          "time": { "created": {{t4}}, "completed": {{t4 + 1500}} },
          "tokens": { "input": 400, "output": 100, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);

        // v1-only message + step-finish part (not in session_message) -> counted via part
        InsertLegacyMessage(dbPath, "msg_v1_only", t5, $$"""
        {
          "role": "assistant",
          "providerID": "xai",
          "modelID": "grok-4.7",
          "cost": 0,
          "time": { "created": {{t5}}, "completed": {{t5 + 2000}} },
          "tokens": { "input": 1000, "output": 500, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);
        InsertLegacyPart(dbPath, "prt_v1_1", "msg_v1_only", t5, """
        {
          "type": "step-finish",
          "cost": 0,
          "tokens": { "input": 1000, "output": 500, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);

        // Excluded codex-auto-review should be ignored
        InsertSessionMessage(dbPath, "msg_excluded", "assistant", t5, $$"""
        {
          "model": { "providerID": "openai", "id": "codex-auto-review" },
          "cost": 1.0,
          "time": { "created": {{t5}}, "completed": {{t5 + 2000}} },
          "tokens": { "input": 9999, "output": 9999, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);

        TokenCostCollector collector = new(pricingPath);
        TokenCostStatistics stats = collector.CollectOpenCode(openCodeDir, now);

        // Token totals:
        // msg_overlap: input 1000 + cacheRead 5000 + cacheWrite 500 + output 300 + reasoning 100 = 6900
        // msg_claude: 1000 + 200 = 1200
        // msg_custom_priced: 500 + 500 = 1000
        // msg_unpriced: 400 + 100 = 500
        // msg_v1_only (grok-4.7): 1000 + 500 = 1500
        // Total = 11100
        Check(stats.Lifetime.TotalTokens == 11_100, $"expected 11100 OpenCode tokens, got {stats.Lifetime.TotalTokens}");
        Check(stats.Lifetime.HasUnpricedUsage, "uncataloged zero-cost model should flag HasUnpricedUsage");
        Check(stats.Models.Count == 5, $"expected 5 distinct OpenCode models, got {stats.Models.Count}");
        Check(stats.SessionLogs.Count == 5, $"expected 5 session log rows, got {stats.SessionLogs.Count}");
        Check(stats.IsSpeedEstimated, "OpenCode speed should be marked estimated");

        // Expected priced cost:
        // gpt-6.2-sol: (1500 * 2.0 + 5000 * 0.1 + 400 * 10.0) / 1e6 = 7500 / 1e6 = 0.0075
        // claude-opus-4-8: (1000 * 5.0 + 200 * 25.0) / 1e6 = 10000 / 1e6 = 0.01
        // muse-spark: reported 0.05
        // grok-4.7: (1000 * 2.0 + 500 * 6.0) / 1e6 = 5000 / 1e6 = 0.005
        // Sum = 0.0725
        Check(stats.Lifetime.CostUsd == 0.0725m, $"expected $0.0725 OpenCode cost, got {stats.Lifetime.CostUsd}");

        // Speed check for msg_overlap (400 output tokens in 4000 ms = 100 tok/s)
        TokenCostModelStatistics gptModel = stats.Models.Single(m => m.Model == "gpt-6.2-sol");
        Check(gptModel.Lifetime.GetOutputTokensPerSecond() == 100m, $"expected 100 tok/s for gpt-6.2-sol, got {gptModel.Lifetime.GetOutputTokensPerSecond()}");

        // Cache hit & invalidation when DB changes
        TokenCostStatistics cached = collector.CollectOpenCode(openCodeDir, now);
        Check(cached.Lifetime.TotalTokens == 11_100, "cached OpenCode collection preserves totals");

        InsertSessionMessage(dbPath, "msg_added", "assistant", now.ToUnixTimeMilliseconds(), $$"""
        {
          "model": { "providerID": "openai", "id": "gpt-6.2-sol" },
          "cost": 0,
          "time": { "created": {{now.ToUnixTimeMilliseconds()}}, "completed": {{now.ToUnixTimeMilliseconds() + 1000}} },
          "tokens": { "input": 100, "output": 100, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);
        File.SetLastWriteTimeUtc(dbPath, File.GetLastWriteTimeUtc(dbPath).AddSeconds(2));
        TokenCostStatistics updated = collector.CollectOpenCode(openCodeDir, now);
        Check(updated.Lifetime.TotalTokens == 11_300, $"expected 11300 tokens after DB update, got {updated.Lifetime.TotalTokens}");

        // Isolation check: Codex, Grok, Gemini collectors do NOT read OpenCode directory/models.
        string emptyHarnessDir = Path.Combine(temp.Path, "empty-harness");
        Directory.CreateDirectory(emptyHarnessDir);
        Check(collector.CollectCodex(emptyHarnessDir, now).Lifetime.TotalTokens == 0, "Codex must not read OpenCode usage");
        Check(collector.CollectGrok(emptyHarnessDir, now).Lifetime.TotalTokens == 0, "Grok must not read OpenCode usage");
        Check(collector.CollectGemini(emptyHarnessDir, userProfileDirectory: null, now).Lifetime.TotalTokens == 0, "Gemini must not read OpenCode usage");

        // Regression suite: forks (v2 + v1), compaction + title events, empty v2 shadowing, legacy unpriced guard, v1 speed pairing, quiescent WAL.
        using TempDirectory edgeTemp = new();
        string edgeDbPath = Path.Combine(edgeTemp.Path, "opencode.db");
        CreateFullUsageDatabase(edgeDbPath);
        ExecuteSql(edgeDbPath, "PRAGMA journal_mode=WAL;");

        long baseMs = now.AddMinutes(-40).ToUnixTimeMilliseconds();
        long forkMs = now.AddMinutes(-20).ToUnixTimeMilliseconds();
        long postForkMs = now.AddMinutes(-10).ToUnixTimeMilliseconds();

        // v2 root session + forked session: copied historical message must be skipped, post-fork message kept.
        InsertSessionV2(edgeDbPath, "ses_v2_root", forkSessionId: null, createdMs: baseMs);
        InsertSessionV2(edgeDbPath, "ses_v2_fork", forkSessionId: "ses_v2_root", createdMs: forkMs);
        InsertSessionMessage(edgeDbPath, "msg_v2_orig", "ses_v2_root", "assistant", baseMs + 1000, $$"""
        {
          "model": { "id": "gpt-6.2-sol" },
          "cost": 0,
          "time": { "created": {{baseMs + 1000}}, "completed": {{baseMs + 2000}} },
          "tokens": { "input": 100, "output": 0, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);
        InsertSessionMessage(edgeDbPath, "msg_v2_copied", "ses_v2_fork", "assistant", baseMs + 1000, $$"""
        {
          "model": { "id": "gpt-6.2-sol" },
          "cost": 0,
          "time": { "created": {{baseMs + 1000}}, "completed": {{baseMs + 2000}} },
          "tokens": { "input": 100, "output": 0, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);
        InsertSessionMessage(edgeDbPath, "msg_v2_new", "ses_v2_fork", "assistant", postForkMs, $$"""
        {
          "model": { "id": "gpt-6.2-sol" },
          "cost": 0,
          "time": { "created": {{postForkMs}}, "completed": {{postForkMs + 1000}} },
          "tokens": { "input": 50, "output": 0, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);

        // v2 compaction present in BOTH session_message AND event -> must be counted ONCE with its model!
        InsertSessionMessage(edgeDbPath, "msg_compaction", "ses_v2_root", "compaction", baseMs + 5000, $$"""
        {
          "status": "completed",
          "model": { "id": "gpt-6.2-sol" },
          "cost": 0.01,
          "time": { "created": {{baseMs + 5000}} },
          "tokens": { "input": 200, "output": 100, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);
        InsertEvent(edgeDbPath, "evt_compaction_dup", "ses_v2_root", "session.usage.recorded.1", baseMs + 5010, """
        {
          "sessionID": "ses_v2_root",
          "source": "compaction",
          "cost": 0.01,
          "tokens": { "input": 200, "output": 100, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);
        // v2 title generation event (no session_message row) -> counted once!
        InsertEvent(edgeDbPath, "evt_title", "ses_v2_root", "session.usage.recorded.1", baseMs + 6000, """
        {
          "sessionID": "ses_v2_root",
          "source": "title",
          "cost": 0.002,
          "tokens": { "input": 80, "output": 20, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);

        // Empty v2 row with same ID must NOT shadow valid v1 row; excluded model v2 row MUST shadow v1 row.
        InsertSessionMessage(edgeDbPath, "msg_empty_v2", "ses_v2_root", "assistant", baseMs + 7000, $$"""
        {
          "model": { "id": "gpt-6.2-sol" },
          "cost": 0,
          "time": { "created": {{baseMs + 7000}} }
        }
        """);
        InsertSessionV1(edgeDbPath, "ses_v1_root", baseMs);
        InsertSessionV1(edgeDbPath, "ses_v1_fork", forkMs);
        InsertLegacyMessage(edgeDbPath, "msg_empty_v2", "ses_v1_root", baseMs + 7000, $$"""
        {
          "role": "assistant",
          "modelID": "gpt-6.2-sol",
          "cost": 0,
          "time": { "created": {{baseMs + 7000}}, "completed": {{baseMs + 8000}} },
          "tokens": { "input": 500, "output": 100, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);
        InsertSessionMessage(edgeDbPath, "msg_excl_shadow", "ses_v2_root", "assistant", baseMs + 7500, $$"""
        {
          "model": { "id": "codex-auto-review" },
          "cost": 0,
          "time": { "created": {{baseMs + 7500}} },
          "tokens": { "input": 999, "output": 1, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);
        InsertLegacyMessage(edgeDbPath, "msg_excl_shadow", "ses_v1_root", baseMs + 7500, $$"""
        {
          "role": "assistant",
          "modelID": "gpt-6.2-sol",
          "cost": 0,
          "time": { "created": {{baseMs + 7500}} },
          "tokens": { "input": 999, "output": 1, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);

        // v1 single-step & multi-step speed pairing + v1 forked copy skipping + post-fork keeping.
        InsertLegacyMessage(edgeDbPath, "msg_v1_speed", "ses_v1_root", baseMs + 8000, $$"""
        {
          "role": "assistant",
          "modelID": "grok-4.7",
          "cost": 0,
          "time": { "created": {{baseMs + 8000}}, "completed": {{baseMs + 10000}} }
        }
        """);
        InsertLegacyPart(edgeDbPath, "prt_speed_1", "msg_v1_speed", "ses_v1_root", baseMs + 9000, """
        {
          "type": "step-finish",
          "cost": 0,
          "tokens": { "input": 100, "output": 250, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);
        InsertLegacyPart(edgeDbPath, "prt_speed_2", "msg_v1_speed", "ses_v1_root", baseMs + 10000, """
        {
          "type": "step-finish",
          "cost": 0,
          "tokens": { "input": 100, "output": 250, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);
        // Cloned v1 fork message + part (original time < forkMs -> skipped!)
        InsertLegacyMessage(edgeDbPath, "msg_v1_fork_copy", "ses_v1_fork", forkMs + 100, $$"""
        {
          "role": "assistant",
          "modelID": "grok-4.7",
          "cost": 0,
          "time": { "created": {{baseMs + 8000}}, "completed": {{baseMs + 10000}} }
        }
        """);
        InsertLegacyPart(edgeDbPath, "prt_fork_copy", "msg_v1_fork_copy", "ses_v1_fork", forkMs + 100, """
        {
          "type": "step-finish",
          "cost": 0,
          "tokens": { "input": 100, "output": 250, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);
        // Post-fork v1 message (time >= forkMs -> kept!)
        InsertLegacyMessage(edgeDbPath, "msg_v1_fork_new", "ses_v1_fork", postForkMs, $$"""
        {
          "role": "assistant",
          "modelID": "grok-4.7",
          "cost": 0,
          "time": { "created": {{postForkMs}}, "completed": {{postForkMs + 1000}} },
          "tokens": { "input": 40, "output": 10, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);

        // Legacy Claude <4.6 & Gemini <3.1 must NOT bypass unpriced rule via reportedCost.
        InsertSessionMessage(edgeDbPath, "msg_legacy_claude", "ses_v2_root", "assistant", postForkMs + 2000, $$"""
        {
          "model": { "id": "claude-sonnet-4-5" },
          "cost": 0.25,
          "time": { "created": {{postForkMs + 2000}}, "completed": {{postForkMs + 3000}} },
          "tokens": { "input": 300, "output": 100, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);
        InsertSessionMessage(edgeDbPath, "msg_legacy_gemini", "ses_v2_root", "assistant", postForkMs + 4000, $$"""
        {
          "model": { "id": "gemini-2.5-pro" },
          "cost": 0.10,
          "time": { "created": {{postForkMs + 4000}}, "completed": {{postForkMs + 5000}} },
          "tokens": { "input": 200, "output": 50, "reasoning": 0, "cache": { "read": 0, "write": 0 } }
        }
        """);

        // Checkpoint WAL so database is quiescent without -wal/-shm files on disk.
        ExecuteSql(edgeDbPath, "PRAGMA wal_checkpoint(TRUNCATE);");
        Check(!File.Exists(edgeDbPath + "-wal") && !File.Exists(edgeDbPath + "-shm"), "quiescent edge DB has no sidecars before read");

        TokenCostCollector edgeCollector = new(pricingPath);
        TokenCostStatistics edgeStats = edgeCollector.CollectOpenCode(edgeTemp.Path, now);
        Check(!File.Exists(edgeDbPath + "-wal") && !File.Exists(edgeDbPath + "-shm"), "CollectOpenCode must not create -wal or -shm on quiescent DB");

        // Token sum breakdown:
        // msg_v2_orig: 100; msg_v2_new: 50; msg_compaction (once): 300; evt_title: 100;
        // msg_empty_v2 (from v1): 600; msg_v1_speed (2 parts): 350 + 350 = 700; msg_v1_fork_new: 50;
        // msg_legacy_claude: 400; msg_legacy_gemini: 250 -> Total = 2550
        Check(edgeStats.Lifetime.TotalTokens == 2550, $"expected 2550 edge tokens, got {edgeStats.Lifetime.TotalTokens}");

        // Legacy Claude & Gemini must remain unpriced despite reportedCost.
        TokenCostModelStatistics legacyClaude = edgeStats.Models.Single(m => m.Model == "claude-sonnet-4-5");
        Check(legacyClaude.Lifetime.TotalTokens == 400 && legacyClaude.Lifetime.CostUsd == 0m && legacyClaude.Lifetime.HasUnpricedUsage,
            "legacy claude-sonnet-4-5 must stay unpriced despite reported cost");
        TokenCostModelStatistics legacyGemini = edgeStats.Models.Single(m => m.Model == "gemini-2.5-pro");
        Check(legacyGemini.Lifetime.TotalTokens == 250 && legacyGemini.Lifetime.CostUsd == 0m && legacyGemini.Lifetime.HasUnpricedUsage,
            "legacy gemini-2.5-pro must stay unpriced despite reported cost");

        // Multi-step v1 message paired parent duration ONCE (500 output / 2000ms = 250 tok/s, plus msg_v1_fork_new has 10 output < speed floor).
        TokenCostModelStatistics grokEdge = edgeStats.Models.Single(m => m.Model == "grok-4.7");
        Check(grokEdge.Lifetime.TimedDurationMilliseconds == 2000 && grokEdge.Lifetime.GetOutputTokensPerSecond() == 250m,
            $"expected 250 tok/s over 2000ms for v1 multi-step parent timing, got {grokEdge.Lifetime.GetOutputTokensPerSecond()} over {grokEdge.Lifetime.TimedDurationMilliseconds}ms");

        // Active WAL read check: hold a WAL write transaction committed without checkpointing and verify CollectOpenCode reads it.
        using (SqliteConnection activeWriter = OpenWrite(edgeDbPath))
        {
            using SqliteCommand keepWal = activeWriter.CreateCommand();
            keepWal.CommandText = "PRAGMA wal_autocheckpoint=0;";
            keepWal.ExecuteNonQuery();
            using SqliteCommand insertWal = activeWriter.CreateCommand();
            insertWal.CommandText = "INSERT INTO session_message VALUES ('msg_wal_live', 'ses_v2_root', 'assistant', 9, $created, $created, $data)";
            insertWal.Parameters.AddWithValue("$created", postForkMs + 6000);
            insertWal.Parameters.AddWithValue("$data", $$"""{"model":{"id":"gpt-6.2-sol"},"cost":0,"time":{"created":{{postForkMs + 6000}}},"tokens":{"input":111,"output":0,"reasoning":0,"cache":{"read":0,"write":0} } }""");
            insertWal.ExecuteNonQuery();

            Check(File.Exists(edgeDbPath + "-wal"), "active WAL file exists while writer holds uncheckpointed commit");
            TokenCostStatistics walStats = edgeCollector.CollectOpenCode(edgeTemp.Path, now);
            Check(walStats.Lifetime.TotalTokens == 2661, $"expected 2661 tokens including active WAL commit, got {walStats.Lifetime.TotalTokens}");
        }

        return Task.CompletedTask;
    }

    internal static Task ViewModelAsync()
    {
        DateTimeOffset now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        TrayPopupViewModel viewModel = new(new AppSettings { UseAbsoluteResetTime = false }, () => Task.CompletedTask);
        Check(viewModel.OpenCodeSessionQuota.Title == "Session", "OpenCode session quota title");
        Check(viewModel.OpenCodeWeeklyQuota.Title == "Weekly", "OpenCode weekly quota title");
        Check(viewModel.OpenCodeMonthlyQuota.Title == "Monthly", "OpenCode monthly quota title");
        Check(ReferenceEquals(viewModel.OpenCodeQuotaPager.FirstQuota, viewModel.OpenCodeSessionQuota) &&
              ReferenceEquals(viewModel.OpenCodeQuotaPager.SecondQuota, viewModel.OpenCodeWeeklyQuota) &&
              viewModel.OpenCodeQuotaPager.ThirdQuota == null, "OpenCode large card pager holds Session and Weekly");

        viewModel.UpdateOpenCodeDashboard(new OpenCodeUsageDashboard
        {
            Snapshot = OpenCodeUsageCollector.CreateFreeSnapshot(now),
            StatusMessage = string.Empty,
        });
        Check(viewModel.OpenCodePlanDisplay == "FREE", "OpenCode Free badge display");
        Check(viewModel.OpenCodeSessionQuota.PercentText == "100%", "OpenCode Free Session 100%");
        Check(viewModel.OpenCodeWeeklyQuota.PercentText == "100%", "OpenCode Free Weekly 100%");
        Check(viewModel.OpenCodeMonthlyQuota.PercentText == "100%", "OpenCode Free Monthly 100%");

        viewModel.UpdateOpenCodeDashboard(new OpenCodeUsageDashboard
        {
            Snapshot = new OpenCodeUsageSnapshot
            {
                Source = "opencode_go",
                UpdatedAt = now,
                SubscriptionTier = "go-plus",
                Session = new UsageLimit { Name = "session", UsedPercent = 20, RemainingPercent = 80, WindowMinutes = 300, ResetsAt = now.AddHours(2).ToUnixTimeSeconds() },
                Weekly = new UsageLimit { Name = "weekly", UsedPercent = 40, RemainingPercent = 60, WindowMinutes = 10_080, ResetsAt = now.AddDays(3).ToUnixTimeSeconds() },
                Monthly = new UsageLimit { Name = "monthly", UsedPercent = 55, RemainingPercent = 45, WindowMinutes = 43_200, ResetsAt = now.AddDays(12).ToUnixTimeSeconds() },
                IsAvailable = true,
            },
            StatusMessage = string.Empty,
        });
        Check(viewModel.OpenCodePlanDisplay == "GO PLUS", "OpenCode Go Plus badge");
        Check(viewModel.OpenCodeSessionQuota.PercentText == "80%" && viewModel.OpenCodeSessionQuota.ResetText == "2h00m", "OpenCode Session display");
        Check(viewModel.OpenCodeWeeklyQuota.PercentText == "60%" && viewModel.OpenCodeWeeklyQuota.ResetText == "3d00h", "OpenCode Weekly display");
        Check(viewModel.OpenCodeMonthlyQuota.PercentText == "45%" && viewModel.OpenCodeMonthlyQuota.ResetText == "12d00h", "OpenCode Monthly display");

        viewModel.UpdateOpenCodeDashboard(OpenCodeUsageDashboard.Empty);
        Check(viewModel.OpenCodePlanDisplay == "UNKNOWN" && viewModel.OpenCodeSessionQuota.PercentText == "N/A" && viewModel.OpenCodeMonthlyQuota.PercentText == "N/A",
            "unavailable OpenCode resets badge and quotas");
        return Task.CompletedTask;
    }

    private static void CreateCredentialDatabase(string path)
    {
        ExecuteSql(path, """
            CREATE TABLE credential (
                id TEXT PRIMARY KEY,
                integration_id TEXT,
                label TEXT NOT NULL,
                value TEXT NOT NULL,
                connector_id TEXT,
                method_id TEXT,
                active INTEGER,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL
            );
            """);
    }

    private static void InsertCredential(string path, string integrationId, bool active, string value)
    {
        using SqliteConnection connection = OpenWrite(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO credential VALUES ($id, $integration, 'fixture', $value, NULL, NULL, $active, 0, 0)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$integration", integrationId);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$active", active ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void CreateUsageDatabase(string path)
    {
        ExecuteSql(path, """
            CREATE TABLE session_message (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                type TEXT NOT NULL,
                seq INTEGER NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL,
                data TEXT NOT NULL
            );
            CREATE TABLE message (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL,
                data TEXT NOT NULL
            );
            CREATE TABLE part (
                id TEXT PRIMARY KEY,
                message_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL,
                data TEXT NOT NULL
            );
            """);
    }

    private static void CreateFullUsageDatabase(string path)
    {
        CreateUsageDatabase(path);
        ExecuteSql(path, """
            CREATE TABLE session_v2 (
                id TEXT PRIMARY KEY,
                fork_session_id TEXT,
                time_created INTEGER NOT NULL
            );
            CREATE TABLE session (
                id TEXT PRIMARY KEY,
                time_created INTEGER NOT NULL
            );
            CREATE TABLE event (
                id TEXT PRIMARY KEY,
                aggregate_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                type TEXT NOT NULL,
                data TEXT NOT NULL,
                created INTEGER NOT NULL
            );
            """);
    }

    private static void InsertSessionV2(string path, string id, string? forkSessionId, long createdMs)
    {
        using SqliteConnection connection = OpenWrite(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO session_v2 VALUES ($id, $fork, $created)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$fork", (object?)forkSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", createdMs);
        command.ExecuteNonQuery();
    }

    private static void InsertSessionV1(string path, string id, long createdMs)
    {
        using SqliteConnection connection = OpenWrite(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO session VALUES ($id, $created)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$created", createdMs);
        command.ExecuteNonQuery();
    }

    private static void InsertEvent(string path, string id, string aggregateId, string type, long createdMs, string data)
    {
        using SqliteConnection connection = OpenWrite(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO event VALUES ($id, $agg, 1, $type, $data, $created)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$agg", aggregateId);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$data", data);
        command.Parameters.AddWithValue("$created", createdMs);
        command.ExecuteNonQuery();
    }

    private static void InsertSessionMessage(string path, string id, string type, long createdMs, string data) =>
        InsertSessionMessage(path, id, "ses_1", type, createdMs, data);

    private static void InsertSessionMessage(string path, string id, string sessionId, string type, long createdMs, string data)
    {
        using SqliteConnection connection = OpenWrite(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO session_message VALUES ($id, $session, $type, 1, $created, $created, $data)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$created", createdMs);
        command.Parameters.AddWithValue("$data", data);
        command.ExecuteNonQuery();
    }

    private static void InsertLegacyMessage(string path, string id, long createdMs, string data) =>
        InsertLegacyMessage(path, id, "ses_1", createdMs, data);

    private static void InsertLegacyMessage(string path, string id, string sessionId, long createdMs, string data)
    {
        using SqliteConnection connection = OpenWrite(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO message VALUES ($id, $session, $created, $created, $data)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$created", createdMs);
        command.Parameters.AddWithValue("$data", data);
        command.ExecuteNonQuery();
    }

    private static void InsertLegacyPart(string path, string id, string messageId, long createdMs, string data) =>
        InsertLegacyPart(path, id, messageId, "ses_1", createdMs, data);

    private static void InsertLegacyPart(string path, string id, string messageId, string sessionId, long createdMs, string data)
    {
        using SqliteConnection connection = OpenWrite(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO part VALUES ($id, $messageId, $session, $created, $created, $data)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$messageId", messageId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$created", createdMs);
        command.Parameters.AddWithValue("$data", data);
        command.ExecuteNonQuery();
    }

    private static void ExecuteSql(string path, string sql)
    {
        using SqliteConnection connection = OpenWrite(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenWrite(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
