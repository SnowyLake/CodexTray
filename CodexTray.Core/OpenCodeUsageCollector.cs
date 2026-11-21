using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexTray.Core;

/// <summary>
/// Represents the collected quota windows and subscription tier for OpenCode.
/// </summary>
public sealed class OpenCodeUsageSnapshot
{
    public string Source { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }

    public UsageLimit? Session { get; init; }

    public UsageLimit? Weekly { get; init; }

    public UsageLimit? Monthly { get; init; }

    public string SubscriptionTier { get; init; } = string.Empty;

    public bool IsAvailable { get; init; }
}

/// <summary>
/// Represents a display-ready OpenCode usage dashboard result.
/// </summary>
public sealed class OpenCodeUsageDashboard
{
    public static OpenCodeUsageDashboard Empty { get; } = new()
    {
        Snapshot = new OpenCodeUsageSnapshot
        {
            Source = "opencode",
            UpdatedAt = DateTimeOffset.MinValue,
            IsAvailable = false,
        },
        StatusMessage = "OpenCode usage is unavailable",
    };

    public required OpenCodeUsageSnapshot Snapshot { get; init; }

    public required string StatusMessage { get; init; }
}

/// <summary>
/// Collects OpenCode Go quota windows and subscription tier from local read-only CLI credentials or falls back to Free when unsubscribed.
/// </summary>
public sealed class OpenCodeUsageCollector
{
    private const int k_FiveHourWindowMinutes = 300;
    private const int k_WeeklyWindowMinutes = 10_080;
    private const int k_MonthlyWindowMinutes = 43_200;
    private const double k_FiveHourWindowSeconds = k_FiveHourWindowMinutes * 60.0;
    private const double k_PlaceholderResetToleranceSeconds = 2.0;
    private const string k_DefaultUsageUrl = "https://opencode.ai/zen/go/v1/usage";
    private const string k_DefaultGoStatusUrl = "https://opencode.ai/console/api/go/status";

    private static readonly HttpClient s_HttpClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly HttpClient m_HttpClient;
    private readonly Func<DateTimeOffset> m_NowProvider;
    private readonly string? m_ExplicitDataDirectory;
    private readonly Func<string, string?> m_GetEnvironmentVariable;
    private readonly string m_UsageUrl;
    private readonly string m_GoStatusUrl;

    /// <summary>
    /// Creates an OpenCode usage collector using the default local paths and HTTP client.
    /// </summary>
    public OpenCodeUsageCollector()
        : this(s_HttpClient, explicitDataDirectory: null, getEnvironmentVariable: null, usageUrl: null, goStatusUrl: null, nowProvider: null)
    {
    }

