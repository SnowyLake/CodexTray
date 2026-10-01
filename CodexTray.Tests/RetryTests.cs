using CodexTray.App;
using CodexTray.Core;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace CodexTray.Tests;

internal static class RetryTests
{
    /// <summary>
    /// Verifies bounded attempts, fresh POST bodies, server wait times, and cancellation during backoff.
    /// </summary>
    internal static async Task HttpRetriesAsync()
    {
        List<TimeSpan> waits = [];
        List<HttpRequestMessage> requests = [];
        int count = 0;
        using HttpClient client = new(new Handler(async (request, token) =>
        {
            requests.Add(request);
            Check(await request.Content!.ReadAsStringAsync(token) == "fixed-body", "retry POST body");
            Check(request.Headers.Authorization?.Parameter == "test-key", "retry authorization");
            count++;
            if (count == 1)
            {
                throw new HttpRequestException("private credential should not be copied");
            }
            HttpResponseMessage response = new(count == 2 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            if (count == 2)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            }
            return response;
        }));
        using HttpRequestMessage original = new(HttpMethod.Post, "https://example.invalid/usage") { Content = new StringContent("fixed-body") };
        original.Headers.Authorization = new("Bearer", "test-key");
        using HttpResponseMessage success = await UsageHttp.SendAsync(client, original, default, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        Check(success.IsSuccessStatusCode && count == 3 && requests.Distinct().Count() == 3, "three fresh attempts");
        Check(waits.SequenceEqual([TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)]), "respect server wait");

        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest, HttpStatusCode.OK })
        {
            count = 0;
            using HttpClient permanent = new(new Handler((_, _) => { count++; return Task.FromResult(new HttpResponseMessage(status)); }));
            using HttpResponseMessage result = await UsageHttp.SendAsync(permanent, original, default, (_, _) => throw new InvalidOperationException("should not wait"));
            Check(count == 1, "permanent statuses are not retried");
        }

        count = 0;
        using HttpClient limited = new(new Handler((_, _) =>
        {
            count++;
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        }));
        using HttpResponseMessage terminal = await UsageHttp.SendAsync(limited, original, default, (_, _) => throw new InvalidOperationException("must not shorten server wait"));
        Check(count == 1 && terminal.StatusCode == HttpStatusCode.TooManyRequests, "long Retry-After stops automatic retry");

        foreach (bool timeout in new[] { false, true })
        {
            count = 0;
            waits.Clear();
            using HttpClient spent = new(new Handler((_, _) =>
            {
                count++;
                if (count > 1)
                {
                    return Task.FromException<HttpResponseMessage>(timeout ? new TaskCanceledException("private") : new HttpRequestException("private"));
                }

                HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
                return Task.FromResult(response);
            }));
            try
            {
                using HttpResponseMessage ignored = await UsageHttp.SendAsync(spent, original, default, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
                throw new InvalidOperationException("expected terminal network failure");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                Check(count == 2 && waits.SequenceEqual([TimeSpan.FromSeconds(10)]), "network and timeout cannot exceed the spent wait budget");
            }
        }

        count = 0;
        waits.Clear();
        using HttpClient dateLimited = new(new Handler((_, _) =>
        {
            count++;
            HttpResponseMessage response = new(count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            if (count == 1)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(4));
            }
            return Task.FromResult(response);
        }));
        using HttpResponseMessage dated = await UsageHttp.SendAsync(dateLimited, original, default, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        Check(count == 2 && waits.Single() > TimeSpan.FromSeconds(2) && waits.Single() <= TimeSpan.FromSeconds(4), "date Retry-After is respected");

        count = 0;
        waits.Clear();
        using HttpClient unavailable = new(new Handler((_, _) => { count++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }));
        using HttpResponseMessage exhausted = await UsageHttp.SendAsync(unavailable, original, default, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        Check(count == 3 && waits.Count == 2, "server retries are bounded");
        using CancellationTokenSource cancel = new();
        count = 0;
        try
        {
            using HttpResponseMessage ignored = await UsageHttp.SendAsync(unavailable, original, cancel.Token, (wait, token) =>
            {
                cancel.Cancel();
                return Task.Delay(wait, token);
            });
            throw new InvalidOperationException("expected cancellation");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Check(count == 1, "cancellation during backoff prevents another send");
        }
    }

    /// <summary>
    /// Verifies merging source/card retries and rejecting stale card configurations without clearing siblings.
    /// </summary>
    internal static async Task TargetsAndSnapshotsAsync()
    {
        RefreshRequests requests = new();
        requests.Add(PageItem.Codex);
        requests.Add(PageItem.Apis, "a");
        requests.Add(PageItem.Apis, "a");
        requests.Add(PageItem.Grok);
        RefreshRequest first = requests.Take()!;
        Check(first.Pages == (PageItem.Codex | PageItem.Apis | PageItem.Grok) && first.ApiIds.SequenceEqual(["a"]) && !first.AllApiCards, "merge independent retry targets");
        Check(requests.Take() == null, "batch is drained");
        requests.Add(PageItem.Apis, "b");
        requests.Add(PageItem.All);
        Check(requests.Take()!.AllApiCards, "full refresh subsumes card retries");

        ApiMonitorSettings a = new() { Id = "a", ApiKey = "a-key" };
        ApiMonitorSettings b = new() { Id = "b", ApiKey = "b-key" };
        DateTimeOffset now = DateTimeOffset.Now;
        ApiUsageResult Result(ApiMonitorSettings card, string balance) => new(card.Id, true, balance, "N/A", "", now, Provider: card.Provider);
        ApiUsageSnapshots cache = new();
        cache.Merge([a, b], [a, b], [Result(a, "¥10.00"), Result(b, "¥20.00")]);
        IReadOnlyList<ApiUsageResult> merged = cache.Merge([a, b], [b], [Result(b, "¥25.00")]);
        Check(merged[0].BalanceDisplay == "¥10.00" && merged[1].BalanceDisplay == "¥25.00", "one retry preserves sibling");
        Check(ApiUsageCollector.BuildDeepSeekPluginUsage(merged).Display == "¥10", "first DeepSeek stays mapped");
        merged = cache.Merge([b, a], [], []);
        Check(ApiUsageCollector.BuildDeepSeekPluginUsage(merged).Display == "¥25", "reorder immediately remaps first DeepSeek");
        ApiMonitorSettings edited = new() { Id = "b", ApiKey = "new-key" };
        merged = cache.Merge([edited, a], [b], [Result(b, "¥99.00")]);
        Check(!merged[0].Available && merged[1].Available, "late edited result is discarded");
        Check(ApiUsageCollector.BuildDeepSeekPluginUsage(merged).Display == "N/A", "unavailable first never falls back");
        merged = cache.Merge([a], [b], [Result(b, "¥99.00")]);
        Check(merged.Count == 1 && merged[0].MonitorId == "a", "late removed card stays removed");

        List<(PageItem, string?)> targets = [];
        TrayPopupViewModel vm = new(new AppSettings { ApiMonitors = [a, b] }, () => throw new InvalidOperationException("targeted retry must not call full refresh"),
            (page, id) => { targets.Add((page, id)); return Task.CompletedTask; });
        await vm.RetrySourceCommand.ExecuteAsync(PageItem.Cursor);
        await vm.RetryApiMonitorCommand.ExecuteAsync(vm.ApiMonitors[1]);
        Check(targets.SequenceEqual([(PageItem.Cursor, null), (PageItem.Apis, "b")]), "UI targets one source or card");
    }

    /// <summary>
    /// Verifies safe classifications and refusal to echo reason phrases, JSON messages, URLs, or credentials.
    /// </summary>
    internal static async Task DiagnosticsAsync()
    {
        const string secret = "PRIVATE_SECRET";
        using HttpClient client = new(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "reason.example"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { ReasonPhrase = secret }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"success\":false,\"message\":\"" + secret + "\"}") })));
        ApiUsageCollector collector = new(client);
        ApiMonitorSettings card = new() { Id = "card-secret", Provider = ApiMonitorSettings.NewApiProvider, BaseUrl = "https://message.example", ApiKey = secret, UserId = "user-secret", Name = secret };
        ApiMonitorSettings reason = new() { BaseUrl = "https://reason.example", ApiKey = secret };
        IReadOnlyList<ApiUsageResult> results = await collector.CollectAsync([card, reason]);
        Check(results.All(result => !result.Error.Contains(secret)), "untrusted HTTP and JSON text is not exposed");
        Check(UsageDiagnostics.Category(results[1].Error) == "Authentication", "authentication category");
        Check(UsageDiagnostics.Category(UsageDiagnostics.HttpError(HttpStatusCode.TooManyRequests)) == "Rate limit", "rate category");
        Check(UsageDiagnostics.Category(UsageDiagnostics.Error(new TaskCanceledException(secret))) == "Timeout", "timeout category");
        Check(UsageDiagnostics.Category(UsageDiagnostics.Error(new HttpRequestException(secret))) == "Network", "network category");
        TrayPopupViewModel vm = new(new AppSettings { ApiMonitors = [card] }, () => Task.CompletedTask);
        vm.ApiMonitors[0].Update(results[0] with { Error = secret + " HTTP 429" });
        string diagnostics = vm.BuildDiagnostics(vm.ApiMonitors[0]);
        Check(diagnostics.Contains("Rate limit") && !diagnostics.Contains(secret) && !diagnostics.Contains("message.example") && !diagnostics.Contains("user-secret") && !diagnostics.Contains("card-secret"), "copied diagnostics are redacted by construction");
        typeof(ApiMonitorViewModel).GetField("m_Provider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(vm.ApiMonitors[0], secret);
        Check(!vm.BuildDiagnostics(PageItem.Apis).Contains(secret), "whole API page uses the same provider whitelist");
        Check(vm.BuildDiagnostics(PageItem.Cursor).Contains("Not collected"), "initial diagnostics do not claim successful collection");
    }

    /// <summary>
    /// Verifies the gRPC fallback does not bypass a terminal server Retry-After instruction.
    /// </summary>
    internal static async Task GrokRateLimitAsync()
    {
        using TempDirectory temp = new();
        string? previous = Environment.GetEnvironmentVariable("GROK_HOME");
        string path = Path.Combine(temp.Path, "auth.json");
        File.WriteAllText(path, "{\"https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828\":{\"key\":\"fake-access\",\"user_id\":\"fake-user\"}}");
        Environment.SetEnvironmentVariable("GROK_HOME", temp.Path);
        try
        {
            foreach (HttpStatusCode status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
            {
                int sends = 0;
                using HttpClient client = new(new Handler((_, _) =>
                {
                    sends++;
                    HttpResponseMessage response = new(status);
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
                    return Task.FromResult(response);
                }));
                GrokUsageDashboard dashboard = await new GrokUsageCollector(client).CollectDashboardAsync();
                Check(sends == 1 && UsageDiagnostics.Category(dashboard.Error) == (status == HttpStatusCode.TooManyRequests ? "Rate limit" : "Service unavailable"),
                    "no gRPC or OAuth bypass on terminal HTTP failure");
                sends = 0;
                try
                {
                    await OAuthTokenHelpers.RefreshAsync(client, "https://auth.example/token", "fake-client", "fake-refresh", "Grok", "Sign in", default);
                    throw new InvalidOperationException("expected OAuth rejection");
                }
                catch (UsageHttpException)
                {
                    Check(sends == 1, "rotating OAuth POST is never automatically replayed");
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", previous);
        }
    }

    /// <summary>
    /// Fails one deterministic assertion.
    /// </summary>
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>
        /// Dispatches a synthetic HTTP response without accessing any provider.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
