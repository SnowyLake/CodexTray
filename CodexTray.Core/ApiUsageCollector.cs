using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexTray.Core;

public sealed record ApiUsageResult(
    string MonitorId,
    bool Available,
    string BalanceDisplay,
    string UsedDisplay,
    string Error,
    DateTimeOffset UpdatedAt,
    string BalanceTooltip = "",
    string Provider = "");

public enum ApiUsageRefreshStatus
{
    AllAvailable,
    PartiallyAvailable,
    Unavailable,
}

public sealed record ApiUsageSummary(
    ApiUsageRefreshStatus Status,
    int ErrorCount,
    DateTimeOffset? LatestAvailableUpdatedAt);

public sealed class ApiUsageCollector
{
    private static readonly HttpClient s_HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private readonly HttpClient m_HttpClient;
    private readonly GrokUsageCollector m_GrokUsageCollector;

    /// <summary>
    /// Creates an API usage collector with the shared HTTP client.
    /// </summary>
    public ApiUsageCollector()
        : this(s_HttpClient)
    {
    }

    /// <summary>
    /// Creates an API usage collector with a custom HTTP client.
    /// </summary>
    public ApiUsageCollector(HttpClient httpClient)
    {
        m_HttpClient = httpClient;
        m_GrokUsageCollector = new GrokUsageCollector(httpClient);
    }

    /// <summary>
    /// Summarizes the availability of one completed API usage refresh.
    /// </summary>
    public static ApiUsageSummary Summarize(IReadOnlyList<ApiUsageResult> results)
    {
        if (results.Count == 0)
        {
            throw new ArgumentException("At least one API usage result is required.", nameof(results));
        }

        int availableCount = 0;
        int errorCount = 0;
        DateTimeOffset? latestAvailableUpdatedAt = null;
        foreach (ApiUsageResult result in results)
        {
            if (!result.Available)
            {
                errorCount++;
                continue;
            }

            availableCount++;
            if (latestAvailableUpdatedAt == null || result.UpdatedAt > latestAvailableUpdatedAt)
            {
                latestAvailableUpdatedAt = result.UpdatedAt;
            }
        }

        ApiUsageRefreshStatus status = errorCount == 0
            ? ApiUsageRefreshStatus.AllAvailable
            : availableCount == 0
                ? ApiUsageRefreshStatus.Unavailable
                : ApiUsageRefreshStatus.PartiallyAvailable;
        return new ApiUsageSummary(status, errorCount, latestAvailableUpdatedAt);
    }

    /// <summary>
    /// Queries every configured API monitor in parallel.
    /// </summary>
    public async Task<IReadOnlyList<ApiUsageResult>> CollectAsync(
        IEnumerable<ApiMonitorSettings> monitors,
        bool useAbsoluteResetTime = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<ApiUsageResult>[] queries = monitors.Select(async monitor =>
            (await CollectOneAsync(monitor, useAbsoluteResetTime, cancellationToken).ConfigureAwait(false)) with { Provider = monitor.Provider }).ToArray();
        return await Task.WhenAll(queries).ConfigureAwait(false);
    }

    /// <summary>
    /// Queries one supported API monitor.
    /// </summary>
    private async Task<ApiUsageResult> CollectOneAsync(ApiMonitorSettings monitor, bool useAbsoluteResetTime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = DateTimeOffset.Now;
        if (monitor.Provider == ApiMonitorSettings.GrokProvider)
        {
            return await CollectGrokAsync(monitor, now, useAbsoluteResetTime, cancellationToken).ConfigureAwait(false);
        }

        if (monitor.ApiKey.Length == 0)
        {
            return Unavailable(monitor.Id, "Enter an API key", now);
        }

        if (monitor.Provider == ApiMonitorSettings.NanoGptProvider)
        {
            return await CollectNanoGptAsync(monitor, now, cancellationToken).ConfigureAwait(false);
        }

        string endpointPath = monitor.Provider switch
        {
            ApiMonitorSettings.NewApiProvider => "/api/user/self",
            ApiMonitorSettings.OpenRouterProvider => "/api/v1/credits",
            _ => "/user/balance",
        };
        if (!TryBuildUri(monitor.BaseUrl, endpointPath, out Uri? uri, monitor.Provider == ApiMonitorSettings.OpenRouterProvider))
        {
            return Unavailable(monitor.Id, "Enter a valid HTTP or HTTPS base URL", now);
        }

        if (monitor.Provider == ApiMonitorSettings.NewApiProvider && monitor.UserId.Length == 0)
        {
            return Unavailable(monitor.Id, "Enter a NewAPI user ID", now);
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", monitor.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (monitor.Provider == ApiMonitorSettings.NewApiProvider)
            {
                request.Headers.TryAddWithoutValidation("New-Api-User", monitor.UserId);
            }

            using HttpResponseMessage response = await m_HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable(monitor.Id, $"Request failed: {(int)response.StatusCode} {response.ReasonPhrase}", now);
            }

            using JsonDocument document = JsonDocument.Parse(body);
            return monitor.Provider switch
            {
                ApiMonitorSettings.NewApiProvider => ParseNewApi(monitor.Id, document.RootElement, now),
                ApiMonitorSettings.OpenRouterProvider => ParseOpenRouter(monitor.Id, document.RootElement, now),
                _ => ParseDeepSeek(monitor.Id, document.RootElement, now),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            return Unavailable(monitor.Id, exception is TaskCanceledException ? "Request timed out" : "Invalid API response", now);
        }
    }

