using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTray.Core;

public sealed record CursorUsageSnapshot(
    string PlanType,
    double TotalUsedPercent,
    double AutoUsedPercent,
    double ApiUsedPercent,
    long ResetsAt);

public sealed record CursorUsageDashboard(
    CursorUsageSnapshot? Usage,
    TokenCostStatistics? TokenCost,
    string UsageError,
    string TokenCostError,
    DateTimeOffset UpdatedAt,
    CursorUsageEventsDiagnostics? TokenCostDiagnostics = null,
    bool InitialCredentialRefreshUsed = false,
    bool ForcedCredentialRefreshUsed = false);

public sealed record CursorUsageEventsDiagnostics(
    int EventCount,
    int PageCount,
    int TokenEventCount,
    int ZeroTokenMissingCostEventCount,
    int InputTokenFieldCount,
    int OutputTokenFieldCount,
    int CacheReadTokenFieldCount,
    int CacheWriteTokenFieldCount,
    TimeSpan Elapsed);

public sealed class CursorUsageCollector
{
    private const string k_UsageEndpoint = "https://cursor.com/api/usage-summary";
    private const string k_UsageEventsEndpoint = "https://cursor.com/api/dashboard/get-filtered-usage-events";
    private const string k_TokenEndpoint = "https://api2.cursor.sh/oauth/token";
    private const string k_ClientId = "KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB";
    private const string k_AccessTokenKey = "cursorAuth/accessToken";
    private const string k_RefreshTokenKey = "cursorAuth/refreshToken";
    private const string k_StateDbEnvName = "CODEXTRAY_CURSOR_STATE_VSCDB";
    private const int k_BusyTimeoutMilliseconds = 5_000;
    private const int k_SqliteRetryCount = 3;
    private const int k_UsageEventsPageSize = 1_000;
    private static readonly HttpClient s_HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    private static readonly TimeSpan s_RefreshBuffer = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim s_AuthLock = new(1, 1);

    private readonly HttpClient m_HttpClient;

    /// <summary>
    /// Creates a collector using the shared HTTP client.
    /// </summary>
    public CursorUsageCollector()
        : this(s_HttpClient)
    {
    }

    /// <summary>
    /// Creates a collector using the supplied HTTP client.
    /// </summary>
    public CursorUsageCollector(HttpClient httpClient)
    {
        m_HttpClient = httpClient;
    }

    /// <summary>
    /// Collects Cursor quota and token-cost dashboard data with one local credential load.
    /// </summary>
    public async Task<CursorUsageDashboard> CollectDashboardAsync(DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        DateTimeOffset refreshedAt = now ?? DateTimeOffset.Now;
        CursorCredentialResult initialCredential;
        try
        {
            initialCredential = await ResolveInitialCredentialAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or OverflowException)
        {
            string error = FormatError(exception);
            return new CursorUsageDashboard(null, null, error, error, refreshedAt);
        }

        CursorCredential credential = initialCredential.Credential;
        bool refreshUsed = false;
        CursorEndpointResult<CursorUsageSnapshot> usageResult = await CollectEndpointAsync(
            current => FetchUsageAsync(current, refreshedAt, cancellationToken),
            credential,
            refreshUsed,
            cancellationToken).ConfigureAwait(false);
        credential = usageResult.Credential;
        refreshUsed = usageResult.RefreshUsed;

        CursorEndpointResult<CursorUsageEventsCollection> tokenCostResult = await CollectEndpointAsync(
            current => FetchUsageEventsAsync(current, refreshedAt, cancellationToken),
            credential,
            refreshUsed,
            cancellationToken).ConfigureAwait(false);

        return new CursorUsageDashboard(
            usageResult.Value,
            tokenCostResult.Value?.Statistics,
            usageResult.Error,
            tokenCostResult.Error,
            refreshedAt,
            tokenCostResult.Value?.Diagnostics,
            initialCredential.RefreshUsed,
            usageResult.RefreshUsed || tokenCostResult.RefreshUsed);
    }

