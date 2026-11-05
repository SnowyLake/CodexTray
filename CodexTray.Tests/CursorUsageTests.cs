using CodexTray.App;
using CodexTray.Core;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexTray.Tests;

internal static class CursorUsageTests
{
    internal static async Task CollectionAsync()
    {
        await MonthlyCacheAsync();
        await ApiUsageAsync();
        await UnknownApiMetersAsync();
        await InvalidPagesAsync();
        await PersistentCacheAsync();
    }

    private static async Task MonthlyCacheAsync()
    {
        using TempDirectory temp = new();
        string token = Token("account-a");
        WriteToken(temp.Path, token);
        DateTimeOffset now = new(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        DateTimeOffset october = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset september = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset midnight = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        string first = Page(true, "next/+", Row("one", now, "grok-4.6", 10, 20, 30, 40), Row("bot", now, "bot-model", 1, 2, 3, 4, product: "bot"),
                            Row("build", now, "build-model", 1, 2, 3, 4, product: "build"), Row("availability", now, "none", 0, 0, 0, 0, hasMeters: false));
        string second = Page(false, null, Row("one", now, "duplicate", 10, 20, 30, 40), Row("two", now, "codex-auto-review", 1, 2, 3, 4));
        string octoberPage = Page(false, null, Row("midnight", midnight, "boundary", 1, 2, 3, 4), Row("oct", october, "oct-model", 1, 2, 3, 4));
        string septemberPage = Page(false, null, Row("sep", september, "sep-model", 1, 2, 3, 4));
        List<string> requests = [];
        int phase = 0;
        using HttpClient client = new(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Check(request.RequestUri!.AbsolutePath.EndsWith("/GetMe", StringComparison.Ordinal) && request.Headers.Authorization?.Parameter == token, "GetMe uses the current synthetic JWT");
                requests.Add("identity");
                return Task.FromResult(Response("{\"createdAt\":\"2026-09-01T00:00:00Z\"}"));
            }
            Check(request.Method == HttpMethod.Get && request.RequestUri!.Host == "cursor.com" && request.RequestUri.AbsolutePath == "/api/dashboard/v2/query-billing-items", "v2 billing endpoint");
            Dictionary<string, string> query = Query(request.RequestUri!);
            Check(query["scope"] == "user" && !query.ContainsKey("product") && query["pageSize"] == "500", "v2 queries both products with user scope and full page size");
            Check(request.Headers.GetValues("Cookie").Single() == "WorkosCursorSessionToken=" + Uri.EscapeDataString((token == Token("account-a") ? "account-a" : "account-b") + "::" + token),
                  "account session cookie");
            DateTimeOffset start = DateTimeOffset.Parse(query["startTime"]);
            DateTimeOffset end = DateTimeOffset.Parse(query["endTime"]);
            Check(start.Offset == TimeSpan.Zero && start.Day == 1 && start.TimeOfDay == TimeSpan.Zero && end == (start.Month == 11 ? now : start.AddMonths(1)), "UTC monthly bounds");
            string cursor = query.GetValueOrDefault("cursor", "head");
            requests.Add($"{start.Month}:{cursor}");
            if (phase == 2 && start.Month == 11)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }
            if (phase == 3 && start.Month == 10 || phase == 5 && start.Month == 11 && cursor != "head")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }
            if (phase == 4 && start.Month == 9)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }
            return Task.FromResult(Response(start.Month switch
            {
                11 when cursor == "head" => phase is 1 or 5 ? first.Replace("grok-4.6", "updated-model", StringComparison.Ordinal) : first,
                11 => second,
                10 => octoberPage,
                9 => septemberPage,
                _ => throw new InvalidOperationException("unexpected month"),
            }));
        }));
        CursorUsageCollector collector = new(client, temp.Path);
        IReadOnlyList<CursorUsageEvent> rows = await collector.CollectAsync(now);
        Check(requests.SequenceEqual(["identity", "11:head", "11:next/+", "10:head", "9:head"]) && rows.Count == 4, "all months and cursor pages collected once");
        CursorUsageEvent firstRow = rows.Single(row => row.Model == "grok-4.6");
        Check(firstRow.TotalTokens == 100 && firstRow.CacheReadTokens == 30 && firstRow.CacheableInputTokens == 80, "four token meters and cached write are included");
        Check(rows.Any(row => row.Model == "codex-auto-review") && rows.All(row => row.Model is not ("bot-model" or "build-model" or "boundary" or "duplicate")),
              "Cursor product filtering, duplicate IDs, non-metered rows and next-month midnight");
        requests.Clear();
        rows = await collector.CollectAsync(now);
        Check(rows.Count == 4 && requests.SequenceEqual(["11:head", "10:head"]), "current and previous heads reuse full pages and older month is cached");
        phase = 1;
        requests.Clear();
        rows = await collector.CollectAsync(now);
        Check(rows.Any(row => row.Model == "updated-model") && requests.SequenceEqual(["11:head", "11:next/+", "10:head"]), "changed current head reloads later pages");
        phase = 2;
        requests.Clear();
        Check((await collector.CollectAsync(now)).Count == 0 && requests.SequenceEqual(["11:head"]), "current failure clears all cloud usage");
        phase = 0;
        requests.Clear();
        Check((await collector.CollectAsync(now)).Count == 4 && requests.SequenceEqual(["identity", "11:head", "11:next/+", "10:head", "9:head"]), "after failure all months reload");
        phase = 3;
        requests.Clear();
        Check((await collector.CollectAsync(now)).Count == 0 && requests.SequenceEqual(["11:head", "10:head"]), "failure of a cached previous month clears all cloud usage");
        phase = 0;
        await collector.CollectAsync(now);
        phase = 5;
        requests.Clear();
        Check((await collector.CollectAsync(now)).Count == 0 && requests.SequenceEqual(["11:head", "11:next/+"]), "partial current month failure discards every page");
        phase = 0;
        await collector.CollectAsync(now);
        token = Token("account-b");
        WriteToken(temp.Path, token);
        requests.Clear();
        Check((await collector.CollectAsync(now)).Count == 4 && requests.SequenceEqual(["identity", "11:head", "11:next/+", "10:head", "9:head"]), "account change clears all cached months");
        phase = 4;
        requests.Clear();
        CursorUsageCollector boundary = new(client, temp.Path);
        Check((await boundary.CollectAsync(now)).Count == 3 && requests.SequenceEqual(["identity", "11:head", "11:next/+", "10:head", "9:head"]),
              "unavailable older history retains successful recent months");
        requests.Clear();
        Check((await boundary.CollectAsync(now)).Count == 3 && requests.SequenceEqual(["11:head", "10:head"]), "unreadable history boundary is retained until restart");
    }

    private static async Task ApiUsageAsync()
    {
        using TempDirectory temp = new();
        WriteToken(temp.Path, Token("account"));
        DateTimeOffset now = new(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        string cachePath = Path.Combine(temp.Path, "api-cache.json.gz");
        int pages = 0;
        bool invalidTotal = false;
        using HttpClient client = new(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(Response("{\"createdAt\":\"2026-11-01T00:00:00Z\"}"));
            }
            pages++;
            Dictionary<string, string> query = Query(request.RequestUri!);
            Check(!query.ContainsKey("product"), "mixed ledger request does not exclude API usage");
            return Task.FromResult(Response(query.ContainsKey("cursor")
                ? Page(false, null, ApiRow("api", now), Row("cursor", now, "grok-4.7", 1, 2, 3, 4),
                       Row("chat", now, "excluded", 1, 2, 3, 4, product: "grok-chat"), Row("imagine", now, "excluded", 1, 2, 3, 4, product: "grok-imagine"))
                : Page(true, "next", ApiRow("api", now, total: invalidTotal ? "-1" : "100"), ApiRow("image", now, total: "150", images: true),
                       Row("bot", now, "excluded", 1, 2, 3, 4, product: "grok-bot"), Row("build", now, "excluded", 1, 2, 3, 4, product: "grok-build"))));
        }));
        IReadOnlyList<CursorUsageEvent> events = await new CursorUsageCollector(client, temp.Path, cachePath).CollectAsync(now);
        Check(events.Count == 3 && pages == 2 && events.All(row => row.Model == "grok-4.7"), "API and Cursor coexist across pages without duplicates or other products");
        CursorUsageEvent api = events.Single(row => row.Product == "grok-api" && row.TotalTokens == 100);
        Check(api.TotalTokens == 100 && api.CacheReadTokens == 30 && api.CacheableInputTokens == 40,
              "API string meters count independent cached input and reasoning output exactly once");
        CursorUsageEvent image = events.Single(row => row.Product == "grok-api" && row.TotalTokens == 150);
        Check(image.CacheReadTokens == 30 && image.CacheableInputTokens == 90, "API image tokens join uncached input without clearing either cloud product");
        pages = 0;
        IReadOnlyList<CursorUsageEvent> restarted = await new CursorUsageCollector(client, temp.Path, cachePath).CollectAsync(now);
        Check(events.SequenceEqual(restarted) && pages == 1, "API product and counts survive disk cache and unchanged head reuse");
        TokenCostStatistics stats = new TokenCostCollector().CollectGrok(temp.Path, now, cursorUsage: restarted);
        Check(stats.Today.TotalTokens == 260 && stats.Today.CostUsd == 0.0009135m && !stats.Today.HasUnpricedUsage &&
              stats.SessionLogs.Count == 0 && stats.Today.GetOutputTokensPerSecond() == null, "parsed API reasoning receives output pricing with no local log or speed samples");
        TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
        viewModel.UpdateGrokTokenCost(stats);
        Check(viewModel.GrokTokenCost.DonutSegments.Any(segment => segment.Label == "Grok-4.7-API") &&
              viewModel.GrokTokenCost.DonutSegments.Any(segment => segment.Label == "Grok-4.7-Cursor"), "API image input preserves both cloud donut segments");
        invalidTotal = true;
        Check((await new CursorUsageCollector(client, temp.Path, cachePath).CollectAsync(now)).Count == 0, "invalid API totals are never published as valid usage");
        Check(ReadCache(cachePath).Contains("grok-api", StringComparison.Ordinal), "failed refresh preserves the complete API cache on disk");
        System.Text.Json.Nodes.JsonObject corrupt = System.Text.Json.Nodes.JsonNode.Parse(ReadCache(cachePath))!.AsObject();
        corrupt["Months"]!.AsObject().First().Value!["Events"]![0]!["Product"] = "grok-build";
        WriteCache(cachePath, corrupt.ToJsonString());
        invalidTotal = false;
        pages = 0;
        Check((await new CursorUsageCollector(client, temp.Path, cachePath).CollectAsync(now)).Count == 3 && pages == 2, "unsupported cached products force a fresh complete collection");
    }

    private static async Task UnknownApiMetersAsync()
    {
        using TempDirectory temp = new();
        WriteToken(temp.Path, Token("account"));
        DateTimeOffset now = new(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        foreach (string scenario in new[] { "new-meter", "missing-breakdown", "invalid-breakdown", "overlapping-counts", "overflow", "tool-calls" })
        {
            System.Text.Json.Nodes.JsonObject api = JsonSerializer.SerializeToNode(ApiRow("api", now))!.AsObject();
            switch (scenario)
            {
                case "new-meter":
                    api["totalTokens"] = "120";
                    api["usage"]!["Future modality tokens"] = "20";
                    break;
                case "missing-breakdown":
                    api.Remove("usage");
                    break;
                case "invalid-breakdown":
                    api["usage"]!["Prompt text tokens"] = "not-a-count";
                    break;
                case "overlapping-counts":
                    api["totalTokens"] = "90";
                    break;
                case "overflow":
                    api["usage"]!["Prompt text tokens"] = long.MaxValue;
                    break;
                case "tool-calls":
                    api["usage"]!["Web searches"] = "7";
                    break;
            }
            string body = Page(false, null, api, Row("cursor", now, "grok-4.7", 1, 2, 3, 4));
            using HttpClient client = new(new Handler((request, _) => Task.FromResult(Response(request.Method == HttpMethod.Post
                ? "{\"createdAt\":\"2026-11-01T00:00:00Z\"}" : body))));
            string cachePath = Path.Combine(temp.Path, scenario + ".json.gz");
            IReadOnlyList<CursorUsageEvent> events = await new CursorUsageCollector(client, temp.Path, cachePath).CollectAsync(now);
            Check(events.Count == 2, $"{scenario}: API breakdown changes do not discard either cloud product");
            CursorUsageEvent result = events.Single(row => row.Product == "grok-api");
            long expectedTotal = scenario == "new-meter" ? 120 : scenario == "overlapping-counts" ? 90 : 100;
            bool hasKnownCounts = scenario is "new-meter" or "tool-calls";
            long expectedUnpriced = hasKnownCounts ? expectedTotal - 100 : expectedTotal;
            Check(result.TotalTokens == expectedTotal && result.UnpricedTokens == expectedUnpriced &&
                  result.CacheableInputTokens == (hasKnownCounts ? 40 : 0) && result.CacheReadTokens == (hasKnownCounts ? 30 : 0),
                  $"{scenario}: preserve validated totals without guessing the unknown breakdown");
            IReadOnlyList<CursorUsageEvent> restarted = await new CursorUsageCollector(client, temp.Path, cachePath).CollectAsync(now);
            Check(events.SequenceEqual(restarted), $"{scenario}: unpriced counts survive cache restart");
            TokenCostStatistics stats = new TokenCostCollector().CollectGrok(temp.Path, now, cursorUsage: restarted);
            Check(stats.Today.TotalTokens == expectedTotal + 10 && stats.Today.CostUsd == (hasKnownCounts ? 0.0004185m : 0.0000235m) &&
                  stats.Today.HasUnpricedUsage == (expectedUnpriced > 0), $"{scenario}: unknown tokens never become paid output and known costs remain");
            TrayPopupViewModel viewModel = new(new AppSettings(), () => Task.CompletedTask);
            viewModel.UpdateGrokTokenCost(stats);
            Check(viewModel.GrokTokenCost.DonutSegments.Count == 2 &&
                  viewModel.GrokTokenCost.DonutSegments.Single(segment => segment.Label == "Grok-4.7-API").Tooltip.Contains("Partial cost") == (expectedUnpriced > 0),
                  $"{scenario}: both donut segments remain and API partial costs are visible");
            foreach (long invalidUnpriced in new[] { -1L, expectedTotal + 1 })
            {
                System.Text.Json.Nodes.JsonObject corrupt = System.Text.Json.Nodes.JsonNode.Parse(ReadCache(cachePath))!.AsObject();
                corrupt["Months"]!.AsObject().First().Value!["Events"]![0]!["UnpricedTokens"] = invalidUnpriced;
                WriteCache(cachePath, corrupt.ToJsonString());
                Check((await new CursorUsageCollector(client, temp.Path, cachePath).CollectAsync(now)).SequenceEqual(events), "invalid cached unpriced counts force a fresh collection");
            }
        }
    }

    private static async Task InvalidPagesAsync()
    {
        using TempDirectory temp = new();
        WriteToken(temp.Path, Token("account"));
        DateTimeOffset now = new(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        string good = Page(false, null, Row("one", now, "grok", 1, 2, 3, 4));
        string[] invalid = ["", "{\"available\":true}", "{\"data\":[{}],\"pagination\":{\"hasMore\":true}}", Page(true, null, Row("one", now, "grok", 1, 2, 3, 4)),
                            Page(true, "", Row("one", now, "grok", 1, 2, 3, 4)),
                            Page(false, "unexpected", Row("one", now, "grok", 1, 2, 3, 4)), Page(false, null, Row("one", now, "grok", 1, 2, 3, 4, total: 11))];
        foreach (string body in invalid)
        {
            int identityCalls = 0;
            int billingCalls = 0;
            using HttpClient client = new(new Handler((request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    identityCalls++;
                    return Task.FromResult(Response("{\"createdAt\":\"2026-11-01T00:00:00Z\"}"));
                }
                return Task.FromResult(Response(++billingCalls == 1 ? body : good));
            }));
            CursorUsageCollector collector = new(client, temp.Path);
            Check((await collector.CollectAsync(now)).Count == 0, "invalid empty, availability, cursor or total response fails");
            Check((await collector.CollectAsync(now)).Count == 1 && identityCalls == 2 && billingCalls == 2, "failed response is never cached as success");
        }
        System.Text.Json.Nodes.JsonObject missingModel = System.Text.Json.Nodes.JsonNode.Parse(good)!.AsObject();
        missingModel["data"]![0]!.AsObject().Remove("displayExtras");
        using HttpClient unnamed = new(new Handler((request, _) => Task.FromResult(Response(request.Method == HttpMethod.Post
            ? "{\"createdAt\":\"2026-11-01T00:00:00Z\"}" : missingModel.ToJsonString()))));
        Check((await new CursorUsageCollector(unnamed, temp.Path).CollectAsync(now)).Single().Model == "unknown", "missing model metadata preserves tokens with unknown pricing");
        using HttpClient repeated = new(new Handler((request, _) => Task.FromResult(Response(request.Method == HttpMethod.Post
            ? "{\"createdAt\":\"2026-11-01T00:00:00Z\"}"
            : Page(true, "same", Row("one", now, "grok", 1, 2, 3, 4))))));
        Check((await new CursorUsageCollector(repeated, temp.Path).CollectAsync(now)).Count == 0, "repeated cursor fails the entire month");
        using CancellationTokenSource cancellation = new();
        using HttpClient canceled = new(new Handler((_, token) => { cancellation.Cancel(); return Task.FromCanceled<HttpResponseMessage>(token); }));
        try
        {
            await new CursorUsageCollector(canceled, temp.Path).CollectAsync(now, cancellation.Token);
            throw new InvalidOperationException("caller cancellation must propagate");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static async Task PersistentCacheAsync()
    {
        using TempDirectory temp = new();
        string token = Token("account-a");
        WriteToken(temp.Path, token);
        string cachePath = Path.Combine(temp.Path, "usage-cache.json.gz");
        DateTimeOffset november = new(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        DateTimeOffset december = new(2026, 12, 5, 4, 0, 0, TimeSpan.Zero);
        int phase = 0;
        List<string> requests = [];
        CancellationTokenSource? pendingCancellation = null;
        using HttpClient client = new(new Handler((request, cancellation) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                requests.Add("identity");
                return Task.FromResult(Response("{\"createdAt\":\"2026-09-01T00:00:00Z\"}"));
            }
            Dictionary<string, string> query = Query(request.RequestUri!);
            DateTimeOffset start = DateTimeOffset.Parse(query["startTime"]);
            string cursor = query.GetValueOrDefault("cursor", "head");
            requests.Add($"{start.Month}:{cursor}");
            if (phase == 2 && start.Month == 11 || phase == 4 && start.Month == 9 || phase == 6 && start.Month == 10)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }
            if (phase == 3 && start.Month == 11 && cursor != "head")
            {
                pendingCancellation!.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(cancellation);
            }
            DateTimeOffset timestamp = start.AddDays(1);
            string account = token == Token("account-a") ? "a" : "b";
            if (phase == 7 && start.Month == 9)
            {
                return Task.FromResult(Response(Page(false, null, ApiRow("historic-api", timestamp, total: "120"))));
            }
            if (start.Month == 11 && cursor == "head")
            {
                string model = phase is 1 or 3 ? "updated-model" : "initial-model";
                return Task.FromResult(Response(Page(true, "next", Row(account + "-head", timestamp, model, 1, 2, 3, 4))));
            }
            return Task.FromResult(Response(Page(false, null, Row(account + "-" + start.Month + "-" + cursor, timestamp, account + "-" + start.Month, 1, 2, 3, 4))));
        }));
        CursorUsageCollector NewCollector() => new(client, temp.Path, cachePath);
        IReadOnlyList<CursorUsageEvent> initial = await NewCollector().CollectAsync(november);
        Check(initial.Count == 4 && requests.SequenceEqual(["identity", "11:head", "11:next", "10:head", "9:head"]), "initial complete months are cached");
        string saved = ReadCache(cachePath);
        Check(!saved.Contains(token, StringComparison.Ordinal) && !saved.Contains("account-a", StringComparison.Ordinal) && !saved.Contains("billingItemId", StringComparison.Ordinal) &&
              !saved.Contains("pagination", StringComparison.Ordinal), "disk cache excludes raw credentials and first page");
        requests.Clear();
        Check((await NewCollector().CollectAsync(november)).Count == 4 && requests.SequenceEqual(["11:head", "10:head"]), "restart reuses identity and older completed month");
        IReadOnlyList<CursorUsageEvent> restarted = await NewCollector().CollectAsync(november);
        Check(initial.OrderBy(item => item.Timestamp).ThenBy(item => item.Model).SequenceEqual(restarted.OrderBy(item => item.Timestamp).ThenBy(item => item.Model)),
              "gzip restart preserves every cached event without loss");
        byte[] before = File.ReadAllBytes(cachePath);
        DateTime unchangedTime = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(cachePath, unchangedTime);
        requests.Clear();
        IReadOnlyList<CursorUsageEvent> later = await NewCollector().CollectAsync(november.AddMinutes(5));
        Check(initial.OrderBy(item => item.Timestamp).ThenBy(item => item.Model).SequenceEqual(later.OrderBy(item => item.Timestamp).ThenBy(item => item.Model)) &&
              requests.SequenceEqual(["11:head", "10:head"]) && File.ReadAllBytes(cachePath).SequenceEqual(before) && File.GetLastWriteTimeUtc(cachePath) == unchangedTime,
              "unchanged heads do not rewrite the cache as time advances");
        string migrationPath = Path.Combine(temp.Path, "migration.json.gz");
        string oldPath = migrationPath[..^3];
        File.WriteAllText(oldPath, saved);
        requests.Clear();
        IReadOnlyList<CursorUsageEvent> migrated = await new CursorUsageCollector(client, temp.Path, migrationPath).CollectAsync(november);
        Check(migrated.OrderBy(item => item.Timestamp).ThenBy(item => item.Model).SequenceEqual(initial.OrderBy(item => item.Timestamp).ThenBy(item => item.Model)) &&
              requests.SequenceEqual(["11:head", "10:head"]) && File.Exists(migrationPath) && !File.Exists(oldPath), "legacy JSON migrates losslessly without additional identity or history requests");
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(ReadCache(migrationPath)), System.Text.Json.Nodes.JsonNode.Parse(saved)),
              "migration stores the original complete schema as gzip");

        foreach (bool compressed in new[] { false, true })
        {
            string cursorOnlyPath = Path.Combine(temp.Path, "cursor-only-" + compressed + ".json.gz");
            System.Text.Json.Nodes.JsonObject cursorOnly = System.Text.Json.Nodes.JsonNode.Parse(saved)!.AsObject();
            cursorOnly["Version"] = 1;
            if (compressed)
            {
                WriteCache(cursorOnlyPath, cursorOnly.ToJsonString());
            }
            else
            {
                File.WriteAllText(cursorOnlyPath[..^3], cursorOnly.ToJsonString());
            }
            requests.Clear();
            Check((await new CursorUsageCollector(client, temp.Path, cursorOnlyPath).CollectAsync(november)).Count == 4 &&
                  requests.SequenceEqual(["identity", "11:head", "11:next", "10:head", "9:head"]), "Cursor-only v1 cache cannot hide historic API usage");
            Check(System.Text.Json.Nodes.JsonNode.Parse(ReadCache(cursorOnlyPath))!["Version"]!.GetValue<int>() == 3, "fresh history replaces the v1 cache");
        }

        string versionTwoPath = Path.Combine(temp.Path, "version-two.json.gz");
        System.Text.Json.Nodes.JsonObject versionTwo = System.Text.Json.Nodes.JsonNode.Parse(saved)!.AsObject();
        versionTwo["Version"] = 2;
        foreach (var month in versionTwo["Months"]!.AsObject())
        {
            foreach (var entry in month.Value!["Events"]!.AsArray())
            {
                entry!.AsObject().Remove("UnpricedTokens");
            }
        }
        WriteCache(versionTwoPath, versionTwo.ToJsonString());
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, versionTwoPath).CollectAsync(november)).SequenceEqual(initial) &&
              requests.SequenceEqual(["identity", "11:head", "10:head"]) && System.Text.Json.Nodes.JsonNode.Parse(ReadCache(versionTwoPath))!["Version"]!.GetValue<int>() == 3,
              "v2 complete months survive migration while the account history boundary is rechecked");
        string truncatedPath = Path.Combine(temp.Path, "version-two-truncated.json.gz");
        System.Text.Json.Nodes.JsonObject truncated = System.Text.Json.Nodes.JsonNode.Parse(versionTwo.ToJsonString())!.AsObject();
        truncated["HistoryStart"] = "2026-10-01T00:00:00+00:00";
        var truncatedMonths = truncated["Months"]!.AsObject();
        truncatedMonths.Remove(truncatedMonths.First(pair => pair.Key.StartsWith("2026-09", StringComparison.Ordinal)).Key);
        WriteCache(truncatedPath, truncated.ToJsonString());
        phase = 7;
        requests.Clear();
        IReadOnlyList<CursorUsageEvent> recoveredHistory = await new CursorUsageCollector(client, temp.Path, truncatedPath).CollectAsync(november);
        Check(recoveredHistory.Count == 4 && recoveredHistory.Any(row => row.Product == "grok-api" && row.Timestamp.Month == 9 && row.UnpricedTokens == 20) &&
              requests.SequenceEqual(["identity", "11:head", "10:head", "9:head"]), "v2 parsing failures cannot permanently hide readable API history after migration");
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, truncatedPath).CollectAsync(november)).SequenceEqual(recoveredHistory) &&
              requests.SequenceEqual(["11:head", "10:head"]), "recovered history and partial counts persist without another identity or old-month query");
        phase = 0;

        foreach (string invalidProperty in new[] { "Version", "AccountHash" })
        {
            string invalidLegacyPath = Path.Combine(temp.Path, "invalid-" + invalidProperty + ".json.gz");
            System.Text.Json.Nodes.JsonObject invalidLegacy = System.Text.Json.Nodes.JsonNode.Parse(saved)!.AsObject();
            if (invalidProperty == "Version")
            {
                invalidLegacy[invalidProperty] = 99;
            }
            else
            {
                invalidLegacy[invalidProperty] = new string('A', 64);
            }
            File.WriteAllText(invalidLegacyPath[..^3], invalidLegacy.ToJsonString());
            requests.Clear();
            Check((await new CursorUsageCollector(client, temp.Path, invalidLegacyPath).CollectAsync(november)).Count == 4 &&
                  requests.SequenceEqual(["identity", "11:head", "11:next", "10:head", "9:head"]), "invalid legacy schema or account forces fresh collection");
        }
        string failedMigration = Path.Combine(temp.Path, "failed-migration.json.gz");
        string failedLegacy = failedMigration[..^3];
        File.WriteAllText(failedLegacy, saved);
        Directory.CreateDirectory(failedMigration + ".tmp");
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, failedMigration).CollectAsync(november)).Count == 4 && requests.SequenceEqual(["11:head", "10:head"]) &&
              !File.Exists(failedMigration) && File.ReadAllText(failedLegacy) == saved, "failed migration keeps legacy file and returns live data");

        string priorityPath = Path.Combine(temp.Path, "priority.json.gz");
        File.Copy(cachePath, priorityPath);
        File.WriteAllText(priorityPath[..^3], "not-json");
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, priorityPath).CollectAsync(november)).Count == 4 && requests.SequenceEqual(["11:head", "10:head"]),
              "existing gzip takes precedence over legacy JSON");
        File.WriteAllText(priorityPath, "invalid-gzip");
        File.WriteAllText(priorityPath[..^3], saved);
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, priorityPath).CollectAsync(november)).Count == 4 &&
              requests.SequenceEqual(["identity", "11:head", "11:next", "10:head", "9:head"]), "corrupt gzip triggers fresh collection rather than legacy fallback");

        string offlinePath = Path.Combine(temp.Path, "offline-migration.json.gz");
        File.WriteAllText(offlinePath[..^3], saved);
        phase = 2;
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, offlinePath).CollectAsync(november)).Count == 0 && requests.SequenceEqual(["11:head"]) &&
              File.Exists(offlinePath) && !File.Exists(offlinePath[..^3]), "migration can complete despite a subsequent current-month failure");
        phase = 1;
        requests.Clear();
        Check((await NewCollector().CollectAsync(november)).Any(row => row.Model == "updated-model") && requests.SequenceEqual(["11:head", "11:next", "10:head"]),
              "changed current head fetches remaining pages after restart");
        string dormantPath = Path.Combine(temp.Path, "dormant-cache.json.gz");
        File.Copy(cachePath, dormantPath);
        DateTimeOffset february = new(2027, 2, 5, 4, 0, 0, TimeSpan.Zero);
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, dormantPath).CollectAsync(february)).Count == 7 &&
              requests.SequenceEqual(["2:head", "1:head", "12:head", "11:head"]), "an unclosed month is revalidated even after several months offline");
        System.Text.Json.Nodes.JsonObject dormant = System.Text.Json.Nodes.JsonNode.Parse(ReadCache(dormantPath))!.AsObject();
        System.Text.Json.Nodes.JsonObject dormantMonths = dormant["Months"]!.AsObject();
        System.Text.Json.Nodes.JsonObject closedNovember = dormantMonths.Single(pair => pair.Key.StartsWith("2026-11", StringComparison.Ordinal)).Value!.AsObject();
        Check(DateTimeOffset.Parse(closedNovember["Through"]!.GetValue<string>()) == new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
              "unchanged head still advances cached coverage to the end of the month");
        phase = 2;
        requests.Clear();
        Check((await NewCollector().CollectAsync(november)).Count == 0 && requests.SequenceEqual(["11:head"]), "network failure does not return stale disk data");
        phase = 1;
        requests.Clear();
        Check((await NewCollector().CollectAsync(november)).Count == 4 && requests.SequenceEqual(["11:head", "10:head"]), "network failure retains completed months on disk");
        phase = 0;
        requests.Clear();
        Check((await NewCollector().CollectAsync(december)).Count == 5 && requests.SequenceEqual(["12:head", "11:head", "11:next"]), "new month fetches new data and revalidates previous head");
        token = Token("account-b");
        WriteToken(temp.Path, token);
        requests.Clear();
        IReadOnlyList<CursorUsageEvent> switched = await NewCollector().CollectAsync(november);
        Check(switched.Count == 4 && requests.SequenceEqual(["identity", "11:head", "11:next", "10:head", "9:head"]) && switched.Any(row => row.Model == "b-9") &&
              switched.All(row => row.Model != "a-9"), "different account cannot reuse cached months");

        string valid = ReadCache(cachePath);
        foreach (int corruption in new[] { 0, 1, 2, 3, 4, 5 })
        {
            if (corruption == 0)
            {
                File.WriteAllText(cachePath, "not-json");
            }
            else if (corruption == 5)
            {
                WriteCache(cachePath, "not-json");
            }
            else
            {
                System.Text.Json.Nodes.JsonObject root = System.Text.Json.Nodes.JsonNode.Parse(valid)!.AsObject();
                if (corruption == 1)
                {
                    root["Version"] = 99;
                }
                else
                {
                    System.Text.Json.Nodes.JsonObject months = root["Months"]!.AsObject();
                    KeyValuePair<string, System.Text.Json.Nodes.JsonNode?> selected = months.First();
                    System.Text.Json.Nodes.JsonObject month = selected.Value!.AsObject();
                    if (corruption == 2)
                    {
                        System.Text.Json.Nodes.JsonObject entry = month["Events"]!.AsArray()[0]!.AsObject();
                        entry["TotalTokens"] = -1;
                    }
                    else
                    {
                        DateTimeOffset start = DateTimeOffset.Parse(selected.Key);
                        month["Through"] = (corruption == 3 ? start.AddMonths(2) : start.AddHours(1)).ToString("O");
                    }
                }
                WriteCache(cachePath, root.ToJsonString());
            }
            requests.Clear();
            Check((await NewCollector().CollectAsync(november)).Count == 4 && requests.SequenceEqual(["identity", "11:head", "11:next", "10:head", "9:head"]),
                  "corrupt, wrong-version, negative-token or invalid coverage cache is ignored");
        }
        string fileParent = Path.Combine(temp.Path, "not-a-directory");
        File.WriteAllText(fileParent, "file");
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, Path.Combine(fileParent, "cache.json.gz")).CollectAsync(november)).Count == 4, "cache write failure does not discard live usage");

        valid = ReadCache(cachePath);
        phase = 3;
        pendingCancellation = new CancellationTokenSource();
        try
        {
            await NewCollector().CollectAsync(november, pendingCancellation.Token);
            throw new InvalidOperationException("expected cancellation during pagination");
        }
        catch (OperationCanceledException) when (pendingCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            pendingCancellation.Dispose();
        }
        Check(ReadCache(cachePath) == valid, "cancellation never writes a partial month");

        string boundaryPath = Path.Combine(temp.Path, "boundary-cache.json.gz");
        phase = 4;
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, boundaryPath).CollectAsync(november)).Count == 3 && requests.Contains("9:head"), "unavailable historic month keeps recent data");
        phase = 0;
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, boundaryPath).CollectAsync(november)).Count == 3 && requests.SequenceEqual(["11:head", "10:head"]),
              "restart retains the unavailable history boundary");
        File.Delete(boundaryPath);
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, boundaryPath).CollectAsync(november)).Count == 4 &&
              requests.SequenceEqual(["identity", "11:head", "11:next", "10:head", "9:head"]), "deleting the cache retries older history");

        string gapPath = Path.Combine(temp.Path, "gap-cache.json.gz");
        DateTimeOffset september = new(2026, 9, 5, 4, 0, 0, TimeSpan.Zero);
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, gapPath).CollectAsync(september)).Count == 1 && requests.SequenceEqual(["identity", "9:head"]),
              "the initial September data is cached");
        phase = 6;
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, gapPath).CollectAsync(november)).Count == 0 && requests.SequenceEqual(["11:head", "11:next", "10:head"]),
              "a missing middle month failure cannot truncate older cached history");
        System.Text.Json.Nodes.JsonObject gap = System.Text.Json.Nodes.JsonNode.Parse(ReadCache(gapPath))!.AsObject();
        System.Text.Json.Nodes.JsonObject gapMonths = gap["Months"]!.AsObject();
        Check(DateTimeOffset.Parse(gap["HistoryStart"]!.GetValue<string>()) == new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) &&
              gapMonths.Any(pair => pair.Key.StartsWith("2026-09", StringComparison.Ordinal)) && gapMonths.Any(pair => pair.Key.StartsWith("2026-11", StringComparison.Ordinal)) &&
              !gapMonths.Any(pair => pair.Key.StartsWith("2026-10", StringComparison.Ordinal)), "disk retains consistent history boundary and completed months");
        phase = 0;
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, gapPath).CollectAsync(november)).Count == 4 && requests.SequenceEqual(["11:head", "10:head", "9:head"]),
              "restart fills the gap without repeating identity or cached November pagination");
        requests.Clear();
        Check((await new CursorUsageCollector(client, temp.Path, gapPath).CollectAsync(november)).Count == 4 && requests.SequenceEqual(["11:head", "10:head"]),
              "once closed, the older September cache is reused without another request");
    }

    private static string ReadCache(string path)
    {
        using FileStream file = File.OpenRead(path);
        using System.IO.Compression.GZipStream compressed = new(file, System.IO.Compression.CompressionMode.Decompress);
        using StreamReader reader = new(compressed, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteCache(string path, string json)
    {
        using FileStream file = File.Create(path);
        using System.IO.Compression.GZipStream compressed = new(file, System.IO.Compression.CompressionMode.Compress);
        using StreamWriter writer = new(compressed, new UTF8Encoding(false));
        writer.Write(json);
    }
    internal static async Task CredentialsAsync()
    {
        using TempDirectory temp = new();
        string expectedToken = Token("synthetic-account");
        DateTimeOffset now = new(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        int requests = 0;
        using HttpClient client = new(new Handler((request, _) =>
        {
            requests++;
            if (request.Method == HttpMethod.Post)
            {
                Check(request.Headers.Authorization?.Parameter == expectedToken, "read expected synthetic JWT");
                return Task.FromResult(Response("{\"createdAt\":\"2026-11-01T00:00:00Z\"}"));
            }
            return Task.FromResult(Response(Page(false, null)));
        }));
        Check((await new CursorUsageCollector(client, temp.Path).CollectAsync(now)).Count == 0 && requests == 0, "missing credential does not send HTTP");
        WriteToken(temp.Path, "invalid-token");
        Check((await new CursorUsageCollector(client, temp.Path).CollectAsync(now)).Count == 0 && requests == 0, "invalid JWT does not send HTTP");
        foreach (string payload in new[] { "[]", "{}", "{\"sub\":\"\"}" })
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            WriteToken(temp.Path, "header." + encoded + ".signature");
            Check((await new CursorUsageCollector(client, temp.Path).CollectAsync(now)).Count == 0 && requests == 0, "non-object or missing JWT subject does not send HTTP");
        }
        WriteToken(temp.Path, expectedToken);
        await new CursorUsageCollector(client, temp.Path).CollectAsync(now);
        Check(requests == 2, "plaintext SQLite token");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plaintext = Encoding.UTF8.GetBytes(expectedToken);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using (AesGcm aes = new(key, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        byte[] wrappedKey = "DPAPI"u8.ToArray().Concat(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)).ToArray();
        string localState = JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = Convert.ToBase64String(wrappedKey) } });
        File.WriteAllText(Path.Combine(temp.Path, "Local State"), localState);
        WriteToken(temp.Path, "cursor-safe-storage:v1:" + Convert.ToBase64String("v10"u8.ToArray().Concat(nonce).Concat(ciphertext).Concat(tag).ToArray()));
        await new CursorUsageCollector(client, temp.Path).CollectAsync(now);
        Check(requests == 4, "DPAPI and AES-GCM credential");
        File.WriteAllText(Path.Combine(temp.Path, "Local State"), "{}");
        Check((await new CursorUsageCollector(client, temp.Path).CollectAsync(now)).Count == 0 && requests == 4, "malformed Local State falls back without request");
        File.WriteAllText(Path.Combine(temp.Path, "Local State"), "not-json");
        Check((await new CursorUsageCollector(client, temp.Path).CollectAsync(now)).Count == 0 && requests == 4, "invalid Local State JSON falls back");
        File.WriteAllText(Path.Combine(temp.Path, "Local State"), localState);
        WriteToken(temp.Path, "cursor-safe-storage:v1:not-base64!");
        Check((await new CursorUsageCollector(client, temp.Path).CollectAsync(now)).Count == 0 && requests == 4, "invalid encrypted token falls back");
    }
    internal static Task IntegrationAsync()
    {
        using TempDirectory temp = new();
        DateTimeOffset now = new(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(8));
        string pricingPath = Path.Combine(temp.Path, "pricing.json");
        File.WriteAllText(pricingPath, "{\"grok-4.6\":{\"input\":2,\"cachedInput\":0.5,\"output\":6,\"aliases\":[\"cursor-grok-4.6-high\"]}}");
        string session = Path.Combine(temp.Path, "sessions", "workspace", "one");
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "updates.jsonl"), JsonSerializer.Serialize(new
        {
            timestamp = now.ToUnixTimeSeconds(), method = "_x.ai/session/update", @params = new { update = new
            {
                sessionUpdate = "turn_completed", prompt_id = "local", usage = new { modelUsage = new Dictionary<string, object>
                {
                    ["grok-4.6"] = new { inputTokens = 100, cachedReadTokens = 20, outputTokens = 200, apiDurationMs = 10000, costUsdTicks = 1000000000L },
                } },
            } },
        }));
        CursorUsageEvent[] cloud =
        [
            new(now.AddHours(-1), "grok-4.6", 400, 40, 80),
            new(now.AddHours(-1), "cursor-grok-4.6-high", 200, 20, 120),
            new(now.AddHours(-1), "unpriced", 30, 0, 0),
            new(now.AddDays(-2), "other", 20, 0, 10),
            new(now.AddDays(-40), "old", 30, 0, 0),
            new(now.AddDays(1), "future", 1000, 0, 0),
        ];
        TokenCostStatistics stats = new TokenCostCollector(pricingPath).CollectGrok(temp.Path, now, cursorUsage: cloud);
        Check(stats.Today.TotalTokens == 930 && stats.Today.CostUsd == 0.10271m && stats.Today.HasUnpricedUsage &&
              stats.Today.CacheReadTokens == 80 && stats.Today.CacheableInputTokens == 300, "local and estimated Cursor totals retain unknown usage");
        Check(stats.LastTwentyFourHours.TotalTokens == 930 && stats.LastSevenDays.TotalTokens == 950 && stats.Lifetime.TotalTokens == 980, "rolling periods and future exclusion");
        Check(stats.CurrentMonth.TotalTokens == 950 && stats.CurrentMonthDaily.Single(day => day.Date == now.LocalDateTime.Date).Summary.TotalTokens == 930 &&
              stats.LastThirtyDaysDaily.Single(day => day.Date == now.AddDays(-2).LocalDateTime.Date).Summary.TotalTokens == 20, "calendar periods and daily totals");
        Check(stats.SessionLogs.Count == 1 && stats.SessionLogs[0].Model == "grok-4.6" && stats.Today.GetOutputTokensPerSecond() == 20m, "local-only session logs and speed");
        TokenCostModelStatistics sameModel = stats.Models.Single(model => model.Model == "grok-4.6*");
        TokenCostModelStatistics alias = stats.Models.Single(model => model.Model == "cursor-grok-4.6-high*");
        Check(stats.Models.Single(model => model.Model == "grok-4.6").Today.TotalTokens == 300 && sameModel.Today.TotalTokens == 400 &&
              sameModel.Today.CostUsd == 0.00202m && sameModel.Today.GetOutputTokensPerSecond() == null, "Cursor pricing uses uncached input, cache read and output separately");
        Check(alias.Today.CostUsd == 0.00069m && !alias.Today.HasUnpricedUsage, "Cursor alias resolves local model pricing");
        Check(stats.Models.Single(model => model.Model == "unpriced*").Today is { TotalTokens: 30, CostUsd: 0m, HasUnpricedUsage: true }, "unpriced Cursor model keeps tokens and marks partial cost");
        TokenCostStatistics publishedPricing = new TokenCostCollector().CollectGrok(temp.Path, now, cursorUsage: [cloud[1]]);
        Check(publishedPricing.Models.Single(model => model.Model == "cursor-grok-4.6-high*").Today.CostUsd == 0.00069m, "published pricing contains the Cursor model alias");
        TokenCostCollector publishedCollector = new();
        foreach (string version in new[] { "4.5", "4.6", "4.7" })
        {
            string[] efforts = version == "4.5" ? ["", "-low", "-medium", "-high"] : ["", "-low", "-medium", "-high", "-xhigh"];
            string[] prefixes = version switch { "4.5" => ["", "cursor-"], "4.6" => ["cursor-"], _ => [""] };
            string[] variants = (from prefix in prefixes
                                from effort in efforts
                                from speed in new[] { "", "-fast" }
                                select $"{prefix}grok-{version}{effort}{speed}").ToArray();
            foreach (string model in variants)
            {
                TokenCostStatistics pricing = publishedCollector.CollectGrok(temp.Path, now, cursorUsage: [cloud[1] with { Model = model }]);
                TokenCostSummary summary = pricing.Models.Single(item => item.Model == model + "*").Today;
                decimal expected = model.EndsWith("-fast", StringComparison.Ordinal)
                    ? (version == "4.5" ? 0.00186m : 0.00138m)
                    : (version == "4.5" ? 0.000686m : 0.00069m);
                Check(summary.CostUsd == expected && !summary.HasUnpricedUsage, $"{model} uses the corresponding standard or Fast price");
            }
        }
        TrayPopupViewModel vm = new(new AppSettings(), () => Task.CompletedTask);
        vm.UpdateGrokTokenCost(stats);
        Check(vm.GrokTokenCost.DonutSegments.Any(segment => segment.Label == "Grok-4.6") && vm.GrokTokenCost.DonutSegments.Any(segment => segment.Label == "Grok-4.6-Cursor"),
              "local and Cursor model segments stay separate");
        Check(vm.GrokTokenCost.DonutSegments.Single(segment => segment.Label == "Grok-4.6-Cursor").Share == "65%", "Cursor model aliases merge into one segment");
        TokenCostStatistics fastDisplay = new TokenCostCollector(pricingPath).CollectGrok(temp.Path, now, cursorUsage:
            [cloud[0], cloud[1], cloud[1] with { Model = "cursor-grok-4.6-high-fast" }, cloud[1] with { Model = "cursor-grok-4.6-fast" }, cloud[2]]);
        vm.UpdateGrokTokenCost(fastDisplay);
        Check(vm.GrokTokenCost.DonutSegments.Count(segment => segment.Label == "Grok-4.6-Cursor") == 1 &&
              vm.GrokTokenCost.DonutSegments.Any(segment => segment.Label == "Grok-4.6") && vm.GrokTokenCost.DonutSegments.Any(segment => segment.Label == "Unpriced*"),
              "Cursor Grok prefix, effort and fast variants merge while other cloud markers remain");
        TokenCostStatistics mixedSpeeds = publishedCollector.CollectGrok(temp.Path, now, cursorUsage:
            [new(now, "cursor-grok-4.6-high", 2_000_000, 250_000, 1_000_000), new(now, "cursor-grok-4.6-high-fast", 2_000_000, 250_000, 1_000_000)]);
        Check(mixedSpeeds.Models.Single(model => model.Model == "cursor-grok-4.6-high*").Today.CostUsd == 7.625m &&
              mixedSpeeds.Models.Single(model => model.Model == "cursor-grok-4.6-high-fast*").Today.CostUsd == 15.25m, "Fast and standard costs are calculated separately");
        vm.UpdateGrokTokenCost(mixedSpeeds);
        TokenCostDonutSegment mergedSpeedSegment = vm.GrokTokenCost.DonutSegments.Single(segment => segment.Label == "Grok-4.6-Cursor");
        Check(mergedSpeedSegment.Tooltip.Contains("Tokens: 4.00M") && mergedSpeedSegment.Tooltip.Contains("Cost: $22.88"),
              "one Cursor segment sums tokens and independently calculated standard and Fast costs");
        CursorUsageEvent api = new(now, "grok-4.7", 2_000_000, 250_000, 1_000_000) { Product = "grok-api" };
        TokenCostStatistics apiStats = publishedCollector.CollectGrok(temp.Path, now, cursorUsage:
            [api, api with { Model = "grok-4.7-high" }, api with { Model = "grok-4.7-xhigh-fast" }, api with { Product = "cursor" }]);
        Check(apiStats.Today.TotalTokens == 8_000_300 && apiStats.Today.CostUsd == 38.225m && !apiStats.Today.HasUnpricedUsage &&
              apiStats.Today.CacheReadTokens == 1_000_020 && apiStats.Today.CacheableInputTokens == 4_000_100,
              "Grok API uses current model prices and joins period totals with Cursor and local usage");
        Check(apiStats.SessionLogs.Count == 1 && apiStats.Today.GetOutputTokensPerSecond() == 20m &&
              apiStats.Models.Single(model => model.Model == "grok-4.7-api*").Today.GetOutputTokensPerSecond() == null,
              "Grok API keeps cloud usage out of local logs and speed");
        vm.UpdateGrokTokenCost(apiStats);
        TokenCostDonutSegment apiSegment = vm.GrokTokenCost.DonutSegments.Single(segment => segment.Label == "Grok-4.7-API");
        Check(apiSegment.Tooltip.Contains("Tokens: 6.00M") && apiSegment.Tooltip.Contains("Cost: $30.50") &&
              vm.GrokTokenCost.DonutSegments.Any(segment => segment.Label == "Grok-4.7-Cursor") &&
              vm.GrokTokenCost.DonutSegments.Any(segment => segment.Label == "Grok-4.6"),
              "API effort and Fast variants merge by version while Cursor and local sources stay separate");
        TokenCostStatistics unpricedApi = publishedCollector.CollectGrok(temp.Path, now, cursorUsage: [api with { Model = "grok-4.99" }]);
        vm.UpdateGrokTokenCost(unpricedApi);
        Check(unpricedApi.Today.HasUnpricedUsage && vm.GrokTokenCost.DonutSegments.Single(segment => segment.Label == "Grok-4.99-API").Tooltip.Contains("Partial cost"),
              "unknown API prices retain tokens and mark partial costs");
        CursorUsageEvent[] more = [.. cloud, new(now, "alpha", 50, 0, 0), new(now, "beta", 40, 0, 0), new(now, "gamma", 30, 0, 0)];
        vm.UpdateGrokTokenCost(new TokenCostCollector(pricingPath).CollectGrok(temp.Path, now, cursorUsage: more));
        Check(vm.GrokTokenCost.DonutSegments.Count == 4 && vm.GrokTokenCost.DonutSegments.Any(segment => segment.Label == "Other"), "existing Other grouping remains intact");
        return Task.CompletedTask;
    }
    private static string Token(string account) => "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = "auth0|" + account })))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    private static object Row(string id, DateTimeOffset time, string model, long input, long output, long read, long write, string product = "cursor", bool hasMeters = true, long? total = null) => new
    {
        billingItemId = id, eventTime = time.ToString("O"), product, hasTokenMeters = hasMeters, totalTokens = total ?? input + output + read + write,
        usage = new Dictionary<string, long> { ["prompt-text-tokens"] = input, ["completion-text-tokens"] = output, ["cached-prompt-text-tokens"] = read, ["cache-write-text-tokens"] = write },
        displayExtras = new { model }, pricing = new { total = 0 },
    };

    private static object ApiRow(string id, DateTimeOffset time, string total = "100", bool images = false)
    {
        Dictionary<string, string> usage = new()
        {
            ["Prompt text tokens"] = "10", ["Completion text tokens"] = "20", ["Cached prompt text tokens"] = "30", ["Reasoning text tokens"] = "40",
        };
        if (images)
        {
            usage["Prompt image tokens"] = "50";
        }
        return new
        {
            billingItemId = id, eventTime = time.ToString("O"), product = "grok-api", hasTokenMeters = true, totalTokens = total, usage,
            displayExtras = new { model = "grok-4.7" },
        };
    }

    private static string Page(bool hasMore, string? cursor, params object[] rows) => hasMore || cursor != null
        ? JsonSerializer.Serialize(new { data = rows, pagination = new { hasMore, nextCursor = cursor } })
        : JsonSerializer.Serialize(new { data = rows, pagination = new { hasMore } });
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static void WriteToken(string root, string token)
    {
        string directory = Path.Combine(root, "User", "globalStorage");
        Directory.CreateDirectory(directory);
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "state.vscdb"), Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS ItemTable (key TEXT PRIMARY KEY, value TEXT); INSERT OR REPLACE INTO ItemTable (key, value) VALUES ('cursorAuth/accessToken', $value)";
        command.Parameters.AddWithValue("$value", token);
        command.ExecuteNonQuery();
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