    /// <summary>
    /// Queries NanoGPT balance and optional 30-day usage data.
    /// </summary>
    private async Task<ApiUsageResult> CollectNanoGptAsync(ApiMonitorSettings monitor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!TryBuildUri(monitor.BaseUrl, "/api/check-balance", out Uri? balanceUri, stripApiV1: true) ||
            !TryBuildUri(monitor.BaseUrl, "/api/v1/usage", out Uri? usageUri, stripApiV1: true))
        {
            return Unavailable(monitor.Id, "Enter a valid HTTP or HTTPS base URL", now);
        }

        try
        {
            using HttpRequestMessage balanceRequest = new(HttpMethod.Post, balanceUri);
            balanceRequest.Headers.TryAddWithoutValidation("X-API-Key", monitor.ApiKey);
            balanceRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage balanceResponse = await m_HttpClient.SendAsync(balanceRequest, cancellationToken).ConfigureAwait(false);
            string balanceBody = await balanceResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!balanceResponse.IsSuccessStatusCode)
            {
                return Unavailable(monitor.Id, $"Request failed: {(int)balanceResponse.StatusCode} {balanceResponse.ReasonPhrase}", now);
            }

            using JsonDocument balanceDocument = JsonDocument.Parse(balanceBody);
            if (!TryGetDecimal(balanceDocument.RootElement, "usd_balance", out decimal balance))
            {
                return Unavailable(monitor.Id, "Balance data is missing", now);
            }

