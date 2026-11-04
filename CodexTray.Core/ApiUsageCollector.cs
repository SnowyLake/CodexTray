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
    string Provider = "",
    string UsedError = "");

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
    private readonly Func<DateTimeOffset> m_Clock;

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
    public ApiUsageCollector(HttpClient httpClient, Func<DateTimeOffset>? clock = null)
    {
        m_HttpClient = httpClient;
        m_Clock = clock ?? (() => DateTimeOffset.Now);
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
            if (result.UsedError.Length > 0)
            {
                errorCount++;
            }
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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<ApiUsageResult>[] queries = monitors.Select(async monitor =>
            (await CollectOneAsync(monitor, cancellationToken).ConfigureAwait(false)) with { Provider = monitor.Provider }).ToArray();
        return await Task.WhenAll(queries).ConfigureAwait(false);
    }

    /// <summary>
    /// Converts the first DeepSeek card's CNY balance into a rounded whole-yuan plugin snapshot.
    /// </summary>
    public static DeepSeekPluginUsage BuildDeepSeekPluginUsage(IReadOnlyList<ApiUsageResult> results)
    {
        foreach (ApiUsageResult result in results)
        {
            if (!string.Equals(result.Provider, ApiMonitorSettings.DeepSeekProvider, StringComparison.Ordinal))
            {
                continue;
            }

            if (result.Available)
            {
                return new DeepSeekPluginUsage(
                    new UsageLimit { Name = "deepseek", RemainingPercent = 100 },
                    FormatDeepSeekPluginDisplay(result.BalanceDisplay));
            }

            break;
        }

        return new DeepSeekPluginUsage(
            new UsageLimit { Name = "deepseek", UsedPercent = 100, RemainingPercent = 0 },
            CodexTrayDefaults.UnavailableDisplay);
    }

    /// <summary>
    /// Rounds a DeepSeek CNY card balance to a whole-yuan plugin display.
    /// </summary>
    private static string FormatDeepSeekPluginDisplay(string balanceDisplay)
    {
        if (balanceDisplay.StartsWith("¥", StringComparison.Ordinal) &&
            decimal.TryParse(balanceDisplay.AsSpan(1), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
        {
            return string.Create(CultureInfo.InvariantCulture, $"¥{decimal.Round(value, 0, MidpointRounding.AwayFromZero):0}");
        }

        return CodexTrayDefaults.UnavailableDisplay;
    }

    /// <summary>
    /// Queries one supported API monitor.
    /// </summary>
    private async Task<ApiUsageResult> CollectOneAsync(ApiMonitorSettings monitor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = DateTimeOffset.Now;
        if (monitor.ApiKey.Length == 0)
        {
            return Unavailable(monitor.Id, monitor.Provider == ApiMonitorSettings.OpenAiProvider ? "Enter an Admin API key" : "Enter an API key", now);
        }

        if (monitor.Provider == ApiMonitorSettings.OpenAiProvider)
        {
            return await CollectOpenAiAsync(monitor, now, cancellationToken).ConfigureAwait(false);
        }

        string endpointPath = monitor.Provider switch
        {
            ApiMonitorSettings.NewApiProvider => "/api/user/self",
            ApiMonitorSettings.OpenRouterProvider => "/api/v1/credits",
            ApiMonitorSettings.VercelProvider => "/v1/credits",
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

            using HttpResponseMessage response = await UsageHttp.SendAsync(m_HttpClient, request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable(monitor.Id, UsageDiagnostics.HttpError(response.StatusCode), now);
            }

            using JsonDocument document = JsonDocument.Parse(body);
            return monitor.Provider switch
            {
                ApiMonitorSettings.NewApiProvider => ParseNewApi(monitor.Id, document.RootElement, now),
                ApiMonitorSettings.OpenRouterProvider => ParseOpenRouter(monitor.Id, document.RootElement, now),
                ApiMonitorSettings.VercelProvider => ParseVercel(monitor.Id, document.RootElement, now),
                _ => ParseDeepSeek(monitor.Id, document.RootElement, now),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            return Unavailable(monitor.Id, UsageDiagnostics.Error(exception), now);
        }
    }

    /// <summary>
    /// Queries OpenAI organization costs for the current UTC month and UTC day.
    /// </summary>
    private async Task<ApiUsageResult> CollectOpenAiAsync(ApiMonitorSettings monitor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateTimeOffset utcNow = m_Clock().ToUniversalTime();
        long monthStart = new DateTimeOffset(utcNow.Year, utcNow.Month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        long todayStart = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        long rangeEnd = todayStart + 86_400;
        int limit = utcNow.Day;
        if (!TryBuildUri(monitor.BaseUrl, "/v1/organization/costs", out Uri? uri))
        {
            return Unavailable(monitor.Id, "Enter a valid HTTP or HTTPS base URL", now);
        }

        decimal monthCost = 0;
        decimal todayCost = 0;
        string? page = null;
        try
        {
            for (int pageIndex = 0; pageIndex < 7; pageIndex++)
            {
                using HttpRequestMessage request = new(HttpMethod.Get, BuildOpenAiCostsUri(uri!, monthStart, rangeEnd, limit, page));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", monitor.ApiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using HttpResponseMessage response = await UsageHttp.SendAsync(m_HttpClient, request, cancellationToken).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return Unavailable(monitor.Id, UsageDiagnostics.HttpError(response.StatusCode), now);
                }

                using JsonDocument document = JsonDocument.Parse(body);
                if (!TryAddOpenAiCosts(document.RootElement, monthStart, todayStart, rangeEnd, ref monthCost, ref todayCost, out string? nextPage, out bool hasMore))
                {
                    return Unavailable(monitor.Id, "Cost data is missing", now);
                }

                if (!hasMore)
                {
                    return new ApiUsageResult(monitor.Id, true, FormatUsd(monthCost), FormatUsd(todayCost), string.Empty, now);
                }

                if (string.IsNullOrEmpty(nextPage))
                {
                    break;
                }

                page = nextPage;
            }

            return Unavailable(monitor.Id, "Cost data is incomplete", now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            return Unavailable(monitor.Id, UsageDiagnostics.Error(exception), now);
        }
    }

    /// <summary>
    /// Adds USD costs from one organization costs page. Non-USD amounts fail the page.
    /// </summary>
    private static bool TryAddOpenAiCosts(
        JsonElement root,
        long monthStart,
        long todayStart,
        long rangeEnd,
        ref decimal monthCost,
        ref decimal todayCost,
        out string? nextPage,
        out bool hasMore)
    {
        nextPage = null;
        hasMore = false;
        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement bucket in data.EnumerateArray())
        {
            if (!TryGetDecimal(bucket, "start_time", out decimal startValue) || startValue != decimal.Truncate(startValue))
            {
                return false;
            }

            long start = (long)startValue;
            if (start < monthStart || start >= rangeEnd)
            {
                continue;
            }

            if (!bucket.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            decimal bucketCost = 0;
            foreach (JsonElement result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("amount", out JsonElement amount) || amount.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string currency = amount.TryGetProperty("currency", out JsonElement currencyElement) && currencyElement.ValueKind == JsonValueKind.String
                    ? currencyElement.GetString() ?? string.Empty
                    : string.Empty;
                if (currency.Length > 0 && !string.Equals(currency, "usd", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (TryGetDecimal(amount, "value", out decimal value))
                {
                    bucketCost += value;
                }
            }

            monthCost += bucketCost;
            if (start == todayStart)
            {
                todayCost += bucketCost;
            }
        }

        hasMore = root.TryGetProperty("has_more", out JsonElement hasMoreElement) && hasMoreElement.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("next_page", out JsonElement nextPageElement) && nextPageElement.ValueKind == JsonValueKind.String)
        {
            nextPage = nextPageElement.GetString();
        }

        return true;
    }

    /// <summary>
    /// Builds one daily organization costs request, including a pagination cursor when present.
    /// </summary>
    private static Uri BuildOpenAiCostsUri(Uri endpoint, long startTime, long endTime, int limit, string? page)
    {
        string query = $"start_time={startTime.ToString(CultureInfo.InvariantCulture)}&end_time={endTime.ToString(CultureInfo.InvariantCulture)}&bucket_width=1d&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrEmpty(page))
        {
            query += "&page=" + Uri.EscapeDataString(page);
        }

        return new UriBuilder(endpoint) { Query = query }.Uri;
    }

    /// <summary>
    /// Formats a USD amount without using the current culture.
    /// </summary>
    private static string FormatUsd(decimal value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"${value:0.00}");
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
                return new ApiUsageResult(monitorId, true, string.Create(CultureInfo.InvariantCulture, $"¥{value:0.00}"), string.Empty, string.Empty, now);
            }
        }

        return Unavailable(monitorId, "CNY balance data is missing", now);
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
            return Unavailable(monitorId, "Quota data is missing or request was rejected", now);
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
    /// Parses Vercel AI Gateway credit totals.
    /// </summary>
    private static ApiUsageResult ParseVercel(string monitorId, JsonElement root, DateTimeOffset now)
    {
        if (!TryGetDecimal(root, "balance", out decimal balance) ||
            !TryGetDecimal(root, "total_used", out decimal totalUsed))
        {
            return Unavailable(monitorId, "Credit data is missing", now);
        }

        return new ApiUsageResult(monitorId, true, $"${balance:0.00}", $"${totalUsed:0.00}", string.Empty, now);
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