    /// <summary>
    /// Collects Cursor plan usage from the local IDE OAuth session.
    /// </summary>
    public async Task<CursorUsageSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        CursorCredential credential = (await ResolveInitialCredentialAsync(cancellationToken).ConfigureAwait(false)).Credential;
        try
        {
            return await FetchUsageAsync(credential, DateTimeOffset.Now, cancellationToken).ConfigureAwait(false);
        }
        catch (CursorRequestException exception) when (exception.IsAuthenticationFailure)
        {
            credential = await RefreshCredentialAsync(credential, cancellationToken).ConfigureAwait(false);
            return await FetchUsageAsync(credential, DateTimeOffset.Now, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses a Cursor usage-summary JSON body into remaining-percent inputs.
    /// </summary>
    public static CursorUsageSnapshot ParseUsageSummary(string json, DateTimeOffset now)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!TryGetPlan(root, out JsonElement plan) ||
            !TryGetPlanPercent(plan, "totalPercentUsed", out double totalUsedPercent) ||
            !TryGetPlanPercent(plan, "autoPercentUsed", out double autoUsedPercent) ||
            !TryGetPlanPercent(plan, "apiPercentUsed", out double apiUsedPercent))
        {
            throw new InvalidOperationException("Cursor usage-summary did not include plan usage percents.");
        }

        if (!TryGetBillingCycleEnd(root, out DateTimeOffset resetsAt) || resetsAt <= now)
        {
            throw new InvalidOperationException("Cursor usage-summary did not include a valid billing cycle end.");
        }

        return new CursorUsageSnapshot(
            GetStringProperty(root, "membershipType", "unknown"),
            totalUsedPercent,
            autoUsedPercent,
            apiUsedPercent,
            resetsAt.ToUnixTimeSeconds());
    }

    /// <summary>
    /// Loads local Cursor credentials and refreshes an expiring access token when needed.
    /// </summary>
    private async Task<CursorCredentialResult> ResolveInitialCredentialAsync(CancellationToken cancellationToken)
    {
        await s_AuthLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryLoadCredential(out CursorCredential credential, out string error))
            {
                throw new InvalidOperationException(error);
            }

            if (!NeedsRefresh(credential.AccessToken))
            {
                return new CursorCredentialResult(credential, false);
            }

