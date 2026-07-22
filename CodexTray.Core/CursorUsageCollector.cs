using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTray.Core;

public sealed record CursorUsageSnapshot(
    double TotalUsedPercent,
    double FirstPartyUsedPercent,
    double ApiUsedPercent,
    long ResetsAt);

public sealed class CursorUsageCollector
{
    private const string k_UsageEndpoint = "https://cursor.com/api/usage-summary";
    private const string k_TokenEndpoint = "https://api2.cursor.sh/oauth/token";
    private const string k_ClientId = "KbZUR41cY7W6zRSdpSUJ7I7mLYBKOCmB";
    private const string k_AccessTokenKey = "cursorAuth/accessToken";
    private const string k_RefreshTokenKey = "cursorAuth/refreshToken";
    private const string k_StateDbEnvName = "CODEXTRAY_CURSOR_STATE_VSCDB";
    private const int k_BusyTimeoutMilliseconds = 5_000;
    private const int k_SqliteRetryCount = 3;
    private static readonly TimeSpan s_RefreshBuffer = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim s_AuthLock = new(1, 1);

    private readonly HttpClient m_HttpClient;

    /// <summary>
    /// Creates a collector using the supplied HTTP client.
    /// </summary>
    public CursorUsageCollector(HttpClient httpClient)
    {
        m_HttpClient = httpClient;
    }

    /// <summary>
    /// Collects Cursor plan usage from the local IDE OAuth session.
    /// </summary>
    public async Task<CursorUsageSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        CursorCredential credential = await ResolveCredentialAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        try
        {
            return await FetchUsageAsync(credential, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (IsAuthFailure(exception))
        {
            credential = await ResolveCredentialAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            return await FetchUsageAsync(credential, cancellationToken).ConfigureAwait(false);
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
            !TryGetPlanPercent(plan, "autoPercentUsed", out double firstPartyUsedPercent) ||
            !TryGetPlanPercent(plan, "apiPercentUsed", out double apiUsedPercent))
        {
            throw new InvalidOperationException("Cursor usage-summary did not include plan usage percents.");
        }

        if (!TryGetBillingCycleEnd(root, out DateTimeOffset resetsAt) || resetsAt <= now)
        {
            throw new InvalidOperationException("Cursor usage-summary did not include a valid billing cycle end.");
        }

        return new CursorUsageSnapshot(
            totalUsedPercent,
            firstPartyUsedPercent,
            apiUsedPercent,
            resetsAt.ToUnixTimeSeconds());
    }

    /// <summary>
    /// Loads local Cursor credentials and refreshes them when expired or forced.
    /// </summary>
    private async Task<CursorCredential> ResolveCredentialAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await s_AuthLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryLoadCredential(out CursorCredential credential, out string error))
            {
                throw new InvalidOperationException(error);
            }

            if (!forceRefresh && !NeedsRefresh(credential.AccessToken))
            {
                return credential;
            }

            if (string.IsNullOrWhiteSpace(credential.RefreshToken))
            {
                throw new InvalidOperationException(forceRefresh
                    ? "Cursor OAuth token expired or unauthorized and no refresh token is available. Sign in to Cursor again."
                    : "Cursor OAuth token expired. Sign in to Cursor again.");
            }

            OAuthTokenRefresh refresh = await RefreshOAuthTokenAsync(credential.RefreshToken, cancellationToken).ConfigureAwait(false);
            SaveCredential(credential.DbPath, refresh);
            if (!TryGetJwtSubject(refresh.AccessToken, out string userId))
            {
                throw new InvalidOperationException("Cursor OAuth access token did not include a user id.");
            }

            return new CursorCredential(credential.DbPath, refresh.AccessToken, refresh.RefreshToken, userId);
        }
        finally
        {
            s_AuthLock.Release();
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

        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        if (document.RootElement.TryGetProperty("expires_in", out JsonElement expiresIn) &&
            expiresIn.TryGetDouble(out double seconds) &&
            seconds > 0)
        {
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
        else if (TryGetJwtExpiry(accessToken.GetString()!, out DateTimeOffset jwtExpiry))
        {
            expiresAt = jwtExpiry;
        }

        return new OAuthTokenRefresh(accessToken.GetString()!, nextRefresh, null, expiresAt);
    }

    /// <summary>
    /// Sends the Cursor usage-summary request for one credential.
    /// </summary>
    private async Task<CursorUsageSnapshot> FetchUsageAsync(CursorCredential credential, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, k_UsageEndpoint);
        request.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={credential.UserId}%3A%3A{credential.AccessToken}");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("CodexTray");

        using HttpResponseMessage response = await m_HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Cursor OAuth token expired or unauthorized. Sign in to Cursor again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Cursor usage request failed: HTTP {(int)response.StatusCode}");
        }

        return ParseUsageSummary(body, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Loads Cursor access and refresh tokens from the local IDE state database.
    /// </summary>
    private static bool TryLoadCredential(out CursorCredential credential, out string error)
    {
        credential = default!;
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
        if (!root.TryGetProperty("individualUsage", out JsonElement individualUsage) ||
            individualUsage.ValueKind != JsonValueKind.Object ||
            !individualUsage.TryGetProperty("plan", out plan) ||
            plan.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return true;
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
        if (!root.TryGetProperty("billingCycleEnd", out JsonElement endElement))
        {
            return false;
        }

        if (endElement.ValueKind == JsonValueKind.Number)
        {
            if (endElement.TryGetInt64(out long seconds) && seconds > 1_000_000_000_000)
            {
                resetsAt = DateTimeOffset.FromUnixTimeMilliseconds(seconds);
                return true;
            }

            if (endElement.TryGetInt64(out seconds) && seconds > 1_000_000_000)
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            }

            if (endElement.TryGetDouble(out double floating) && floating > 1_000_000_000)
            {
                resetsAt = floating > 1_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)floating)
                    : DateTimeOffset.FromUnixTimeSeconds((long)floating);
                return true;
            }

            return false;
        }

        if (endElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? text = endElement.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out resetsAt))
        {
            return true;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return false;
        }

        if (parsed > 1_000_000_000_000)
        {
            resetsAt = DateTimeOffset.FromUnixTimeMilliseconds(parsed);
            return true;
        }

        if (parsed > 1_000_000_000)
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(parsed);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns whether the access token should be refreshed before the next usage call.
    /// </summary>
    private static bool NeedsRefresh(string accessToken)
    {
        return TryGetJwtExpiry(accessToken, out DateTimeOffset expiry) &&
            expiry <= DateTimeOffset.UtcNow + s_RefreshBuffer;
    }

    /// <summary>
    /// Reads the JWT exp claim without validating the signature.
    /// </summary>
    private static bool TryGetJwtExpiry(string token, out DateTimeOffset expiry)
    {
        expiry = default;
        if (!TryReadJwtPayload(token, out JsonElement payload))
        {
            return false;
        }

        if (!payload.TryGetProperty("exp", out JsonElement exp) || !exp.TryGetInt64(out long seconds))
        {
            return false;
        }

        expiry = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
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

    /// <summary>
    /// Returns whether a usage failure should trigger one OAuth refresh retry.
    /// </summary>
    private static bool IsAuthFailure(InvalidOperationException exception)
    {
        return exception.Message.Contains("expired or unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct CursorCredential(string DbPath, string AccessToken, string RefreshToken, string UserId);

    private sealed record OAuthTokenRefresh(string AccessToken, string RefreshToken, string? IdToken, DateTimeOffset ExpiresAt);
}