    /// <summary>
    /// Creates an OpenCode usage collector with explicit dependencies for deterministic tests.
    /// </summary>
    internal OpenCodeUsageCollector(
        HttpClient httpClient,
        string? explicitDataDirectory,
        Func<string, string?>? getEnvironmentVariable = null,
        string? usageUrl = null,
        string? goStatusUrl = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        m_HttpClient = httpClient;
        m_ExplicitDataDirectory = explicitDataDirectory;
        m_GetEnvironmentVariable = getEnvironmentVariable ?? (explicitDataDirectory == null ? Environment.GetEnvironmentVariable : _ => null);
        m_UsageUrl = string.IsNullOrWhiteSpace(usageUrl) ? k_DefaultUsageUrl : usageUrl;
        m_GoStatusUrl = string.IsNullOrWhiteSpace(goStatusUrl) ? k_DefaultGoStatusUrl : goStatusUrl;
        m_NowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// Collects OpenCode quota windows and subscription tier for the current local login, returning Free when not logged in or unsubscribed.
    /// </summary>
    public async Task<OpenCodeUsageSnapshot> CollectAsync(bool useAbsoluteResetTime = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = m_NowProvider();
        OpenCodeCredential? credential = LoadCredential(now);
        if (credential == null)
        {
            return CreateFreeSnapshot(now, "opencode_free");
        }

        using HttpRequestMessage request = new(HttpMethod.Get, m_UsageUrl);
        ApplyUsageHeaders(request, credential.Token, credential.OrgId, useOpenCodeOrgHeader: true);

        using HttpResponseMessage response = await UsageHttp.SendAsync(m_HttpClient, request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            string errorType = ReadErrorType(body);
            if (string.Equals(errorType, "EntitlementError", StringComparison.OrdinalIgnoreCase))
            {
                return CreateFreeSnapshot(m_NowProvider(), "opencode_free");
            }

            if (!string.Equals(errorType, "AuthError", StringComparison.OrdinalIgnoreCase) &&
                credential.IsOAuth)
            {
                GoStatusResult goStatus = await FetchGoStatusAsync(credential.Token, credential.OrgId, cancellationToken).ConfigureAwait(false);
                if (goStatus.Kind == GoStatusKind.NoSubscription)
                {
                    return CreateFreeSnapshot(m_NowProvider(), "opencode_free");
                }
            }

            throw new OpenCodeAuthException("OpenCode OAuth token expired or unauthorized; sign in through OpenCode.");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new OpenCodeAuthException("OpenCode OAuth token expired or unauthorized; sign in through OpenCode.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UsageHttpException(response.StatusCode, retryAfter: OAuthTokenHelpers.TryReadRetryAfter(response));
        }

        DateTimeOffset receivedAt = m_NowProvider();
        DateTimeOffset capturedAt = response.Headers.Date ?? receivedAt;
        OpenCodeUsageSnapshot parsed = ParseUsageResponse(body, capturedAt, receivedAt, useAbsoluteResetTime);

        string plan = string.Empty;
        if (credential.IsOAuth || !string.IsNullOrWhiteSpace(credential.OrgId))
        {
            GoStatusResult goStatus = await FetchGoStatusAsync(credential.Token, credential.OrgId, cancellationToken).ConfigureAwait(false);
            if (goStatus.Kind == GoStatusKind.Subscribed)
            {
                plan = goStatus.Plan;
            }
        }

        if (plan.Length == 0)
        {
            plan = "GO";
        }

        return new OpenCodeUsageSnapshot
        {
            Source = parsed.Source,
            UpdatedAt = parsed.UpdatedAt,
            Session = parsed.Session,
            Weekly = parsed.Weekly,
            Monthly = parsed.Monthly,
            SubscriptionTier = plan,
            IsAvailable = parsed.IsAvailable,
        };
    }

    /// <summary>
    /// Collects a display-ready OpenCode dashboard without throwing collector-level failures.
    /// </summary>
    public async Task<OpenCodeUsageDashboard> CollectDashboardAsync(bool useAbsoluteResetTime = false, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = m_NowProvider();
        try
        {
            OpenCodeUsageSnapshot snapshot = await CollectAsync(useAbsoluteResetTime, cancellationToken).ConfigureAwait(false);
            return new OpenCodeUsageDashboard
            {
                Snapshot = snapshot,
                StatusMessage = string.Empty,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OpenCodeDataException or OpenCodeAuthException or UsageHttpException
            or HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
            or IOException or UnauthorizedAccessException or SqliteException or FormatException or OverflowException)
        {
            string message = exception is OpenCodeDataException or OpenCodeAuthException
                ? exception.Message
                : UsageDiagnostics.Error(exception);
            return new OpenCodeUsageDashboard
            {
                Snapshot = new OpenCodeUsageSnapshot
                {
                    Source = "opencode",
                    UpdatedAt = now,
                    IsAvailable = false,
                },
                StatusMessage = message,
            };
        }
    }

    /// <summary>
    /// Builds the OpenCode Session value exposed to monitor plugins.
    /// </summary>
    public static OpenCodePluginUsage BuildPluginUsage(OpenCodeUsageDashboard dashboard)
    {
        OpenCodeUsageSnapshot snapshot = dashboard.Snapshot;
        UsageLimit? session = snapshot.IsAvailable ? snapshot.Session : null;
        if (session == null || session.WindowMinutes <= 0)
        {
            return new OpenCodePluginUsage(
                new UsageLimit { Name = "session", UsedPercent = 100, RemainingPercent = 0 },
                CodexTrayDefaults.UnavailableDisplay);
        }

        int remainingPercent = Math.Clamp(session.RemainingPercent, 0, 100);
        int usedPercent = 100 - remainingPercent;
        return new OpenCodePluginUsage(
            new UsageLimit
            {
                Name = "session",
                UsedPercent = usedPercent,
                RemainingPercent = remainingPercent,
                WindowMinutes = session.WindowMinutes,
                ResetsAt = session.ResetsAt,
            },
            $"{remainingPercent}%");
    }

    /// <summary>
    /// Formats a reset label for one OpenCode quota window according to absolute or countdown display mode.
    /// </summary>
    public static string FormatResetLabel(UsageLimit limit, bool useAbsoluteResetTime, DateTimeOffset now)
    {
        if (limit.ResetsAt <= 0)
        {
            return "unknown";
        }

        if (limit.WindowMinutes > 0 && limit.WindowMinutes <= k_FiveHourWindowMinutes)
        {
            if (useAbsoluteResetTime)
            {
                return DateTimeOffset.FromUnixTimeSeconds(limit.ResetsAt).ToOffset(now.Offset).ToString("HH:mm", CultureInfo.InvariantCulture);
            }

            TimeSpan remaining = UsageLimit.GetRemainingTime(limit.ResetsAt, now);
            long hours = (long)Math.Floor(remaining.TotalHours);
            return string.Create(CultureInfo.InvariantCulture, $"{hours}h{remaining.Minutes:D2}m");
        }

        return useAbsoluteResetTime
            ? UsageLimit.FormatResetDate(limit.ResetsAt, now)
            : UsageLimit.FormatResetLabel(limit.ResetsAt, now);
    }

    /// <summary>
    /// Parses the OpenCode Go usage JSON payload into Session, Weekly, and Monthly quota windows.
    /// </summary>
    public static OpenCodeUsageSnapshot ParseUsageResponse(
        string responseBody,
        DateTimeOffset capturedAt,
        DateTimeOffset now,
        bool useAbsoluteResetTime = false)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            throw new OpenCodeDataException("Could not parse OpenCode usage response.");
        }

        UsageLimit session = ParseWindow(usage, "rolling", "session", k_FiveHourWindowMinutes, capturedAt, now, useAbsoluteResetTime, dropPlaceholderReset: true);
        UsageLimit weekly = ParseWindow(usage, "weekly", "weekly", k_WeeklyWindowMinutes, capturedAt, now, useAbsoluteResetTime, dropPlaceholderReset: false);
        UsageLimit monthly = ParseWindow(usage, "monthly", "monthly", k_MonthlyWindowMinutes, capturedAt, now, useAbsoluteResetTime, dropPlaceholderReset: false);

        return new OpenCodeUsageSnapshot
        {
            Source = "opencode_go",
            UpdatedAt = now,
            Session = session,
            Weekly = weekly,
            Monthly = monthly,
            SubscriptionTier = "GO",
            IsAvailable = true,
        };
    }

    /// <summary>
    /// Normalizes an OpenCode Go product identifier into a compact uppercase badge.
    /// </summary>
    public static string NormalizeSubscriptionTier(string? rawProduct)
    {
        string normalized = (rawProduct ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return normalized.ToLowerInvariant() switch
        {
            "free" => "FREE",
            "go" => "GO",
            "go-plus" or "go_plus" or "go plus" => "GO PLUS",
            "go-teams" or "go_teams" or "go teams" => "GO TEAMS",
            _ => string.Join(' ', normalized.Replace('_', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant(),
        };
    }

    /// <summary>
    /// Resolves the OpenCode local data directory honoring XDG_DATA_HOME when rooted.
    /// </summary>
    internal static string ResolveDataDirectory(string? explicitDataDirectory = null, Func<string, string?>? getEnvironmentVariable = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDataDirectory))
        {
            return explicitDataDirectory;
        }

        Func<string, string?> env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        string? xdgDataHome = env("XDG_DATA_HOME")?.Trim();
        if (!string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathRooted(xdgDataHome))
        {
            return Path.Combine(xdgDataHome, "opencode");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode");
    }

    /// <summary>
    /// Resolves the single OpenCode SQLite database path honoring OPENCODE_DB, or null for :memory:.
    /// </summary>
    internal static string? ResolveDatabasePath(string? explicitDataDirectory = null, Func<string, string?>? getEnvironmentVariable = null)
    {
        string dataDirectory = ResolveDataDirectory(explicitDataDirectory, getEnvironmentVariable);
        Func<string, string?> env = getEnvironmentVariable ?? (explicitDataDirectory == null ? Environment.GetEnvironmentVariable : _ => null);
        string? overrideDb = env("OPENCODE_DB")?.Trim();
        if (!string.IsNullOrWhiteSpace(overrideDb))
        {
            if (string.Equals(overrideDb, ":memory:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Path.IsPathRooted(overrideDb) ? overrideDb : Path.Combine(dataDirectory, overrideDb);
        }

        return Path.Combine(dataDirectory, "opencode.db");
    }

    /// <summary>
    /// Creates the default Free snapshot with 100% remaining across Session, Weekly, and Monthly windows.
    /// </summary>
    internal static OpenCodeUsageSnapshot CreateFreeSnapshot(DateTimeOffset now, string source = "opencode_free")
    {
        return new OpenCodeUsageSnapshot
        {
            Source = source,
            UpdatedAt = now,
            Session = CreateFullLimit("session", k_FiveHourWindowMinutes),
            Weekly = CreateFullLimit("weekly", k_WeeklyWindowMinutes),
            Monthly = CreateFullLimit("monthly", k_MonthlyWindowMinutes),
            SubscriptionTier = "FREE",
            IsAvailable = true,
        };
    }

    private static UsageLimit CreateFullLimit(string name, int windowMinutes)
    {
        return new UsageLimit
        {
            Name = name,
            UsedPercent = 0,
            RemainingPercent = 100,
            WindowMinutes = windowMinutes,
            ResetsAt = 0,
            ResetLabel = "unknown",
        };
    }

    private static UsageLimit ParseWindow(
        JsonElement usage,
        string propertyName,
        string limitName,
        int windowMinutes,
        DateTimeOffset capturedAt,
        DateTimeOffset now,
        bool useAbsoluteResetTime,
        bool dropPlaceholderReset)
    {
        if (!usage.TryGetProperty(propertyName, out JsonElement window) ||
            window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("percent", out JsonElement percentElement) ||
            percentElement.ValueKind != JsonValueKind.Number ||
            !percentElement.TryGetDouble(out double rawPercent) ||
            !double.IsFinite(rawPercent))
        {
            throw new OpenCodeDataException("Could not parse OpenCode usage response.");
        }

        double clampedUsed = Math.Clamp(rawPercent, 0, 100);
        int usedPercent = (int)Math.Round(clampedUsed, MidpointRounding.AwayFromZero);
        int remainingPercent = 100 - usedPercent;

        long resetsAt = 0;
        if (window.TryGetProperty("resetsAt", out JsonElement resetsAtElement) && resetsAtElement.ValueKind != JsonValueKind.Null)
        {
            if (resetsAtElement.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(resetsAtElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsedReset))
            {
                throw new OpenCodeDataException("Could not parse OpenCode usage response.");
            }

            resetsAt = parsedReset.ToUnixTimeSeconds();
            // Untouched rolling sessions answer resetsAt = now + 5h on every request; drop that placeholder so it does not show a sliding countdown.
            if (dropPlaceholderReset &&
                clampedUsed == 0 &&
                Math.Abs((parsedReset - capturedAt).TotalSeconds - k_FiveHourWindowSeconds) <= k_PlaceholderResetToleranceSeconds)
            {
                resetsAt = 0;
            }
        }
        else if (window.TryGetProperty("resetInSec", out JsonElement resetInSecElement) &&
                 resetInSecElement.ValueKind == JsonValueKind.Number &&
                 resetInSecElement.TryGetDouble(out double resetInSec) &&
                 double.IsFinite(resetInSec) &&
                 resetInSec > 0)
        {
            DateTimeOffset computedReset = capturedAt.AddSeconds(resetInSec);
            resetsAt = computedReset.ToUnixTimeSeconds();
            if (dropPlaceholderReset &&
                clampedUsed == 0 &&
                Math.Abs(resetInSec - k_FiveHourWindowSeconds) <= k_PlaceholderResetToleranceSeconds)
            {
                resetsAt = 0;
            }
        }

        UsageLimit limit = new()
        {
            Name = limitName,
            UsedPercent = usedPercent,
            RemainingPercent = remainingPercent,
            WindowMinutes = windowMinutes,
            ResetsAt = resetsAt,
        };
        limit.ResetLabel = FormatResetLabel(limit, useAbsoluteResetTime, now);
        return limit;
    }

    private OpenCodeCredential? LoadCredential(DateTimeOffset now)
    {
        string? envApiKey = m_GetEnvironmentVariable("OPENCODE_API_KEY")?.Trim();
        if (!string.IsNullOrWhiteSpace(envApiKey))
        {
            return new OpenCodeCredential(ValidateToken(envApiKey), OrgId: null, IsOAuth: false);
        }

        string dataDirectory = ResolveDataDirectory(m_ExplicitDataDirectory, m_GetEnvironmentVariable);
        string? databasePath = ResolveDatabasePath(m_ExplicitDataDirectory, m_GetEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(databasePath) && File.Exists(databasePath))
        {
            if (TryLoadDatabaseCredential(databasePath, now, out OpenCodeCredential? dbCredential))
            {
                return dbCredential;
            }
        }

        return LoadLegacyAuthCredential(Path.Combine(dataDirectory, "auth.json"), now);
    }

    /// <summary>
    /// Opens an OpenCode SQLite database read-only, using immutable URI mode when no active WAL/SHM sidecar exists
    /// so quiescent WAL databases do not create sidecar files or fail in read-only directories.
    /// </summary>
    internal static SqliteConnection OpenReadOnlyConnection(string databasePath)
    {
        bool hasActiveWal = File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm");
        SqliteConnectionStringBuilder builder = new()
        {
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        };
        if (hasActiveWal)
        {
            builder.DataSource = databasePath;
        }
        else
        {
            string fullPath = Path.GetFullPath(databasePath);
            builder.DataSource = $"{new Uri(fullPath).AbsoluteUri}?mode=ro&immutable=1";
        }

        SqliteConnection connection = new(builder.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads active v2 CLI credentials from opencode.db without writing or refreshing tokens.
    /// Returns true when the v2 credential table is authoritative (preventing fallback to stale auth.json after logout).
    /// </summary>
    private static bool TryLoadDatabaseCredential(string databasePath, DateTimeOffset now, out OpenCodeCredential? credential)
    {
        credential = null;
        using SqliteConnection connection = OpenReadOnlyConnection(databasePath);

        using (SqliteCommand tableCheck = connection.CreateCommand())
        {
            tableCheck.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'credential' LIMIT 1";
            if (tableCheck.ExecuteScalar() == null)
            {
                return false;
            }
        }

        using SqliteCommand query = connection.CreateCommand();
        query.CommandText = """
            SELECT integration_id, value FROM credential
            WHERE active = 1 AND integration_id IN ('opencode-go', 'opencode')
            ORDER BY CASE integration_id WHEN 'opencode-go' THEN 0 ELSE 1 END
            LIMIT 2
            """;

        using SqliteDataReader reader = query.ExecuteReader();
        if (!reader.Read())
        {
            return true;
        }

        string selectedIntegration = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        string rawValue = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

        if (reader.Read())
        {
            string secondIntegration = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (string.Equals(selectedIntegration, secondIntegration, StringComparison.Ordinal))
            {
                throw new OpenCodeAuthException("Multiple active OpenCode credentials found; sign in again through OpenCode.");
            }
        }

        credential = ParseCredentialEntry(rawValue, now);
        return true;
    }

    private static OpenCodeCredential? LoadLegacyAuthCredential(string authPath, DateTimeOffset now)
    {
        if (!File.Exists(authPath))
        {
            return null;
        }

        string json = File.ReadAllText(authPath);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new OpenCodeDataException("Local OpenCode auth file has an invalid format.");
        }

        foreach (string key in new[] { "opencode-go", "opencode" })
        {
            if (document.RootElement.TryGetProperty(key, out JsonElement entry))
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new OpenCodeAuthException("OpenCode authentication credential is invalid; sign in through OpenCode.");
                }

                return ParseCredentialObject(entry, now);
            }
        }

        return null;
    }

    private static OpenCodeCredential ParseCredentialEntry(string rawJson, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            throw new OpenCodeAuthException("OpenCode authentication credential is invalid; sign in through OpenCode.");
        }

        using JsonDocument document = JsonDocument.Parse(rawJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new OpenCodeAuthException("OpenCode authentication credential is invalid; sign in through OpenCode.");
        }

        return ParseCredentialObject(document.RootElement, now);
    }

    private static OpenCodeCredential ParseCredentialObject(JsonElement entry, DateTimeOffset now)
    {
        string type = ReadOptionalString(entry, "type");
        string? orgId = null;
        if (entry.TryGetProperty("metadata", out JsonElement metadata))
        {
            if (metadata.ValueKind == JsonValueKind.Object)
            {
                ValidateOfficialServer(metadata);
                string candidateOrg = ReadOptionalString(metadata, "orgID");
                if (candidateOrg.Length == 0)
                {
                    candidateOrg = ReadOptionalString(metadata, "org_id");
                }

                if (candidateOrg.Length > 0)
                {
                    orgId = candidateOrg;
                }
            }
            else if (metadata.ValueKind != JsonValueKind.Null)
            {
                throw new OpenCodeDataException("OpenCode credential metadata is invalid.");
            }
        }

        if (string.Equals(type, "key", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "api", StringComparison.OrdinalIgnoreCase))
        {
            string key = ValidateToken(ReadOptionalString(entry, "key"));
            return new OpenCodeCredential(key, orgId, IsOAuth: false);
        }

        if (string.Equals(type, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            string access = ValidateToken(ReadOptionalString(entry, "access"));
            if (entry.TryGetProperty("expires", out JsonElement expiresElement) && expiresElement.ValueKind != JsonValueKind.Null)
            {
                if (expiresElement.ValueKind != JsonValueKind.Number ||
                    !expiresElement.TryGetDouble(out double expiresMs) ||
                    !double.IsFinite(expiresMs) ||
                    expiresMs < 0)
                {
                    throw new OpenCodeAuthException("OpenCode OAuth token is invalid; sign in through OpenCode.");
                }

                if (expiresMs / 1000.0 <= now.ToUnixTimeSeconds())
                {
                    throw new OpenCodeAuthException("OpenCode OAuth token expired; renew login in OpenCode.");
                }
            }
            else if (OAuthTokenHelpers.TryGetJwtExpiry(access, out DateTimeOffset jwtExpiry) && jwtExpiry <= now)
            {
                throw new OpenCodeAuthException("OpenCode OAuth token expired; renew login in OpenCode.");
            }

            return new OpenCodeCredential(access, orgId, IsOAuth: true);
        }

        throw new OpenCodeAuthException("OpenCode authentication credential is invalid; sign in through OpenCode.");
    }

    private static string ValidateToken(string? rawToken)
    {
        if (string.IsNullOrEmpty(rawToken) || rawToken.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new OpenCodeAuthException("OpenCode authentication credential is invalid; sign in through OpenCode.");
        }

        return rawToken;
    }

    private static void ValidateOfficialServer(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("server", out JsonElement serverElement) || serverElement.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (serverElement.ValueKind != JsonValueKind.String)
        {
            throw new OpenCodeDataException("OpenCode credential uses a custom server; only official opencode.ai is supported.");
        }

        string serverText = (serverElement.GetString() ?? string.Empty).Trim();
        if (serverText.Length == 0)
        {
            return;
        }

        if (!IsOfficialConsoleServer(serverText))
        {
            throw new OpenCodeDataException("OpenCode credential uses a custom server; only official opencode.ai is supported.");
        }
    }

    internal static bool IsOfficialConsoleServer(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 ||
            uri.Fragment.Length > 0)
        {
            return false;
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        if (string.Equals(uri.IdnHost, "opencode.ai", StringComparison.OrdinalIgnoreCase))
        {
            return path.Length == 0 || string.Equals(path, "/console", StringComparison.Ordinal);
        }

        // Upstream OpenCode rewrites retired https://console.opencode.ai credentials onto https://opencode.ai/console.
        if (string.Equals(uri.IdnHost, "console.opencode.ai", StringComparison.OrdinalIgnoreCase))
        {
            return path.Length == 0;
        }

        return false;
    }

    private async Task<GoStatusResult> FetchGoStatusAsync(string accessToken, string? orgId, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, m_GoStatusUrl);
            ApplyUsageHeaders(request, accessToken, orgId, useOpenCodeOrgHeader: false);

            using HttpResponseMessage response = await UsageHttp.SendAsync(m_HttpClient, request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new GoStatusResult(GoStatusKind.Unknown, string.Empty);
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseGoStatus(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or IOException)
        {
            return new GoStatusResult(GoStatusKind.Unknown, string.Empty);
        }
    }

    internal static GoStatusResult ParseGoStatus(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new GoStatusResult(GoStatusKind.Unknown, string.Empty);
        }

        if (document.RootElement.TryGetProperty("product", out JsonElement product))
        {
            if (product.ValueKind == JsonValueKind.Null)
            {
                return new GoStatusResult(GoStatusKind.NoSubscription, string.Empty);
            }

            if (product.ValueKind != JsonValueKind.String)
            {
                return new GoStatusResult(GoStatusKind.Unknown, string.Empty);
            }

            string normalized = NormalizeSubscriptionTier(product.GetString());
            if (normalized.Length == 0 || string.Equals(normalized, "FREE", StringComparison.Ordinal))
            {
                return new GoStatusResult(GoStatusKind.NoSubscription, string.Empty);
            }

            return new GoStatusResult(GoStatusKind.Subscribed, normalized);
        }

        if (document.RootElement.TryGetProperty("subscribed", out JsonElement subscribed) &&
            subscribed.ValueKind == JsonValueKind.False)
        {
            return new GoStatusResult(GoStatusKind.NoSubscription, string.Empty);
        }

        return new GoStatusResult(GoStatusKind.Unknown, string.Empty);
    }

    internal enum GoStatusKind
    {
        Subscribed,
        NoSubscription,
        Unknown,
    }

    internal readonly record struct GoStatusResult(GoStatusKind Kind, string Plan);

    private static string ReadErrorType(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody) || responseBody.Length > 65_536)
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                return ReadOptionalString(error, "type");
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    private static void ApplyUsageHeaders(HttpRequestMessage request, string token, string? orgId, bool useOpenCodeOrgHeader)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("CodexTray");
        if (!string.IsNullOrWhiteSpace(orgId))
        {
            request.Headers.TryAddWithoutValidation(useOpenCodeOrgHeader ? "x-opencode-org-id" : "x-org-id", orgId);
        }
    }

    private static string ReadOptionalString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
                ? (property.GetString() ?? string.Empty).Trim()
                : string.Empty;
    }

    private sealed record OpenCodeCredential(string Token, string? OrgId, bool IsOAuth);

    private sealed class OpenCodeDataException(string message) : InvalidOperationException(message);

    private sealed class OpenCodeAuthException(string message) : InvalidOperationException(message);
}