            return new CursorCredentialResult(
                await RefreshCredentialAsync(credential, cancellationToken).ConfigureAwait(false),
                true);
        }
        finally
        {
            s_AuthLock.Release();
        }
    }

    /// <summary>
    /// Refreshes one already loaded Cursor credential without rereading the local database.
    /// </summary>
    private async Task<CursorCredential> RefreshCredentialAsync(CursorCredential credential, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.RefreshToken))
        {
            throw new InvalidOperationException("Cursor OAuth token expired or unauthorized and no refresh token is available. Sign in to Cursor again.");
        }

        OAuthTokenRefresh refresh = await RefreshOAuthTokenAsync(credential.RefreshToken, cancellationToken).ConfigureAwait(false);
        SaveCredential(credential.DbPath, refresh);
        if (!TryGetJwtSubject(refresh.AccessToken, out string userId))
        {
            throw new InvalidOperationException("Cursor OAuth access token did not include a user id.");
        }

        return new CursorCredential(credential.DbPath, refresh.AccessToken, refresh.RefreshToken, userId);
    }

    /// <summary>
    /// Executes one endpoint and spends at most one dashboard-wide forced refresh retry.
    /// </summary>
    private async Task<CursorEndpointResult<T>> CollectEndpointAsync<T>(
        Func<CursorCredential, Task<T>> collect,
        CursorCredential credential,
        bool refreshUsed,
        CancellationToken cancellationToken)
    {
        try
        {
            return new CursorEndpointResult<T>(await collect(credential).ConfigureAwait(false), string.Empty, credential, refreshUsed);
        }
        catch (CursorRequestException exception) when (exception.IsAuthenticationFailure && !refreshUsed)
        {
            CursorCredential refreshed = credential;
            try
            {
                refreshed = await RefreshCredentialAsync(credential, cancellationToken).ConfigureAwait(false);
                return new CursorEndpointResult<T>(await collect(refreshed).ConfigureAwait(false), string.Empty, refreshed, true);
            }
            catch (Exception retryException) when (retryException is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or OverflowException)
            {
                return new CursorEndpointResult<T>(default, FormatError(retryException), refreshed, true);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or OverflowException)
        {
            return new CursorEndpointResult<T>(default, FormatError(exception), credential, refreshUsed);
        }
    }

    /// <summary>
    /// Requests a new Cursor access token from the OAuth token endpoint.
    /// </summary>
    private async Task<OAuthTokenRefresh> RefreshOAuthTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, k_TokenEndpoint);
        request.Content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", k_ClientId),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
        ]);

        using HttpResponseMessage response = await m_HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Cursor OAuth refresh failed: HTTP {(int)response.StatusCode}. Sign in to Cursor again.");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("access_token", out JsonElement accessToken) ||
            accessToken.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(accessToken.GetString()))
        {
            throw new InvalidOperationException("Cursor OAuth refresh response did not include an access token.");
        }

        string nextRefresh = refreshToken;
        if (document.RootElement.TryGetProperty("refresh_token", out JsonElement refreshed) &&
            refreshed.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(refreshed.GetString()))
        {
            nextRefresh = refreshed.GetString()!;
        }

        return new OAuthTokenRefresh(accessToken.GetString()!, nextRefresh);
    }

    /// <summary>
    /// Sends the Cursor usage-summary request for one credential.
    /// </summary>
    private async Task<CursorUsageSnapshot> FetchUsageAsync(CursorCredential credential, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, k_UsageEndpoint);
        AddSessionHeaders(request, credential);
        request.Headers.UserAgent.ParseAdd("CodexTray");

        using HttpResponseMessage response = await m_HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "Cursor usage request failed");
        return ParseUsageSummary(body, now);
    }

    /// <summary>
    /// Fetches every Cursor usage-event page and aggregates complete token-cost periods.
    /// </summary>
    private async Task<CursorUsageEventsCollection> FetchUsageEventsAsync(CursorCredential credential, DateTimeOffset refreshedAt, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int expectedCount = -1;
        int displayedCount = 0;
        int zeroTokenMissingCostEventCount = 0;
        List<CursorUsageEvent> events = [];
        for (int page = 1; ; page++)
        {
            CursorUsageEventsPage response = await FetchUsageEventsPageAsync(credential, page, refreshedAt, cancellationToken).ConfigureAwait(false);
            if (expectedCount < 0)
            {
                expectedCount = response.TotalCount;
            }
            else if (response.TotalCount != expectedCount)
            {
                throw new InvalidOperationException("Cursor usage events changed while paging.");
            }

            if (response.DisplayCount > expectedCount - displayedCount)
            {
                throw new InvalidOperationException("Cursor usage events exceeded the reported total.");
            }

            events.AddRange(response.Events);
            displayedCount += response.DisplayCount;
            zeroTokenMissingCostEventCount += response.ZeroTokenMissingCostEventCount;
            if (displayedCount == expectedCount)
            {
                stopwatch.Stop();
                return new CursorUsageEventsCollection(
                    AggregateUsageEvents(events, refreshedAt),
                    new CursorUsageEventsDiagnostics(
                        expectedCount,
                        page,
                        events.Count,
                        zeroTokenMissingCostEventCount,
                        events.Count(usageEvent => usageEvent.HasInputTokens),
                        events.Count(usageEvent => usageEvent.HasOutputTokens),
                        events.Count(usageEvent => usageEvent.HasCacheReadTokens),
                        events.Count(usageEvent => usageEvent.HasCacheWriteTokens),
                        stopwatch.Elapsed));
            }

            if (response.DisplayCount == 0)
            {
                throw new InvalidOperationException("Cursor usage events ended before the reported total.");
            }
        }
    }

    /// <summary>
    /// Requests and validates one Cursor usage-events response page.
    /// </summary>
    private async Task<CursorUsageEventsPage> FetchUsageEventsPageAsync(CursorCredential credential, int page, DateTimeOffset refreshedAt, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new
        {
            page,
            pageSize = k_UsageEventsPageSize,
            startDate = "0",
            endDate = refreshedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        });
        using HttpRequestMessage request = new(HttpMethod.Post, k_UsageEventsEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        AddSessionHeaders(request, credential);
        request.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");

        using HttpResponseMessage response = await m_HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "Cursor usage-events request failed");
        return ParseUsageEventsPage(body);
    }

    /// <summary>
    /// Parses one usage-events response page without returning malformed partial data.
    /// </summary>
    private static CursorUsageEventsPage ParseUsageEventsPage(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("totalUsageEventsCount", out JsonElement totalElement))
        {
            throw new InvalidOperationException("Cursor usage events schema is missing totalUsageEventsCount.");
        }

        if (!TryGetNonNegativeInt32(totalElement, out int totalCount))
        {
            throw new InvalidOperationException("Cursor usage events schema has an invalid totalUsageEventsCount.");
        }

        if (!root.TryGetProperty("usageEventsDisplay", out JsonElement displays) || displays.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Cursor usage events schema is missing usageEventsDisplay.");
        }

        int zeroTokenMissingCostEventCount = 0;
        List<CursorUsageEvent> result = [];
        foreach (JsonElement display in displays.EnumerateArray())
        {
            if (!display.TryGetProperty("tokenUsage", out JsonElement tokenUsage) || tokenUsage.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (tokenUsage.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Cursor usage event tokenUsage is not an object.");
            }

            if (!TryGetEventTimestamp(display, out DateTimeOffset timestamp))
            {
                throw new InvalidOperationException("Cursor usage event has an invalid timestamp.");
            }

            if (!TryGetTokenCount(tokenUsage, "inputTokens", out long inputTokens, out bool hasInputTokens))
            {
                throw new InvalidOperationException("Cursor usage event has an invalid inputTokens value.");
            }

            if (!TryGetTokenCount(tokenUsage, "outputTokens", out long outputTokens, out bool hasOutputTokens))
            {
                throw new InvalidOperationException("Cursor usage event has an invalid outputTokens value.");
            }

            if (!TryGetTokenCount(tokenUsage, "cacheReadTokens", out long cacheReadTokens, out bool hasCacheReadTokens))
            {
                throw new InvalidOperationException("Cursor usage event has an invalid cacheReadTokens value.");
            }

            if (!TryGetTokenCount(tokenUsage, "cacheWriteTokens", out long cacheWriteTokens, out bool hasCacheWriteTokens))
            {
                throw new InvalidOperationException("Cursor usage event has an invalid cacheWriteTokens value.");
            }

            TotalCentsValidation totalCentsValidation = ValidateTotalCents(tokenUsage, out decimal totalCents);
            if (totalCentsValidation != TotalCentsValidation.Valid)
            {
                if (totalCentsValidation == TotalCentsValidation.Missing &&
                    inputTokens == 0 &&
                    outputTokens == 0 &&
                    cacheReadTokens == 0 &&
                    cacheWriteTokens == 0)
                {
                    zeroTokenMissingCostEventCount++;
                    continue;
                }

                throw new InvalidOperationException(totalCentsValidation switch
                {
                    TotalCentsValidation.Missing => "Cursor usage event totalCents is missing.",
                    TotalCentsValidation.Null => "Cursor usage event totalCents is null.",
                    TotalCentsValidation.NonNumericString => "Cursor usage event totalCents is a nonnumeric string.",
                    TotalCentsValidation.Negative => "Cursor usage event totalCents is negative.",
                    TotalCentsValidation.NonFinite => "Cursor usage event totalCents is nonfinite.",
                    _ => "Cursor usage event totalCents is out of range.",
                });
            }

            result.Add(new CursorUsageEvent(
                timestamp,
                inputTokens,
                outputTokens,
                cacheReadTokens,
                cacheWriteTokens,
                totalCents,
                hasInputTokens,
                hasOutputTokens,
                hasCacheReadTokens,
                hasCacheWriteTokens));
        }

        return new CursorUsageEventsPage(totalCount, displays.GetArrayLength(), result, zeroTokenMissingCostEventCount);
    }

    /// <summary>
    /// Aggregates Cursor usage events into the same periods shown for Codex token cost.
    /// </summary>
    private static TokenCostStatistics AggregateUsageEvents(IEnumerable<CursorUsageEvent> events, DateTimeOffset refreshedAt)
    {
        DateTime today = refreshedAt.LocalDateTime.Date;
        DateTime weekStart = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        DateTime monthStart = new(today.Year, today.Month, 1);
        CursorPeriodAccumulator todayPeriod = new();
        CursorPeriodAccumulator yesterdayPeriod = new();
        CursorPeriodAccumulator weekPeriod = new();
        CursorPeriodAccumulator monthPeriod = new();
        CursorPeriodAccumulator sevenDayPeriod = new();
        CursorPeriodAccumulator thirtyDayPeriod = new();
        CursorPeriodAccumulator totalPeriod = new();

        foreach (CursorUsageEvent usageEvent in events)
        {
            DateTime eventDate = usageEvent.Timestamp.LocalDateTime.Date;
            long tokens = checked(usageEvent.InputTokens + usageEvent.OutputTokens + usageEvent.CacheReadTokens + usageEvent.CacheWriteTokens);
            if (eventDate == today)
            {
                todayPeriod.Add(tokens, usageEvent.TotalCents);
            }

            if (eventDate == today.AddDays(-1))
            {
                yesterdayPeriod.Add(tokens, usageEvent.TotalCents);
            }

            if (eventDate >= weekStart && eventDate <= today)
            {
                weekPeriod.Add(tokens, usageEvent.TotalCents);
            }

            if (eventDate >= monthStart && eventDate <= today)
            {
                monthPeriod.Add(tokens, usageEvent.TotalCents);
            }

            if (eventDate >= today.AddDays(-6) && eventDate <= today)
            {
                sevenDayPeriod.Add(tokens, usageEvent.TotalCents);
            }

            if (eventDate >= today.AddDays(-29) && eventDate <= today)
            {
                thirtyDayPeriod.Add(tokens, usageEvent.TotalCents);
            }

            if (eventDate <= today)
            {
                totalPeriod.Add(tokens, usageEvent.TotalCents);
            }
        }

        return new TokenCostStatistics
        {
            Today = todayPeriod.ToSummary(),
            Yesterday = yesterdayPeriod.ToSummary(),
            Week = weekPeriod.ToSummary(),
            Month = monthPeriod.ToSummary(),
            SevenDay = sevenDayPeriod.ToSummary(),
            ThirtyDay = thirtyDayPeriod.ToSummary(),
            Total = totalPeriod.ToSummary(),
        };
    }

    /// <summary>
    /// Adds the common Cursor session cookie and JSON accept header.
    /// </summary>
    private static void AddSessionHeaders(HttpRequestMessage request, CursorCredential credential)
    {
        request.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={credential.UserId}%3A%3A{credential.AccessToken}");
        request.Headers.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// Converts an HTTP response into a sanitized Cursor endpoint failure.
    /// </summary>
    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new CursorRequestException("Cursor OAuth token expired or unauthorized. Sign in to Cursor again.", true);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CursorRequestException($"{operation}: HTTP {(int)response.StatusCode}", false);
        }
    }

    /// <summary>
    /// Converts a collection exception into a user-safe display message.
    /// </summary>
    private static string FormatError(Exception exception)
    {
        return exception switch
        {
            TaskCanceledException => "Request timed out",
            CursorRequestException requestException => requestException.Message,
            JsonException => "Cursor response JSON decode failed.",
            HttpRequestException => "Cursor network request failed.",
            OverflowException => "Cursor response numeric overflow.",
            InvalidOperationException operationException => operationException.Message,
            _ => "Cursor collection failed.",
        };
    }

    /// <summary>
    /// Loads Cursor access and refresh tokens from the local IDE state database.
    /// </summary>
    private static bool TryLoadCredential(out CursorCredential credential, out string error)
    {
        credential = default;
        string dbPath = GetStateDbPath();
        if (!File.Exists(dbPath))
        {
            error = "Cursor local auth database was not found. Sign in to Cursor first.";
            return false;
        }

        try
        {
            string? accessToken = null;
            string? refreshToken = null;
            WithSqlite(dbPath, connection =>
            {
                accessToken = ReadItemValue(connection, k_AccessTokenKey);
                refreshToken = ReadItemValue(connection, k_RefreshTokenKey);
            });

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                error = "Cursor OAuth access token was not found. Sign in to Cursor first.";
                return false;
            }

            if (!TryGetJwtSubject(accessToken, out string userId))
            {
                error = "Cursor OAuth access token did not include a user id.";
                return false;
            }

            credential = new CursorCredential(dbPath, accessToken, refreshToken ?? string.Empty, userId);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            error = "Cursor local auth database could not be read.";
            return false;
        }
    }

    /// <summary>
    /// Writes refreshed Cursor tokens back into the local IDE state database.
    /// </summary>
    private static void SaveCredential(string dbPath, OAuthTokenRefresh refresh)
    {
        WithSqlite(dbPath, connection =>
        {
            UpsertItemValue(connection, k_AccessTokenKey, refresh.AccessToken);
            UpsertItemValue(connection, k_RefreshTokenKey, refresh.RefreshToken);
        });
    }

    /// <summary>
    /// Opens the Cursor state database with busy timeout and limited retries.
    /// </summary>
    private static void WithSqlite(string dbPath, Action<SqliteConnection> action)
    {
        SqliteException? lastBusy = null;
        for (int attempt = 1; attempt <= k_SqliteRetryCount; attempt++)
        {
            try
            {
                using SqliteConnection connection = new(new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString());
                connection.Open();
                using (SqliteCommand busy = connection.CreateCommand())
                {
                    busy.CommandText = $"PRAGMA busy_timeout = {k_BusyTimeoutMilliseconds};";
                    busy.ExecuteNonQuery();
                }

                action(connection);
                return;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 5 || exception.SqliteExtendedErrorCode == 5)
            {
                lastBusy = exception;
                Thread.Sleep(50 * attempt);
            }
        }

        throw lastBusy ?? new SqliteException("Cursor state database was busy.", 5);
    }

    /// <summary>
    /// Reads one VS Code ItemTable value as text.
    /// </summary>
    private static string? ReadItemValue(SqliteConnection connection, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        object? value = command.ExecuteScalar();
        return value switch
        {
            null or DBNull => null,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Inserts or replaces one VS Code ItemTable value.
    /// </summary>
    private static void UpsertItemValue(SqliteConnection connection, string key, string value)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the Cursor state.vscdb path, honoring the test override environment variable.
    /// </summary>
    private static string GetStateDbPath()
    {
        string? overridePath = Environment.GetEnvironmentVariable(k_StateDbEnvName);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor",
            "User",
            "globalStorage",
            "state.vscdb");
    }

    /// <summary>
    /// Reads individualUsage.plan from a usage-summary payload.
    /// </summary>
    private static bool TryGetPlan(JsonElement root, out JsonElement plan)
    {
        plan = default;
        return root.TryGetProperty("individualUsage", out JsonElement individualUsage) &&
            individualUsage.ValueKind == JsonValueKind.Object &&
            individualUsage.TryGetProperty("plan", out plan) &&
            plan.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// Reads a string property or returns the supplied fallback.
    /// </summary>
    private static string GetStringProperty(JsonElement element, string propertyName, string fallback)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    /// <summary>
    /// Reads one plan usage-percent field from a usage-summary plan object.
    /// </summary>
    private static bool TryGetPlanPercent(JsonElement plan, string propertyName, out double usedPercent)
    {
        usedPercent = 0;
        if (!plan.TryGetProperty(propertyName, out JsonElement percentElement))
        {
            return false;
        }

        if (percentElement.ValueKind == JsonValueKind.Number && percentElement.TryGetDouble(out usedPercent))
        {
            return double.IsFinite(usedPercent) && usedPercent >= 0;
        }

        return percentElement.ValueKind == JsonValueKind.String &&
            double.TryParse(percentElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out usedPercent) &&
            double.IsFinite(usedPercent) &&
            usedPercent >= 0;
    }

    /// <summary>
    /// Reads billingCycleEnd as a Unix timestamp or ISO-8601 timestamp.
    /// </summary>
    private static bool TryGetBillingCycleEnd(JsonElement root, out DateTimeOffset resetsAt)
    {
        resetsAt = default;
        return root.TryGetProperty("billingCycleEnd", out JsonElement endElement) && TryGetTimestamp(endElement, out resetsAt);
    }

    /// <summary>
    /// Reads one event timestamp from a usage-events display record.
    /// </summary>
    private static bool TryGetEventTimestamp(JsonElement display, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return display.TryGetProperty("timestamp", out JsonElement value) && TryGetTimestamp(value, out timestamp);
    }

    /// <summary>
    /// Parses an ISO-8601 or Unix timestamp value.
    /// </summary>
    private static bool TryGetTimestamp(JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp))
            {
                return true;
            }

            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return false;
            }

            return TryCreateUnixTimestamp(parsed, out timestamp);
        }

        return value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long numeric) &&
            TryCreateUnixTimestamp(numeric, out timestamp);
    }

    /// <summary>
    /// Creates a timestamp from Unix seconds or milliseconds.
    /// </summary>
    private static bool TryCreateUnixTimestamp(long value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        try
        {
            timestamp = value > 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads an optional token count, treating only a missing field as zero.
    /// </summary>
    private static bool TryGetTokenCount(JsonElement parent, string propertyName, out long value, out bool isPresent)
    {
        value = 0;
        isPresent = parent.TryGetProperty(propertyName, out JsonElement element);
        return !isPresent || TryGetNonNegativeInt64(element, out value);
    }

    /// <summary>
    /// Reads a required non-negative integer property encoded as a number or string.
    /// </summary>
    private static bool TryGetNonNegativeInt32(JsonElement element, out int value)
    {
        value = 0;
        return TryGetNonNegativeInt64(element, out long parsed) &&
            parsed <= int.MaxValue &&
            (value = (int)parsed) >= 0;
    }

    /// <summary>
    /// Reads a non-negative integer encoded as a JSON number or numeric string.
    /// </summary>
    private static bool TryGetNonNegativeInt64(JsonElement element, out long value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out value) && value >= 0;
        }

        return element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value >= 0;
    }

    /// <summary>
    /// Validates totalCents without exposing raw event values in error messages.
    /// </summary>
    private static TotalCentsValidation ValidateTotalCents(JsonElement parent, out decimal value)
    {
        value = 0;
        if (!parent.TryGetProperty("totalCents", out JsonElement element))
        {
            return TotalCentsValidation.Missing;
        }

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return TotalCentsValidation.Null;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetDecimal(out value))
            {
                return element.TryGetDouble(out double floating) && !double.IsFinite(floating)
                    ? TotalCentsValidation.NonFinite
                    : TotalCentsValidation.OutOfRange;
            }

            return value < 0 ? TotalCentsValidation.Negative : TotalCentsValidation.Valid;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return TotalCentsValidation.NonNumericString;
        }

        string? text = element.GetString();
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return value < 0 ? TotalCentsValidation.Negative : TotalCentsValidation.Valid;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedFloating))
        {
            return TotalCentsValidation.NonNumericString;
        }

        return !double.IsFinite(parsedFloating)
            ? TotalCentsValidation.NonFinite
            : TotalCentsValidation.OutOfRange;
    }

    /// <summary>
    /// Returns whether the access token should be refreshed before the next usage call.
    /// </summary>
    private static bool NeedsRefresh(string accessToken)
    {
        return TryGetJwtExpiry(accessToken, out DateTimeOffset expiry) && expiry <= DateTimeOffset.UtcNow + s_RefreshBuffer;
    }

    /// <summary>
    /// Reads the JWT exp claim without validating the signature.
    /// </summary>
    private static bool TryGetJwtExpiry(string token, out DateTimeOffset expiry)
    {
        expiry = default;
        return TryReadJwtPayload(token, out JsonElement payload) &&
            payload.TryGetProperty("exp", out JsonElement exp) &&
            exp.TryGetInt64(out long seconds) &&
            TryCreateUnixTimestamp(seconds, out expiry);
    }

    /// <summary>
    /// Reads the JWT sub claim used as the Cursor session user id.
    /// </summary>
    private static bool TryGetJwtSubject(string token, out string subject)
    {
        subject = string.Empty;
        if (!TryReadJwtPayload(token, out JsonElement payload) ||
            !payload.TryGetProperty("sub", out JsonElement sub) ||
            sub.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(sub.GetString()))
        {
            return false;
        }

        subject = sub.GetString()!;
        return true;
    }

    /// <summary>
    /// Decodes a JWT payload JSON object without validating the signature.
    /// </summary>
    private static bool TryReadJwtPayload(string token, out JsonElement payload)
    {
        payload = default;
        string[] parts = token.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        try
        {
            string encoded = parts[1].Replace('-', '+').Replace('_', '/');
            switch (encoded.Length % 4)
            {
                case 2:
                    encoded += "==";
                    break;
                case 3:
                    encoded += "=";
                    break;
            }

            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            payload = document.RootElement.Clone();
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private readonly record struct CursorCredential(string DbPath, string AccessToken, string RefreshToken, string UserId);

    private readonly record struct CursorCredentialResult(CursorCredential Credential, bool RefreshUsed);

    private sealed record OAuthTokenRefresh(string AccessToken, string RefreshToken);

    private sealed record CursorEndpointResult<T>(T? Value, string Error, CursorCredential Credential, bool RefreshUsed);

    private sealed record CursorUsageEventsPage(int TotalCount, int DisplayCount, IReadOnlyList<CursorUsageEvent> Events, int ZeroTokenMissingCostEventCount);

    private sealed record CursorUsageEvent(
        DateTimeOffset Timestamp,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        decimal TotalCents,
        bool HasInputTokens,
        bool HasOutputTokens,
        bool HasCacheReadTokens,
        bool HasCacheWriteTokens);

    private sealed record CursorUsageEventsCollection(TokenCostStatistics Statistics, CursorUsageEventsDiagnostics Diagnostics);

    private enum TotalCentsValidation
    {
        Valid,
        Missing,
        Null,
        NonNumericString,
        Negative,
        NonFinite,
        OutOfRange,
    }

    private sealed class CursorPeriodAccumulator
    {
        private long m_TotalTokens;
        private decimal m_TotalCents;

        /// <summary>
        /// Adds one complete Cursor usage event to the period.
        /// </summary>
        public void Add(long tokens, decimal totalCents)
        {
            m_TotalTokens = checked(m_TotalTokens + tokens);
            m_TotalCents += totalCents;
        }

        /// <summary>
        /// Converts the accumulated values into the shared token-cost summary shape.
        /// </summary>
        public TokenCostSummary ToSummary()
        {
            return new TokenCostSummary
            {
                TotalTokens = m_TotalTokens,
                CostUsd = m_TotalCents / 100m,
            };
        }
    }

    private sealed class CursorRequestException : InvalidOperationException
    {
        /// <summary>
        /// Creates a Cursor endpoint exception with its retry classification.
        /// </summary>
        public CursorRequestException(string message, bool isAuthenticationFailure)
            : base(message)
        {
            IsAuthenticationFailure = isAuthenticationFailure;
        }

        public bool IsAuthenticationFailure { get; }
    }
}