            string usedDisplay = string.Empty;
            try
            {
                using HttpRequestMessage usageRequest = new(HttpMethod.Get, usageUri);
                usageRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", monitor.ApiKey);
                usageRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using HttpResponseMessage usageResponse = await m_HttpClient.SendAsync(usageRequest, cancellationToken).ConfigureAwait(false);
                string usageBody = await usageResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (usageResponse.IsSuccessStatusCode)
                {
                    using JsonDocument usageDocument = JsonDocument.Parse(usageBody);
                    if (usageDocument.RootElement.TryGetProperty("totals", out JsonElement totals) &&
                        totals.ValueKind == JsonValueKind.Object &&
                        TryGetDecimal(totals, "netCostUsd", out decimal used))
                    {
                        usedDisplay = $"${used:0.00}";
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
            {
                usedDisplay = string.Empty;
            }

            return new ApiUsageResult(monitor.Id, true, $"${balance:0.00}", usedDisplay, string.Empty, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            return Unavailable(monitor.Id, exception is TaskCanceledException ? "Request timed out" : "Invalid API response", now);
        }
    }

    /// <summary>
    /// Queries Grok Build billing with the selected locally stored OAuth session.
    /// </summary>
    private async Task<ApiUsageResult> CollectGrokAsync(ApiMonitorSettings monitor, DateTimeOffset now, bool useAbsoluteResetTime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            GrokUsageSnapshot snapshot = await m_GrokUsageCollector
                .CollectAsync(monitor.GrokOAuthSource, cancellationToken)
                .ConfigureAwait(false);
            return new ApiUsageResult(
                monitor.Id,
                true,
                FormatRemainingPercent(snapshot.UsedPercent),
                useAbsoluteResetTime
                    ? CodexTrayCollector.FormatSevenDayResetDate(snapshot.ResetsAt, now)
                    : CodexTrayCollector.FormatSevenDayResetLabel(snapshot.ResetsAt, now),
                string.Empty,
                now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or OverflowException)
        {
            return Unavailable(monitor.Id, exception is TaskCanceledException ? "Request timed out" : exception.Message, now);
        }
    }

    /// <summary>
    /// Parses a DeepSeek balance response.
    /// </summary>
    private static ApiUsageResult ParseDeepSeek(string monitorId, JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetProperty("balance_infos", out JsonElement balances) || balances.ValueKind != JsonValueKind.Array)
        {
            return Unavailable(monitorId, "Balance data is missing", now);
        }

        foreach (JsonElement balance in balances.EnumerateArray())
        {
            string currency = balance.TryGetProperty("currency", out JsonElement currencyElement)
                ? currencyElement.GetString() ?? string.Empty
                : string.Empty;
            if (!string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rawValue = balance.TryGetProperty("total_balance", out JsonElement valueElement)
                ? valueElement.GetString() ?? string.Empty
                : string.Empty;
            if (decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
            {
                return new ApiUsageResult(monitorId, true, $"¥{value:0.00}", string.Empty, string.Empty, now);
            }
        }

        return Unavailable(monitorId, "CNY balance data is missing", now);
    }

    /// <summary>
    /// Formats a remaining allowance percentage for compact card display.
    /// </summary>
    private static string FormatRemainingPercent(double usedPercent)
    {
        int remainingPercent = (int)Math.Round(Math.Clamp(100 - usedPercent, 0, 100), MidpointRounding.AwayFromZero);
        return $"{remainingPercent.ToString(CultureInfo.InvariantCulture)}%";
    }

    /// <summary>
    /// Parses a NewAPI account quota response using its 500000 units per USD convention.
    /// </summary>
    private static ApiUsageResult ParseNewApi(string monitorId, JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetProperty("success", out JsonElement success) || success.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object ||
            !TryGetDecimal(data, "quota", out decimal quota) || !TryGetDecimal(data, "used_quota", out decimal usedQuota))
        {
            string message = root.TryGetProperty("message", out JsonElement messageElement)
                ? messageElement.GetString() ?? "Quota data is missing"
                : "Quota data is missing";
            return Unavailable(monitorId, message, now);
        }

        return new ApiUsageResult(
            monitorId,
            true,
            $"${quota / 500000m:0.00}",
            $"${usedQuota / 500000m:0.00}",
            string.Empty,
            now);
    }

    /// <summary>
    /// Parses OpenRouter management credit totals.
    /// </summary>
    private static ApiUsageResult ParseOpenRouter(string monitorId, JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object ||
            !TryGetDecimal(data, "total_credits", out decimal totalCredits) ||
            !TryGetDecimal(data, "total_usage", out decimal totalUsage))
        {
            return Unavailable(monitorId, "Credit data is missing", now);
        }

        decimal remaining = Math.Max(0, totalCredits - totalUsage);
        return new ApiUsageResult(monitorId, true, $"${remaining:0.00}", $"${totalUsage:0.00}", string.Empty, now);
    }

    /// <summary>
    /// Reads a JSON number that may be encoded as a number or string.
    /// </summary>
    private static bool TryGetDecimal(JsonElement parent, string propertyName, out decimal value)
    {
        value = 0;
        if (!parent.TryGetProperty(propertyName, out JsonElement element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDecimal(out value);
        }

        return element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Builds an account endpoint from a user-provided base URL.
    /// </summary>
    private static bool TryBuildUri(string baseUrl, string endpointPath, out Uri? uri, bool stripApiV1 = false)
    {
        uri = null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        string path = baseUri.AbsolutePath.TrimEnd('/');
        if (stripApiV1 && path.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^7];
        }
        else if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
        }

        UriBuilder builder = new(baseUri)
        {
            Path = $"{path}{endpointPath}",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        uri = builder.Uri;
        return true;
    }

    /// <summary>
    /// Creates a failed API usage result for display.
    /// </summary>
    private static ApiUsageResult Unavailable(string monitorId, string error, DateTimeOffset now)
    {
        return new ApiUsageResult(monitorId, false, "N/A", "N/A", error, now);
    }
}
