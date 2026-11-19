using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodexTray.Core;

/// <summary>
/// Identifies the fixed, sanitized failure category for a Gemini quota collection attempt.
/// </summary>
public enum GeminiUsageFailureKind
{
    None,
    LoginRequired,
    OwnerRefreshRequired,
    AccessDenied,
    CredentialChanged,
    RateLimited,
    Timeout,
    Network,
    ServiceUnavailable,
    InvalidResponse,
    LocalDataError,
}

/// <summary>
/// Identifies the client application that owns the selected Gemini OAuth credential.
/// </summary>
public enum GeminiCredentialOwner
{
    Unknown,
    AntigravityCli,
    GeminiCli,
    AntigravityIde,
}

/// <summary>
/// Represents the collected quota windows and subscription tier for Gemini and Antigravity partner models.
/// </summary>
public sealed class GeminiUsageSnapshot
{
    public string Source { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }

    public UsageLimit? GeminiFiveHour { get; init; }

    public UsageLimit? GeminiWeekly { get; init; }

    public UsageLimit? ArgonWeekly { get; init; }

    public UsageLimit? PartnerFiveHour { get; init; }

    public UsageLimit? PartnerWeekly { get; init; }

    public string SubscriptionTier { get; init; } = string.Empty;

    public bool IsAvailable { get; init; }
}

/// <summary>
/// Represents a display-ready Gemini usage dashboard result.
/// </summary>
public sealed class GeminiUsageDashboard
{
    public static GeminiUsageDashboard Empty { get; } = new()
    {
        Snapshot = new GeminiUsageSnapshot
        {
            Source = "gemini",
            UpdatedAt = DateTimeOffset.MinValue,
            IsAvailable = false,
        },
        StatusMessage = "Gemini usage is unavailable",
        FailureKind = GeminiUsageFailureKind.None,
        CredentialOwner = GeminiCredentialOwner.Unknown,
    };

    public required GeminiUsageSnapshot Snapshot { get; init; }

    public required string StatusMessage { get; init; }

    public GeminiUsageFailureKind FailureKind { get; init; }

    public GeminiCredentialOwner CredentialOwner { get; init; }
}

/// <summary>
/// Collects Gemini, Argon, and Claude &amp; GPT partner quota limits using Antigravity or Gemini CLI OAuth credentials.
/// </summary>
public sealed partial class GeminiUsageCollector
{
    private const int k_FiveHourWindowMinutes = 300;
    private const int k_WeeklyWindowMinutes = 10080;
    private const int k_CredTypeGeneric = 1;
    private const int k_AgyVersionOutputMaxBytes = 64 * 1024;
    private const int k_AgyUsageOutputMaxBytes = 1024 * 1024;
    private const string k_PrimaryCloudCodeBaseUrl = "https://daily-cloudcode-pa.googleapis.com";
    private const string k_FallbackCloudCodeBaseUrl = "https://cloudcode-pa.googleapis.com";
    private const string k_LoadCodeAssistPath = "/v1internal:loadCodeAssist";
    private const string k_RetrieveQuotaSummaryPath = "/v1internal:retrieveUserQuotaSummary";
    private const string k_DefaultUserAgent = "antigravity/hub/2.24.0";
    private const string k_DefaultTokenRefreshUrl = "https://oauth2.googleapis.com/token";

    private static readonly Version s_MinAgyUsageVersion = new(1, 1, 11);
    private static readonly TimeSpan s_RefreshBuffer = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_AgyVersionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_AgyUsageTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan[] s_BackoffSteps = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];
    private static readonly SemaphoreSlim s_AuthLock = new(1, 1);
    private static readonly JsonSerializerOptions s_WriteJsonOptions = new()
    {
        WriteIndented = true,
    };
    private static readonly HttpClient s_HttpClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly HttpClient m_HttpClient;
    private readonly Func<DateTimeOffset> m_NowProvider;
    private readonly string? m_ExplicitGeminiHomePath;
    private readonly string? m_BaseUrlOverride;
    private readonly string m_TokenRefreshUrl;
    private readonly Func<string?>? m_CredentialStoreReader;
    private readonly bool m_UseSystemCredentialStores;
    private readonly bool m_AllowAgyHelper;
    private readonly string? m_ExplicitAgyExecutablePath;
    private readonly Func<string, string[], TimeSpan, int, CancellationToken, Task<AgyCliResult>>? m_AgyCliRunner;

    private readonly object m_StateLock = new();
    private long m_StateGeneration;
    private BackoffState? m_BackoffState;
    private CachedAgyVersion? m_CachedAgyVersion;

    [GeneratedRegex(@"ya29\.[A-Za-z0-9._\-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleAccessTokenRegex();

    [GeneratedRegex(@"1//[A-Za-z0-9._\-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleRefreshTokenRegex();

    [GeneratedRegex(@"\b(\d+)\.(\d+)\.(\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SemVerRegex();

    /// <summary>
    /// Creates a Gemini usage collector using the default credential discovery paths and HTTP client.
    /// </summary>
    public GeminiUsageCollector()
        : this(s_HttpClient, explicitGeminiHomePath: null, baseUrl: null, tokenRefreshUrl: null, credentialStoreReader: null, nowProvider: null, useSystemCredentialStores: true, allowAgyHelper: true)
    {
    }

    /// <summary>
    /// Creates a Gemini usage collector with explicit dependencies for deterministic tests.
    /// </summary>
    internal GeminiUsageCollector(
        HttpClient httpClient,
        string? explicitGeminiHomePath,
        string? baseUrl = null,
        string? tokenRefreshUrl = null,
        Func<string?>? credentialStoreReader = null,
        Func<DateTimeOffset>? nowProvider = null,
        bool useSystemCredentialStores = false,
        bool allowAgyHelper = false,
        string? agyExecutablePath = null,
        Func<string, string[], TimeSpan, int, CancellationToken, Task<AgyCliResult>>? agyCliRunner = null)
    {
        m_HttpClient = httpClient;
        m_NowProvider = nowProvider ?? (() => DateTimeOffset.Now);
        m_ExplicitGeminiHomePath = explicitGeminiHomePath;
        m_BaseUrlOverride = baseUrl?.TrimEnd('/');
        m_TokenRefreshUrl = string.IsNullOrWhiteSpace(tokenRefreshUrl) ? k_DefaultTokenRefreshUrl : tokenRefreshUrl;
        m_CredentialStoreReader = credentialStoreReader;
        m_UseSystemCredentialStores = useSystemCredentialStores;
        m_AllowAgyHelper = allowAgyHelper || agyCliRunner != null;
        m_ExplicitAgyExecutablePath = string.IsNullOrWhiteSpace(agyExecutablePath) ? null : agyExecutablePath;
        m_AgyCliRunner = agyCliRunner;
    }

    /// <summary>
    /// Clears transient failure backoff and cached CLI metadata without blocking the caller.
    /// </summary>
    public void ResetState()
    {
        lock (m_StateLock)
        {
            m_StateGeneration++;
            m_BackoffState = null;
            m_CachedAgyVersion = null;
        }
    }

    private long ReadStateGeneration()
    {
        lock (m_StateLock)
        {
            return m_StateGeneration;
        }
    }

    /// <summary>
    /// Collects Gemini quota windows and subscription tier for the current local login.
    /// </summary>
    public async Task<GeminiUsageSnapshot> CollectAsync(bool useAbsoluteResetTime = false, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string geminiHome = ResolveGeminiHomePath(m_ExplicitGeminiHomePath);
        long stateGeneration = ReadStateGeneration();

        await s_AuthLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now;
        GeminiCredential credential;
        GeminiCredential sourceCredential;
        bool refreshed = false;
        try
        {
            now = m_NowProvider();
            if (!TryLoadCredential(geminiHome, out credential, out string error, out bool hadLocalReadError))
            {
                ClearBackoff(stateGeneration);
                throw new GeminiDataException(error, hadLocalReadError ? GeminiUsageFailureKind.LocalDataError : GeminiUsageFailureKind.LoginRequired, GeminiCredentialOwner.Unknown);
            }

            BackoffState? activeBackoff = ReadBackoffForSource(credential.SourceKey, stateGeneration);
            if (!forceRefresh && activeBackoff != null && (activeBackoff.WaitForChange || now < activeBackoff.NextAttemptAfter))
            {
                throw new GeminiDataException(activeBackoff.Message, activeBackoff.FailureKind, activeBackoff.Owner);
            }

            sourceCredential = credential;
            bool isActuallyExpired = NeedsRefresh(credential.ExpiresAt, credential.AccessToken, now, TimeSpan.Zero);
            bool inRefreshBuffer = isActuallyExpired || NeedsRefresh(credential.ExpiresAt, credential.AccessToken, now);
            if (inRefreshBuffer)
            {
                RecoveryResult recovery = await RecoverCredentialLockedAsync(geminiHome, credential, now, useAbsoluteResetTime, allowCurrentOnUnavailable: !isActuallyExpired, stateGeneration, cancellationToken).ConfigureAwait(false);
                if (recovery.DirectSnapshot != null)
                {
                    ClearBackoff(stateGeneration);
                    return recovery.DirectSnapshot;
                }

                if (recovery.Credential != null && !ReferenceEquals(recovery.Credential, credential))
                {
                    credential = recovery.Credential;
                    sourceCredential = credential;
                    refreshed = true;
                }
            }
        }
        finally
        {
            s_AuthLock.Release();
        }

        try
        {
            GeminiUsageSnapshot snapshot = await FetchFromCloudCodeAsync(credential, useAbsoluteResetTime, now, cancellationToken).ConfigureAwait(false);
            EnsureCredentialUnchanged(sourceCredential);
            ClearBackoff(stateGeneration);
            return snapshot;
        }
        catch (GeminiAuthException) when (!refreshed && CanAttemptRecovery(credential))
        {
            await s_AuthLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                now = m_NowProvider();
                EnsureCredentialUnchanged(sourceCredential);
                RecoveryResult recovery = await RecoverCredentialLockedAsync(geminiHome, credential, now, useAbsoluteResetTime, allowCurrentOnUnavailable: false, stateGeneration, cancellationToken).ConfigureAwait(false);
                if (recovery.DirectSnapshot != null)
                {
                    ClearBackoff(stateGeneration);
                    return recovery.DirectSnapshot;
                }

                credential = recovery.Credential!;
                sourceCredential = credential;
                refreshed = true;
            }
            finally
            {
                s_AuthLock.Release();
            }

            try
            {
                GeminiUsageSnapshot snapshot = await FetchFromCloudCodeAsync(credential, useAbsoluteResetTime, now, cancellationToken).ConfigureAwait(false);
                EnsureCredentialUnchanged(sourceCredential);
                ClearBackoff(stateGeneration);
                return snapshot;
            }
            catch (GeminiAuthException secondAuth)
            {
                throw RecordAuthRejection(sourceCredential, secondAuth, stateGeneration);
            }
            catch (Exception exception) when (exception is UsageHttpException or HttpRequestException or TaskCanceledException)
            {
                RecordTransientFailure(sourceCredential, exception, stateGeneration);
                throw;
            }
        }
        catch (GeminiAuthException authException)
        {
            throw RecordAuthRejection(sourceCredential, authException, stateGeneration);
        }
        catch (Exception exception) when (exception is UsageHttpException or HttpRequestException or TaskCanceledException)
        {
            RecordTransientFailure(sourceCredential, exception, stateGeneration);
            throw;
        }
    }

    /// <summary>
    /// Collects a display-ready Gemini dashboard without throwing collector-level failures.
    /// </summary>
    public async Task<GeminiUsageDashboard> CollectDashboardAsync(bool useAbsoluteResetTime = false, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        DateTimeOffset now = m_NowProvider();
        try
        {
            GeminiUsageSnapshot snapshot = await CollectAsync(useAbsoluteResetTime, cancellationToken, forceRefresh).ConfigureAwait(false);
            return new GeminiUsageDashboard
            {
                Snapshot = snapshot,
                StatusMessage = string.Empty,
                FailureKind = GeminiUsageFailureKind.None,
                CredentialOwner = GeminiCredentialOwner.Unknown,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is GeminiDataException or GeminiAuthException or UsageHttpException
            or HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
            or IOException or UnauthorizedAccessException or SqliteException or FormatException or OverflowException)
        {
            (GeminiUsageFailureKind failureKind, GeminiCredentialOwner owner, string message) = ClassifyFailure(exception);
            return new GeminiUsageDashboard
            {
                Snapshot = new GeminiUsageSnapshot
                {
                    Source = "gemini",
                    UpdatedAt = now,
                    IsAvailable = false,
                },
                StatusMessage = message,
                FailureKind = failureKind,
                CredentialOwner = owner,
            };
        }
    }

    /// <summary>
    /// Formats a reset label for one Gemini quota window according to absolute or countdown display mode.
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
    /// Parses a retrieveUserQuotaSummary JSON payload into normalized Gemini and partner quota limits.
    /// </summary>
    public static GeminiUsageSnapshot ParseQuotaSummary(
        string responseBody,
        DateTimeOffset now,
        bool useAbsoluteResetTime = false,
        string? fallbackTier = null,
        string source = "cloud_code")
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new GeminiDataException("Could not parse Gemini quota response.");
        }

        UsageLimit? geminiFiveHour = null;
        UsageLimit? geminiWeekly = null;
        UsageLimit? argonWeekly = null;
        UsageLimit? partnerFiveHour = null;
        UsageLimit? partnerWeekly = null;

        IEnumerable<JsonElement> groupContainers = EnumerateCandidateObjects(root);
        foreach (JsonElement container in groupContainers)
        {
            if (container.TryGetProperty("groups", out JsonElement groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement group in groups.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string groupName = ReadFirstString(group, "displayName", "groupName", "name", "id");
                    foreach (string arrayProperty in new[] { "buckets", "limits", "quotas", "quotaBuckets" })
                    {
                        if (!group.TryGetProperty(arrayProperty, out JsonElement buckets) || buckets.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (JsonElement bucket in buckets.EnumerateArray())
                        {
                            AssignBucket(bucket, groupName, now, useAbsoluteResetTime, ref geminiFiveHour, ref geminiWeekly, ref argonWeekly, ref partnerFiveHour, ref partnerWeekly);
                        }
                    }
                }
            }

            foreach (string flatProperty in new[] { "buckets", "limits", "quotas", "quotaBuckets" })
            {
                if (!container.TryGetProperty(flatProperty, out JsonElement flatBuckets) || flatBuckets.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement bucket in flatBuckets.EnumerateArray())
                {
                    AssignBucket(bucket, string.Empty, now, useAbsoluteResetTime, ref geminiFiveHour, ref geminiWeekly, ref argonWeekly, ref partnerFiveHour, ref partnerWeekly);
                }
            }
        }

        if (geminiFiveHour == null && geminiWeekly == null && argonWeekly == null && partnerFiveHour == null && partnerWeekly == null)
        {
            throw new GeminiDataException("Gemini quota API did not return quota buckets.");
        }

        string rawTier = ExtractTierFromObject(root);
        if (string.IsNullOrWhiteSpace(rawTier))
        {
            rawTier = fallbackTier ?? string.Empty;
        }

        return new GeminiUsageSnapshot
        {
            Source = source,
            UpdatedAt = now,
            GeminiFiveHour = geminiFiveHour,
            GeminiWeekly = geminiWeekly,
            ArgonWeekly = argonWeekly,
            PartnerFiveHour = partnerFiveHour,
            PartnerWeekly = partnerWeekly,
            SubscriptionTier = NormalizeSubscriptionTier(rawTier),
            IsAvailable = true,
        };
    }

    /// <summary>
    /// Resolves the root directory that holds Antigravity and Gemini CLI data.
    /// </summary>
    internal static string ResolveGeminiHomePath(string? explicitGeminiHomePath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitGeminiHomePath))
        {
            return explicitGeminiHomePath;
        }

        string? envHome = Environment.GetEnvironmentVariable("GEMINI_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
        {
            return envHome;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
    }

    /// <summary>
    /// Normalizes a Google AI / Antigravity tier identifier into a compact uppercase badge.
    /// </summary>
    public static string NormalizeSubscriptionTier(string? rawTier)
    {
        string normalized = (rawTier ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (normalized.Contains("ultra", StringComparison.OrdinalIgnoreCase))
        {
            return "ULTRA";
        }

        if (normalized.Contains("enterprise", StringComparison.OrdinalIgnoreCase))
        {
            return "ENTERPRISE";
        }

        if (normalized.Contains("business", StringComparison.OrdinalIgnoreCase) || normalized.Contains("team", StringComparison.OrdinalIgnoreCase))
        {
            return "TEAM";
        }

        if (normalized.Contains("pro", StringComparison.OrdinalIgnoreCase) || normalized.Contains("standard", StringComparison.OrdinalIgnoreCase))
        {
            return "PRO";
        }

        if (normalized.Contains("free", StringComparison.OrdinalIgnoreCase) || normalized.Contains("legacy", StringComparison.OrdinalIgnoreCase))
        {
            return "FREE";
        }

        return normalized.Replace('_', ' ').Replace('-', ' ').Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Queries Cloud Code APIs with primary and fallback host URLs.
    /// </summary>
    private async Task<GeminiUsageSnapshot> FetchFromCloudCodeAsync(
        GeminiCredential credential,
        bool useAbsoluteResetTime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string[] baseUrls = !string.IsNullOrWhiteSpace(m_BaseUrlOverride)
            ? [m_BaseUrlOverride]
            : [k_PrimaryCloudCodeBaseUrl, k_FallbackCloudCodeBaseUrl];

        Exception? lastError = null;
        TimeSpan? longestRetryAfter = null;
        foreach (string baseUrl in baseUrls)
        {
            try
            {
                (string projectId, string loadCodeAssistTier, TimeSpan? loadRetryAfter) = await TryLoadCodeAssistAsync(baseUrl, credential.AccessToken, cancellationToken).ConfigureAwait(false);
                longestRetryAfter = MaxDelay(longestRetryAfter, loadRetryAfter);
                string payload = string.IsNullOrWhiteSpace(projectId)
                    ? "{}"
                    : JsonSerializer.Serialize(new { project = projectId });

                using HttpRequestMessage quotaRequest = new(HttpMethod.Post, baseUrl + k_RetrieveQuotaSummaryPath);
                ApplyCloudCodeHeaders(quotaRequest, credential.AccessToken);
                quotaRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using HttpResponseMessage quotaResponse = await UsageHttp.SendAsync(m_HttpClient, quotaRequest, cancellationToken).ConfigureAwait(false);
                if (quotaResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new GeminiAuthException(quotaResponse.StatusCode);
                }

                string body = await quotaResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!quotaResponse.IsSuccessStatusCode)
                {
                    TimeSpan? retryAfter = OAuthTokenHelpers.TryReadRetryAfter(quotaResponse);
                    longestRetryAfter = MaxDelay(longestRetryAfter, retryAfter);
                    throw new UsageHttpException(quotaResponse.StatusCode, retryAfter: retryAfter);
                }

                string tierFallback = !string.IsNullOrWhiteSpace(loadCodeAssistTier) ? loadCodeAssistTier : credential.TierHint;
                return ParseQuotaSummary(body, now, useAbsoluteResetTime, tierFallback, "cloud_code");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (GeminiAuthException)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UsageHttpException or JsonException or GeminiDataException)
            {
                lastError = exception;
            }
        }

        if (longestRetryAfter is TimeSpan retryDelay)
        {
            if (lastError is UsageHttpException lastHttp)
            {
                throw new UsageHttpException(lastHttp.StatusCode, lastHttp.OAuthErrorCode, MaxDelay(lastHttp.RetryAfter, retryDelay));
            }

            throw new UsageHttpException(HttpStatusCode.TooManyRequests, retryAfter: retryDelay);
        }

        throw lastError ?? new GeminiDataException("Gemini quota request failed.", GeminiUsageFailureKind.InvalidResponse, credential.Owner);
    }

    /// <summary>
    /// Calls loadCodeAssist to obtain the active Cloud Code project ID and subscription tier when available.
    /// </summary>
    private async Task<(string ProjectId, string Tier, TimeSpan? RetryAfter)> TryLoadCodeAssistAsync(string baseUrl, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, baseUrl + k_LoadCodeAssistPath);
            ApplyCloudCodeHeaders(request, accessToken);
            request.Content = new StringContent("{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}", Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await UsageHttp.SendAsync(m_HttpClient, request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new GeminiAuthException(response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                TimeSpan? retryAfter = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? OAuthTokenHelpers.TryReadRetryAfter(response)
                    : null;
                return (string.Empty, string.Empty, retryAfter);
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string projectId = ExtractProjectId(root);
            string tier = ExtractTierFromObject(root);
            return (projectId, tier, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GeminiAuthException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (string.Empty, string.Empty, null);
        }
    }

    private static TimeSpan? MaxDelay(TimeSpan? first, TimeSpan? second)
    {
        if (first is not TimeSpan a || a <= TimeSpan.Zero)
        {
            return second is TimeSpan b && b > TimeSpan.Zero ? b : null;
        }

        if (second is not TimeSpan c || c <= TimeSpan.Zero)
        {
            return a;
        }

        return a >= c ? a : c;
    }

    /// <summary>
    /// Applies standard Cloud Code control plane request headers.
    /// </summary>
    private static void ApplyCloudCodeHeaders(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        // Antigravity uses a two-slash User-Agent value that ProductInfoHeaderValue validation rejects.
        request.Headers.TryAddWithoutValidation("User-Agent", k_DefaultUserAgent);
        request.Headers.Accept.ParseAdd("application/json");
    }

    private bool CanAttemptRecovery(GeminiCredential credential)
    {
        return (credential.SourceFilePath != null && TryResolveCompleteClientPair(credential, out _, out _) && !string.IsNullOrWhiteSpace(credential.RefreshToken))
            || credential.CanInvokeAgyHelper;
    }

    /// <summary>
    /// Refreshes a writable local file credential or delegates expired Antigravity CLI logins to the owning CLI.
    /// </summary>
    private async Task<RecoveryResult> RecoverCredentialLockedAsync(
        string geminiHome,
        GeminiCredential credential,
        DateTimeOffset now,
        bool useAbsoluteResetTime,
        bool allowCurrentOnUnavailable,
        long stateGeneration,
        CancellationToken cancellationToken)
    {
        EnsureCredentialUnchanged(credential);
        bool canKeepCurrent = allowCurrentOnUnavailable && !NeedsRefresh(credential.ExpiresAt, credential.AccessToken, now, TimeSpan.Zero);

        // External stores (Credential Manager / IDE SQLite) are never refreshed via direct token POST to avoid unpersisted rotation.
        if (credential.SourceFilePath == null)
        {
            if (canKeepCurrent)
            {
                return new RecoveryResult(credential, null);
            }

            if (credential.CanInvokeAgyHelper)
            {
                return await TryRecoverWithAgyLockedAsync(geminiHome, credential, now, useAbsoluteResetTime, stateGeneration, cancellationToken).ConfigureAwait(false);
            }

            throw RecordPermanentBackoff(credential, GeminiUsageFailureKind.OwnerRefreshRequired, FormatOwnerRefreshMessage(credential.Owner), stateGeneration);
        }

        bool hasRefreshToken = !string.IsNullOrWhiteSpace(credential.RefreshToken);
        bool hasClientPair = TryResolveCompleteClientPair(credential, out string? clientId, out string? clientSecret);
        bool supportsWriteback = hasRefreshToken && hasClientPair && CanWriteCredentialFileSafely(credential);
        if (!hasRefreshToken || !hasClientPair || !supportsWriteback)
        {
            EnsureCredentialUnchanged(credential);
            if (canKeepCurrent)
            {
                return new RecoveryResult(credential, null);
            }

            if (credential.CanInvokeAgyHelper)
            {
                return await TryRecoverWithAgyLockedAsync(geminiHome, credential, now, useAbsoluteResetTime, stateGeneration, cancellationToken).ConfigureAwait(false);
            }

            if (!hasRefreshToken || !hasClientPair)
            {
                string reason = !hasRefreshToken
                    ? FormatOwnerRefreshMessage(credential.Owner)
                    : "OAuth client configuration is incomplete. " + FormatOwnerRefreshMessage(credential.Owner);
                throw RecordPermanentBackoff(credential, GeminiUsageFailureKind.OwnerRefreshRequired, reason, stateGeneration);
            }

            // A writeback probe can fail temporarily while another process holds the credential file.
            throw RecordTransientBackoff(
                credential,
                GeminiUsageFailureKind.OwnerRefreshRequired,
                "Gemini OAuth file cannot be updated safely. " + FormatOwnerRefreshMessage(credential.Owner),
                now,
                stateGeneration);
        }

        OAuthTokenResponse refreshed;
        try
        {
            refreshed = await OAuthTokenHelpers.RefreshAsync(
                m_HttpClient,
                m_TokenRefreshUrl,
                clientId!,
                credential.RefreshToken!,
                "Gemini",
                cancellationToken,
                clientSecret).ConfigureAwait(false);
        }
        catch (UsageHttpException httpException) when (httpException.OAuthErrorCode is "invalid_grant" or "invalid_client" or "unauthorized_client")
        {
            throw RecordPermanentBackoff(credential, GeminiUsageFailureKind.OwnerRefreshRequired, FormatOwnerRefreshMessage(credential.Owner), stateGeneration);
        }
        catch (Exception exception) when (exception is UsageHttpException or HttpRequestException or TaskCanceledException)
        {
            RecordTransientBackoff(credential, exception, now, stateGeneration);
            throw;
        }

        EnsureCredentialUnchanged(credential);

        DateTimeOffset expiresAt = refreshed.ExpiresInSeconds is double seconds
            ? now.AddSeconds(seconds)
            : OAuthTokenHelpers.TryGetJwtExpiry(refreshed.AccessToken, out DateTimeOffset jwtExpiry)
                ? jwtExpiry
                : now.AddHours(1);

        GeminiCredential updated = credential with
        {
            AccessToken = refreshed.AccessToken,
            RefreshToken = refreshed.RefreshToken,
            ExpiresAt = expiresAt,
        };

        updated = SaveUpdatedCredentialFile(credential, updated);
        return new RecoveryResult(updated, null);
    }

    private static bool TryResolveCompleteClientPair(GeminiCredential credential, out string? clientId, out string? clientSecret)
    {
        clientId = credential.ClientId;
        clientSecret = credential.ClientSecret;
        if (clientId == null && clientSecret == null)
        {
            string prefix = credential.PreferGeminiCliClient ? "CODEXTRAY_GEMINI_CLI" : "CODEXTRAY_ANTIGRAVITY";
            clientId = Environment.GetEnvironmentVariable(prefix + "_CLIENT_ID");
            clientSecret = Environment.GetEnvironmentVariable(prefix + "_CLIENT_SECRET");
        }

        return !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);
    }

    private static bool CanWriteCredentialFileSafely(GeminiCredential credential)
    {
        if (credential.SourceFilePath == null || credential.SourceFileContents == null)
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(credential.SourceFileContents) is JsonObject
                && AtomicFile.CanReplaceAllText(credential.SourceFilePath, credential.SourceFileContents);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the supported read-only Antigravity CLI usage command so agy renews its own credential store.
    /// </summary>
    private async Task<RecoveryResult> TryRecoverWithAgyLockedAsync(
        string geminiHome,
        GeminiCredential credential,
        DateTimeOffset now,
        bool useAbsoluteResetTime,
        long stateGeneration,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(credential.AuthMethod)
            && !string.Equals(credential.AuthMethod, "consumer", StringComparison.OrdinalIgnoreCase))
        {
            throw RecordPermanentBackoff(credential, GeminiUsageFailureKind.OwnerRefreshRequired, FormatOwnerRefreshMessage(credential.Owner), stateGeneration);
        }

        if (!AreAgySlashCommandsEnabled(geminiHome))
        {
            throw RecordPermanentBackoff(credential, GeminiUsageFailureKind.OwnerRefreshRequired, FormatOwnerRefreshMessage(credential.Owner), stateGeneration);
        }

        if (!TryResolveAgyExecutablePath(out string agyExecutablePath))
        {
            throw RecordPermanentBackoff(credential, GeminiUsageFailureKind.OwnerRefreshRequired, FormatOwnerRefreshMessage(credential.Owner), stateGeneration);
        }

        AgyVersionProbeStatus versionStatus = await CheckAgyVersionSupportedLockedAsync(agyExecutablePath, stateGeneration, cancellationToken).ConfigureAwait(false);
        if (versionStatus == AgyVersionProbeStatus.TimedOut)
        {
            throw RecordTransientBackoff(credential, GeminiUsageFailureKind.Timeout, "Antigravity CLI timed out while checking version.", now, stateGeneration);
        }

        if (versionStatus == AgyVersionProbeStatus.ProbeFailed)
        {
            throw RecordTransientBackoff(credential, GeminiUsageFailureKind.OwnerRefreshRequired, FormatOwnerRefreshMessage(credential.Owner), now, stateGeneration);
        }

        if (versionStatus != AgyVersionProbeStatus.Supported)
        {
            throw RecordPermanentBackoff(credential, GeminiUsageFailureKind.OwnerRefreshRequired, FormatOwnerRefreshMessage(credential.Owner), stateGeneration);
        }

        AgyCliResult usageResult = await RunAgyCliAsync(
            agyExecutablePath,
            ["-p", "/usage", "--output-format", "json"],
            s_AgyUsageTimeout,
            k_AgyUsageOutputMaxBytes,
            cancellationToken).ConfigureAwait(false);

        if (usageResult.TimedOut)
        {
            throw RecordTransientBackoff(credential, GeminiUsageFailureKind.Timeout, "Antigravity CLI timed out while refreshing login.", now, stateGeneration);
        }

        if (!TryLoadCredential(geminiHome, out GeminiCredential reloaded, out _, out _))
        {
            ClearBackoff(stateGeneration);
            throw new GeminiDataException("Gemini OAuth credentials changed. Refresh again.", GeminiUsageFailureKind.CredentialChanged, credential.Owner);
        }

        if (reloaded.SourceKind != credential.SourceKind || !string.Equals(reloaded.SourceLocation, credential.SourceLocation, StringComparison.Ordinal))
        {
            ClearBackoff(stateGeneration);
            throw new GeminiDataException("Gemini OAuth credentials changed. Refresh again.", GeminiUsageFailureKind.CredentialChanged, reloaded.Owner);
        }

        DateTimeOffset postRunNow = m_NowProvider();
        if (!string.Equals(reloaded.RawFingerprint, credential.RawFingerprint, StringComparison.Ordinal))
        {
            if (!NeedsRefresh(reloaded.ExpiresAt, reloaded.AccessToken, postRunNow, TimeSpan.Zero))
            {
                return new RecoveryResult(reloaded, null);
            }

            throw RecordTransientBackoff(reloaded, GeminiUsageFailureKind.OwnerRefreshRequired, FormatOwnerRefreshMessage(reloaded.Owner), postRunNow, stateGeneration);
        }

        // Only accept stdout fallback when the active source is the unambiguous Credential Manager login (never a stale local file when agy may use Credential Manager).
        bool canUseStdoutFallback = credential.SourceKind is GeminiCredentialSourceKind.WindowsCredentialManager or GeminiCredentialSourceKind.InjectedStore;
        if (canUseStdoutFallback && usageResult.ExitCode == 0 && !usageResult.OutputTruncated
            && TryParseAgyUsageReport(usageResult.StdOut, postRunNow, useAbsoluteResetTime, fallbackTier: null, out GeminiUsageSnapshot cliSnapshot))
        {
            EnsureCredentialUnchanged(credential);
            return new RecoveryResult(null, cliSnapshot);
        }

        throw RecordTransientBackoff(credential, GeminiUsageFailureKind.OwnerRefreshRequired, FormatOwnerRefreshMessage(credential.Owner), postRunNow, stateGeneration);
    }

    private bool TryResolveAgyExecutablePath(out string executablePath)
    {
        if (m_ExplicitAgyExecutablePath != null)
        {
            executablePath = m_ExplicitAgyExecutablePath;
            return m_AgyCliRunner != null || IsTrustedLocalExecutable(executablePath);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            executablePath = string.Empty;
            return false;
        }

        string candidate = Path.Combine(localAppData, "agy", "bin", "agy.exe");
        if (m_AgyCliRunner != null || IsTrustedLocalExecutable(candidate))
        {
            executablePath = candidate;
            return true;
        }

        executablePath = string.Empty;
        return false;
    }

    internal static bool IsTrustedLocalExecutable(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                return false;
            }

            AppUpdatePaths.EnsureSafePath(path);
            return true;
        }
        catch (Exception exception) when (exception is AppUpdateException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static bool AreAgySlashCommandsEnabled(string geminiHome)
    {
        string settingsPath = Path.Combine(geminiHome, "antigravity-cli", "settings.json");
        if (!File.Exists(settingsPath))
        {
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (string propertyName in new[] { "disableSlashCommands", "disable_slash_commands" })
            {
                if (document.RootElement.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.True)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private async Task<AgyVersionProbeStatus> CheckAgyVersionSupportedLockedAsync(string agyExecutablePath, long stateGeneration, CancellationToken cancellationToken)
    {
        DateTime lastWriteUtc = DateTime.MinValue;
        long length = -1;
        if (File.Exists(agyExecutablePath))
        {
            try
            {
                FileInfo info = new(agyExecutablePath);
                lastWriteUtc = info.LastWriteTimeUtc;
                length = info.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return AgyVersionProbeStatus.ProbeFailed;
            }
        }

        CachedAgyVersion? cached;
        lock (m_StateLock)
        {
            cached = m_CachedAgyVersion;
        }

        if (cached != null
            && string.Equals(cached.Path, agyExecutablePath, StringComparison.OrdinalIgnoreCase)
            && cached.LastWriteUtc == lastWriteUtc
            && cached.Length == length)
        {
            return cached.Version >= s_MinAgyUsageVersion
                ? AgyVersionProbeStatus.Supported
                : AgyVersionProbeStatus.UnsupportedVersion;
        }

        AgyCliResult versionResult = await RunAgyCliAsync(
            agyExecutablePath,
            ["--version"],
            s_AgyVersionTimeout,
            k_AgyVersionOutputMaxBytes,
            cancellationToken).ConfigureAwait(false);

        if (versionResult.TimedOut)
        {
            return AgyVersionProbeStatus.TimedOut;
        }

        if (versionResult.ExitCode != 0 || versionResult.OutputTruncated)
        {
            return AgyVersionProbeStatus.ProbeFailed;
        }

        Version? parsed = TryParseSemVer(versionResult.StdOut);
        if (parsed == null)
        {
            return AgyVersionProbeStatus.ProbeFailed;
        }

        lock (m_StateLock)
        {
            if (m_StateGeneration == stateGeneration)
            {
                m_CachedAgyVersion = new CachedAgyVersion(agyExecutablePath, lastWriteUtc, length, parsed);
            }
        }

        return parsed >= s_MinAgyUsageVersion
            ? AgyVersionProbeStatus.Supported
            : AgyVersionProbeStatus.UnsupportedVersion;
    }

    internal static Version? TryParseSemVer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match match = SemVerRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            && int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch)
            ? new Version(major, minor, patch)
            : null;
    }

    internal static bool TryParseAgyUsageReport(
        string? stdout,
        DateTimeOffset now,
        bool useAbsoluteResetTime,
        string? fallbackTier,
        out GeminiUsageSnapshot snapshot)
    {
        snapshot = default!;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stdout);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("status", out JsonElement status)
                || status.ValueKind != JsonValueKind.String
                || !string.Equals(status.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (root.TryGetProperty("is_error", out JsonElement isError) && isError.ValueKind == JsonValueKind.True)
            {
                return false;
            }

            if (!root.TryGetProperty("num_turns", out JsonElement numTurns)
                || numTurns.ValueKind != JsonValueKind.Number
                || !numTurns.TryGetInt32(out int turnCount)
                || turnCount != 0)
            {
                return false;
            }

            if (!root.TryGetProperty("command", out JsonElement command) || command.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!command.TryGetProperty("name", out JsonElement commandName)
                || commandName.ValueKind != JsonValueKind.String
                || !string.Equals(commandName.GetString(), "usage", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!command.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("groups", out JsonElement groups)
                || groups.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            snapshot = ParseQuotaSummary(data.GetRawText(), now, useAbsoluteResetTime, fallbackTier, "agy_cli");
            return snapshot.IsAvailable;
        }
        catch (Exception exception) when (exception is JsonException or GeminiDataException)
        {
            return false;
        }
    }

    private Task<AgyCliResult> RunAgyCliAsync(
        string executablePath,
        string[] arguments,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        if (m_AgyCliRunner != null)
        {
            return m_AgyCliRunner(executablePath, arguments, timeout, maxOutputBytes, cancellationToken);
        }

        return ExecuteAgyProcessAsync(executablePath, arguments, timeout, maxOutputBytes, cancellationToken);
    }

    internal static async Task<AgyCliResult> ExecuteAgyProcessAsync(
        string executablePath,
        string[] arguments,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new AgyCliResult(-1, string.Empty, false, false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new AgyCliResult(-1, string.Empty, false, false);
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        using StreamReader stdOutReader = process.StandardOutput;
        using StreamReader stdErrReader = process.StandardError;
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        int[] remainingBytes = [maxOutputBytes];
        int truncatedFlag = 0;
        Task<string> stdoutTask = ReadBoundedStreamAsync(stdOutReader, remainingBytes, () => Volatile.Write(ref truncatedFlag, 1), timeoutCts.Token);
        Task stderrTask = DrainBoundedStreamAsync(stdErrReader, remainingBytes, () => Volatile.Write(ref truncatedFlag, 1), timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            return new AgyCliResult(process.ExitCode, stdout, false, Volatile.Read(ref truncatedFlag) != 0);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await AbortAndCleanupProcessAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            return new AgyCliResult(-1, string.Empty, true, false);
        }
        catch (OperationCanceledException)
        {
            await AbortAndCleanupProcessAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task AbortAndCleanupProcessAsync(Process process, Task stdoutTask, Task stderrTask)
    {
        TryKillProcessTree(process);
        using CancellationTokenSource cleanupCts = new(TimeSpan.FromSeconds(2));
        try
        {
            await process.WaitForExitAsync(cleanupCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        await ObserveStreamCleanupAsync(stdoutTask, cleanupCts.Token).ConfigureAwait(false);
        await ObserveStreamCleanupAsync(stderrTask, cleanupCts.Token).ConfigureAwait(false);
    }

    private static async Task ObserveStreamCleanupAsync(Task streamTask, CancellationToken cleanupToken)
    {
        try
        {
            await streamTask.WaitAsync(cleanupToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static async Task<string> ReadBoundedStreamAsync(StreamReader reader, int[] remainingBytes, Action onTruncated, CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        char[] buffer = new char[4096];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            int byteCount = Encoding.UTF8.GetByteCount(buffer, 0, count);
            if (Interlocked.Add(ref remainingBytes[0], -byteCount) < 0)
            {
                onTruncated();
                continue;
            }

            builder.Append(buffer, 0, count);
        }

        return builder.ToString();
    }

    private static async Task DrainBoundedStreamAsync(StreamReader reader, int[] remainingBytes, Action onTruncated, CancellationToken cancellationToken)
    {
        char[] buffer = new char[2048];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            int byteCount = Encoding.UTF8.GetByteCount(buffer, 0, count);
            if (Interlocked.Add(ref remainingBytes[0], -byteCount) < 0)
            {
                onTruncated();
            }
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private BackoffState? ReadBackoffForSource(string sourceKey, long stateGeneration)
    {
        lock (m_StateLock)
        {
            if (m_StateGeneration != stateGeneration || m_BackoffState == null)
            {
                return null;
            }

            if (!string.Equals(m_BackoffState.SourceKey, sourceKey, StringComparison.Ordinal))
            {
                m_BackoffState = null;
                return null;
            }

            return m_BackoffState;
        }
    }

    private void ClearBackoff(long stateGeneration)
    {
        lock (m_StateLock)
        {
            if (m_StateGeneration == stateGeneration)
            {
                m_BackoffState = null;
            }
        }
    }

    private GeminiDataException RecordAuthRejection(GeminiCredential credential, GeminiAuthException authException, long stateGeneration)
    {
        if (authException.StatusCode == HttpStatusCode.Forbidden)
        {
            return RecordPermanentBackoff(credential, GeminiUsageFailureKind.AccessDenied, "Gemini quota access was denied (HTTP 403).", stateGeneration);
        }

        return RecordPermanentBackoff(credential, GeminiUsageFailureKind.OwnerRefreshRequired, FormatOwnerRefreshMessage(credential.Owner), stateGeneration);
    }

    private void RecordTransientFailure(GeminiCredential credential, Exception exception, long stateGeneration)
    {
        RecordTransientBackoff(credential, exception, m_NowProvider(), stateGeneration);
    }

    private GeminiDataException RecordPermanentBackoff(GeminiCredential credential, GeminiUsageFailureKind failureKind, string message, long stateGeneration)
    {
        lock (m_StateLock)
        {
            if (m_StateGeneration == stateGeneration)
            {
                m_BackoffState = new BackoffState(credential.SourceKey, failureKind, credential.Owner, message, DateTimeOffset.MaxValue, WaitForChange: true, ConsecutiveFailures: 1);
            }
        }

        return new GeminiDataException(message, failureKind, credential.Owner);
    }

    private GeminiDataException RecordTransientBackoff(GeminiCredential credential, GeminiUsageFailureKind failureKind, string message, DateTimeOffset now, long stateGeneration, TimeSpan? explicitDelay = null)
    {
        lock (m_StateLock)
        {
            if (m_StateGeneration == stateGeneration)
            {
                int failures = m_BackoffState != null && string.Equals(m_BackoffState.SourceKey, credential.SourceKey, StringComparison.Ordinal) && !m_BackoffState.WaitForChange
                    ? m_BackoffState.ConsecutiveFailures + 1
                    : 1;
                TimeSpan delay = explicitDelay is TimeSpan serverDelay && serverDelay > TimeSpan.Zero
                    ? serverDelay
                    : s_BackoffSteps[Math.Min(failures - 1, s_BackoffSteps.Length - 1)];
                m_BackoffState = new BackoffState(credential.SourceKey, failureKind, credential.Owner, message, now + delay, WaitForChange: false, ConsecutiveFailures: failures);
            }
        }

        return new GeminiDataException(message, failureKind, credential.Owner);
    }

    private void RecordTransientBackoff(GeminiCredential credential, Exception exception, DateTimeOffset now, long stateGeneration)
    {
        (GeminiUsageFailureKind failureKind, _, string message) = ClassifyFailure(exception, credential.Owner);
        TimeSpan? explicitDelay = exception is UsageHttpException { RetryAfter: TimeSpan retryAfter } && retryAfter > TimeSpan.Zero
            ? retryAfter
            : null;
        RecordTransientBackoff(credential, failureKind, message, now, stateGeneration, explicitDelay);
    }

    private static (GeminiUsageFailureKind FailureKind, GeminiCredentialOwner Owner, string Message) ClassifyFailure(Exception exception, GeminiCredentialOwner fallbackOwner = GeminiCredentialOwner.Unknown)
    {
        return exception switch
        {
            GeminiDataException dataException => (dataException.FailureKind, dataException.Owner != GeminiCredentialOwner.Unknown ? dataException.Owner : fallbackOwner, dataException.Message),
            GeminiAuthException authException when authException.StatusCode == HttpStatusCode.Forbidden => (GeminiUsageFailureKind.AccessDenied, fallbackOwner, "Gemini quota access was denied (HTTP 403)."),
            GeminiAuthException => (GeminiUsageFailureKind.OwnerRefreshRequired, fallbackOwner, FormatOwnerRefreshMessage(fallbackOwner)),
            UsageHttpException { StatusCode: HttpStatusCode.TooManyRequests } http => (GeminiUsageFailureKind.RateLimited, fallbackOwner, http.Message),
            UsageHttpException http when (int)http.StatusCode >= 500 => (GeminiUsageFailureKind.ServiceUnavailable, fallbackOwner, http.Message),
            UsageHttpException http when http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (GeminiUsageFailureKind.AccessDenied, fallbackOwner, http.Message),
            UsageHttpException http => (GeminiUsageFailureKind.InvalidResponse, fallbackOwner, http.Message),
            TaskCanceledException => (GeminiUsageFailureKind.Timeout, fallbackOwner, UsageDiagnostics.Error(exception)),
            HttpRequestException => (GeminiUsageFailureKind.Network, fallbackOwner, UsageDiagnostics.Error(exception)),
            IOException or UnauthorizedAccessException or SqliteException => (GeminiUsageFailureKind.LocalDataError, fallbackOwner, UsageDiagnostics.Error(exception)),
            _ => (GeminiUsageFailureKind.InvalidResponse, fallbackOwner, UsageDiagnostics.Error(exception)),
        };
    }

    private static string FormatOwnerRefreshMessage(GeminiCredentialOwner owner) => owner switch
    {
        GeminiCredentialOwner.AntigravityCli => "Open Antigravity CLI (agy) to renew your Gemini login, then refresh.",
        GeminiCredentialOwner.GeminiCli => "Open Gemini CLI to renew your Gemini login, then refresh.",
        GeminiCredentialOwner.AntigravityIde => "Open Antigravity IDE to renew your Gemini login, then refresh.",
        _ => "Renew your login in Antigravity or Gemini CLI, then refresh.",
    };

    /// <summary>
    /// Persists refreshed OAuth tokens atomically back to a writable local JSON credential file.
    /// </summary>
    private static GeminiCredential SaveUpdatedCredentialFile(GeminiCredential original, GeminiCredential updated)
    {
        string filePath = original.SourceFilePath!;
        string rawText = File.ReadAllText(filePath);
        if (!string.Equals(rawText, original.SourceFileContents, StringComparison.Ordinal))
        {
            throw new GeminiDataException("Gemini OAuth credentials changed. Refresh again.", GeminiUsageFailureKind.CredentialChanged, original.Owner);
        }

        JsonObject rootObject = JsonNode.Parse(rawText) as JsonObject
            ?? throw new GeminiDataException("Gemini OAuth file has an invalid format.", GeminiUsageFailureKind.LocalDataError, original.Owner);

        JsonObject tokenObject = rootObject["token"] as JsonObject ?? rootObject;
        tokenObject[tokenObject.ContainsKey("accessToken") && !tokenObject.ContainsKey("access_token") ? "accessToken" : "access_token"] = updated.AccessToken;
        if (!string.IsNullOrWhiteSpace(updated.RefreshToken))
        {
            tokenObject[tokenObject.ContainsKey("refreshToken") && !tokenObject.ContainsKey("refresh_token") ? "refreshToken" : "refresh_token"] = updated.RefreshToken;
        }

        if (updated.ExpiresAt is DateTimeOffset expiresAt)
        {
            if (tokenObject.ContainsKey("expiry"))
            {
                tokenObject["expiry"] = expiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            }
            else
            {
                tokenObject["expiry_date"] = expiresAt.ToUnixTimeMilliseconds();
            }
        }

        string contents = rootObject.ToJsonString(s_WriteJsonOptions);
        if (!AtomicFile.TryReplaceAllText(filePath, rawText, contents))
        {
            throw new GeminiDataException("Gemini OAuth credentials changed. Refresh again.", GeminiUsageFailureKind.CredentialChanged, original.Owner);
        }

        return updated with
        {
            SourceFileContents = contents,
            RawFingerprint = ComputeFingerprint(contents),
        };
    }

    private void EnsureCredentialUnchanged(GeminiCredential original)
    {
        if (!TryLoadCredential(ResolveGeminiHomePath(m_ExplicitGeminiHomePath), out GeminiCredential current, out _, out _)
            || current.SourceKind != original.SourceKind
            || !string.Equals(current.SourceLocation, original.SourceLocation, StringComparison.Ordinal)
            || !string.Equals(current.RawFingerprint, original.RawFingerprint, StringComparison.Ordinal)
            || (original.SourceFilePath != null && !string.Equals(current.SourceFileContents, original.SourceFileContents, StringComparison.Ordinal)))
        {
            throw new GeminiDataException("Gemini OAuth credentials changed. Refresh again.", GeminiUsageFailureKind.CredentialChanged, original.Owner);
        }
    }

    /// <summary>
    /// Searches local files, Windows Credential Manager, and Antigravity IDE state storage for OAuth credentials.
    /// </summary>
    private bool TryLoadCredential(string geminiHome, out GeminiCredential credential, out string error, out bool hadLocalReadError)
    {
        credential = default!;
        hadLocalReadError = false;
        bool isDefaultProfile = string.IsNullOrWhiteSpace(m_ExplicitGeminiHomePath)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_HOME"));

        (string Path, bool PreferGeminiCliClient, GeminiCredentialOwner Owner, bool CanInvokeAgyHelper)[] candidateFiles =
        [
            (Path.Combine(geminiHome, "antigravity-cli", "antigravity-oauth-token"), false, GeminiCredentialOwner.AntigravityCli, m_AllowAgyHelper && (isDefaultProfile || m_AgyCliRunner != null)),
            (Path.Combine(geminiHome, "oauth_creds.json"), true, GeminiCredentialOwner.GeminiCli, false),
            (Path.Combine(geminiHome, "antigravity", "oauth_creds.json"), false, GeminiCredentialOwner.AntigravityCli, false),
        ];

        foreach ((string candidateFile, bool preferGeminiCliClient, GeminiCredentialOwner owner, bool canInvokeAgyHelper) in candidateFiles)
        {
            if (!File.Exists(candidateFile))
            {
                continue;
            }

            try
            {
                string contents = File.ReadAllText(candidateFile);
                if (TryParseCredentialPayload(contents, candidateFile, preferGeminiCliClient, out credential))
                {
                    credential = credential with
                    {
                        SourceFileContents = contents,
                        SourceKind = GeminiCredentialSourceKind.LocalFile,
                        Owner = owner,
                        SourceLocation = candidateFile,
                        RawFingerprint = ComputeFingerprint(contents),
                        CanInvokeAgyHelper = canInvokeAgyHelper,
                    };
                    error = string.Empty;
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                hadLocalReadError = true;
            }
        }

        if (m_CredentialStoreReader != null)
        {
            string? injected = m_CredentialStoreReader();
            if (!string.IsNullOrWhiteSpace(injected) && TryParseCredentialPayload(injected, sourceFilePath: null, preferGeminiCliClient: false, out credential))
            {
                credential = credential with
                {
                    SourceKind = GeminiCredentialSourceKind.InjectedStore,
                    Owner = GeminiCredentialOwner.AntigravityCli,
                    SourceLocation = "credential-store:gemini:antigravity",
                    RawFingerprint = ComputeFingerprint(injected),
                    CanInvokeAgyHelper = m_AllowAgyHelper,
                };
                error = string.Empty;
                return true;
            }
        }
        else if (m_UseSystemCredentialStores && OperatingSystem.IsWindows())
        {
            foreach (string target in new[] { "gemini:antigravity", "gemini-cli" })
            {
                string? secretBlob = TryReadWindowsGenericCredential(target);
                bool isGeminiCli = target == "gemini-cli";
                if (!string.IsNullOrWhiteSpace(secretBlob) && TryParseCredentialPayload(secretBlob, sourceFilePath: null, preferGeminiCliClient: isGeminiCli, out credential))
                {
                    credential = credential with
                    {
                        SourceKind = GeminiCredentialSourceKind.WindowsCredentialManager,
                        Owner = isGeminiCli ? GeminiCredentialOwner.GeminiCli : GeminiCredentialOwner.AntigravityCli,
                        SourceLocation = "wincred:" + target,
                        RawFingerprint = ComputeFingerprint(secretBlob),
                        CanInvokeAgyHelper = !isGeminiCli && m_AllowAgyHelper && isDefaultProfile,
                    };
                    error = string.Empty;
                    return true;
                }
            }
        }

        if (m_UseSystemCredentialStores)
        {
            if (TryReadAntigravityStateDatabase(out credential, out bool ideReadError))
            {
                error = string.Empty;
                return true;
            }

            if (ideReadError)
            {
                hadLocalReadError = true;
            }
        }

        error = hadLocalReadError
            ? "Local Gemini credentials could not be read."
            : "Gemini OAuth credentials unavailable. Sign in with Antigravity or gemini first.";
        return false;
    }

    private static string ComputeFingerprint(string raw)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Attempts to read an OAuth credential blob from Windows Credential Manager.
    /// </summary>
    private static string? TryReadWindowsGenericCredential(string targetName)
    {
        if (!CredReadW(targetName, k_CredTypeGeneric, 0, out nint credentialPtr) || credentialPtr == 0)
        {
            return null;
        }

        try
        {
            WindowsCredential credential = Marshal.PtrToStructure<WindowsCredential>(credentialPtr);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0 || credential.CredentialBlobSize > 1024 * 1024)
            {
                return null;
            }

            byte[] bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return DecodeWindowsCredential(bytes);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    internal static string DecodeWindowsCredential(byte[] bytes)
    {
        string utf8 = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        return (bytes.Length % 2 == 0 && utf8.Contains('\0') ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : utf8).TrimStart('\uFEFF');
    }

    /// <summary>
    /// Reads the Antigravity IDE globalStorage SQLite database for a synced Google OAuth token.
    /// </summary>
    private static bool TryReadAntigravityStateDatabase(out GeminiCredential credential, out bool hadReadError)
    {
        credential = default!;
        hadReadError = false;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            return false;
        }

        foreach (string folderName in new[] { "Antigravity", "Antigravity IDE" })
        {
            string databasePath = Path.Combine(appData, folderName, "User", "globalStorage", "state.vscdb");
            if (!File.Exists(databasePath))
            {
                continue;
            }

            try
            {
                using SqliteConnection connection = new(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 1,
                }.ToString());
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM ItemTable WHERE key = 'antigravityUnifiedStateSync.oauthToken'";
                if (command.ExecuteScalar() is string stored && !string.IsNullOrWhiteSpace(stored)
                    && TryParseCredentialPayload(stored, sourceFilePath: null, preferGeminiCliClient: false, out credential))
                {
                    credential = credential with
                    {
                        SourceKind = GeminiCredentialSourceKind.AntigravityIdeDatabase,
                        Owner = GeminiCredentialOwner.AntigravityIde,
                        SourceLocation = $"sqlite:{databasePath}#antigravityUnifiedStateSync.oauthToken",
                        RawFingerprint = ComputeFingerprint(stored),
                        CanInvokeAgyHelper = false,
                    };
                    return true;
                }
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                hadReadError = true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses JSON, Base64-wrapped JSON, or protobuf Base64 blobs into OAuth credential fields.
    /// </summary>
    internal static bool TryParseCredentialPayload(
        string payload,
        string? sourceFilePath,
        bool preferGeminiCliClient,
        out GeminiCredential credential)
    {
        credential = default!;
        string trimmed = payload.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (TryParseJsonCredential(trimmed, sourceFilePath, preferGeminiCliClient, out credential))
        {
            return true;
        }

        string candidate = trimmed;
        for (int pass = 0; pass < 2; pass++)
        {
            Match accessMatch = GoogleAccessTokenRegex().Match(candidate);
            if (accessMatch.Success)
            {
                Match refreshMatch = GoogleRefreshTokenRegex().Match(candidate);
                credential = new GeminiCredential(
                    SourceFilePath: sourceFilePath,
                    AccessToken: accessMatch.Value,
                    RefreshToken: refreshMatch.Success ? refreshMatch.Value : null,
                    ExpiresAt: null,
                    ClientId: null,
                    ClientSecret: null,
                    TierHint: string.Empty,
                    PreferGeminiCliClient: preferGeminiCliClient);
                return true;
            }

            if (!TryDecodeBase64(candidate, out byte[] decodedBytes))
            {
                break;
            }

            // IDE storage wraps OAuthTokenInfo in Topic -> Entry -> Row -> base64 payload.
            ReadOnlySpan<byte> entry = ProtobufFields.ReadBytes(decodedBytes, 1);
            ReadOnlySpan<byte> row = ProtobufFields.ReadBytes(entry, 2);
            ReadOnlySpan<byte> encodedToken = ProtobufFields.ReadBytes(row, 1);
            if (!encodedToken.IsEmpty && TryDecodeBase64(Encoding.UTF8.GetString(encodedToken), out byte[] tokenBytes))
            {
                decodedBytes = tokenBytes;
            }

            string decoded = Encoding.UTF8.GetString(decodedBytes);
            if (TryParseJsonCredential(decoded, sourceFilePath, preferGeminiCliClient, out credential))
            {
                return true;
            }

            candidate = decoded;
        }

        return false;
    }

    /// <summary>
    /// Parses a JSON OAuth credential object.
    /// </summary>
    private static bool TryParseJsonCredential(
        string json,
        string? sourceFilePath,
        bool preferGeminiCliClient,
        out GeminiCredential credential)
    {
        credential = default!;
        if (!json.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            JsonElement token = root.TryGetProperty("token", out JsonElement nestedToken) && nestedToken.ValueKind == JsonValueKind.Object ? nestedToken : root;
            string accessToken = ReadFirstString(token, "access_token", "accessToken", "token");
            string refreshToken = ReadFirstString(token, "refresh_token", "refreshToken");
            if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(refreshToken))
            {
                return false;
            }

            DateTimeOffset? expiresAt = ParseExpiryTimestamp(token);
            string clientId = ReadFirstString(root, "client_id", "clientId");
            string clientSecret = ReadFirstString(root, "client_secret", "clientSecret");
            string authMethod = ReadFirstString(root, "auth_method", "authMethod");
            string tierHint = ExtractTierFromObject(root);
            credential = new GeminiCredential(
                SourceFilePath: sourceFilePath,
                AccessToken: accessToken,
                RefreshToken: string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken,
                ExpiresAt: expiresAt,
                ClientId: string.IsNullOrWhiteSpace(clientId) ? null : clientId,
                ClientSecret: string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret,
                TierHint: tierHint,
                PreferGeminiCliClient: preferGeminiCliClient)
            {
                AuthMethod = string.IsNullOrWhiteSpace(authMethod) ? null : authMethod,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes an unpadded or standard Base64 payload without losing protobuf bytes.
    /// </summary>
    private static bool TryDecodeBase64(string value, out byte[] decoded)
    {
        decoded = [];
        string normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
            case 1:
                return false;
        }

        try
        {
            decoded = Convert.FromBase64String(normalized);
            return decoded.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Determines whether an access token is missing or within the refresh buffer of expiry.
    /// </summary>
    private static bool NeedsRefresh(DateTimeOffset? expiresAt, string accessToken, DateTimeOffset now, TimeSpan? refreshBuffer = null)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return true;
        }

        DateTimeOffset? effectiveExpiry = expiresAt;
        if (OAuthTokenHelpers.TryGetJwtExpiry(accessToken, out DateTimeOffset jwtExpiry))
        {
            effectiveExpiry = effectiveExpiry is DateTimeOffset stored
                ? (stored < jwtExpiry ? stored : jwtExpiry)
                : jwtExpiry;
        }

        return effectiveExpiry is DateTimeOffset expiry && expiry <= now + (refreshBuffer ?? s_RefreshBuffer);
    }

    /// <summary>
    /// Assigns one parsed Cloud Code quota bucket to the matching Gemini or partner window slot.
    /// </summary>
    private static void AssignBucket(
        JsonElement bucket,
        string groupName,
        DateTimeOffset now,
        bool useAbsoluteResetTime,
        ref UsageLimit? geminiFiveHour,
        ref UsageLimit? geminiWeekly,
        ref UsageLimit? argonWeekly,
        ref UsageLimit? partnerFiveHour,
        ref UsageLimit? partnerWeekly)
    {
        if (bucket.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (bucket.TryGetProperty("disabled", out JsonElement disabled) && disabled.ValueKind == JsonValueKind.True)
        {
            return;
        }

        string bucketId = ReadFirstString(bucket, "bucketId", "bucket_id", "id", "quotaId", "name").Trim();
        string window = ReadFirstString(bucket, "window", "windowType", "period", "duration").Trim();
        bool hasReset = TryParseResetTime(bucket, out DateTimeOffset resetAt);
        bool isExhausted = bucket.TryGetProperty("isExhausted", out JsonElement exhaustedElement)
            && exhaustedElement.ValueKind == JsonValueKind.True;

        if (!TryReadRemainingFraction(bucket, hasReset || isExhausted, out double remainingFraction))
        {
            return;
        }

        int remainingPercent = (int)Math.Round(Math.Clamp(remainingFraction * 100.0, 0.0, 100.0), MidpointRounding.AwayFromZero);
        int usedPercent = Math.Clamp(100 - remainingPercent, 0, 100);
        long resetsAt = hasReset ? resetAt.ToUnixTimeSeconds() : 0;

        bool isFiveHour = bucketId.EndsWith("-5h", StringComparison.OrdinalIgnoreCase)
            || window.Contains("5h", StringComparison.OrdinalIgnoreCase)
            || window.Contains("five_hour", StringComparison.OrdinalIgnoreCase)
            || window.Contains("five hour", StringComparison.OrdinalIgnoreCase);
        bool isWeekly = bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase)
            || window.Contains("week", StringComparison.OrdinalIgnoreCase);
        int windowMinutes = isFiveHour ? k_FiveHourWindowMinutes : isWeekly ? k_WeeklyWindowMinutes : 0;

        UsageLimit BuildLimit(string name, int fallbackWindowMinutes)
        {
            int effectiveWindowMinutes = windowMinutes > 0 ? windowMinutes : fallbackWindowMinutes;
            UsageLimit limit = new()
            {
                Name = name,
                UsedPercent = usedPercent,
                RemainingPercent = remainingPercent,
                WindowMinutes = effectiveWindowMinutes,
                ResetsAt = resetsAt,
            };
            limit.ResetLabel = FormatResetLabel(limit, useAbsoluteResetTime, now);
            return limit;
        }

        if (bucketId.Equals("gemini-argon-wtu-weekly", StringComparison.OrdinalIgnoreCase)
            || bucketId.Contains("argon", StringComparison.OrdinalIgnoreCase))
        {
            argonWeekly ??= BuildLimit("argon_weekly", k_WeeklyWindowMinutes);
            return;
        }

        if (bucketId.Equals("gemini-5h", StringComparison.OrdinalIgnoreCase)
            || (isFiveHour && groupName.Contains("gemini", StringComparison.OrdinalIgnoreCase) && !bucketId.Contains("3p", StringComparison.OrdinalIgnoreCase)))
        {
            geminiFiveHour ??= BuildLimit("session", k_FiveHourWindowMinutes);
            return;
        }

        if (bucketId.Equals("gemini-weekly", StringComparison.OrdinalIgnoreCase)
            || (isWeekly && groupName.Contains("gemini", StringComparison.OrdinalIgnoreCase) && !bucketId.Contains("3p", StringComparison.OrdinalIgnoreCase)))
        {
            geminiWeekly ??= BuildLimit("weekly", k_WeeklyWindowMinutes);
            return;
        }

        bool isPartner = bucketId.StartsWith("3p", StringComparison.OrdinalIgnoreCase)
            || groupName.Contains("claude", StringComparison.OrdinalIgnoreCase)
            || groupName.Contains("gpt", StringComparison.OrdinalIgnoreCase)
            || groupName.Contains("partner", StringComparison.OrdinalIgnoreCase);
        if (!isPartner)
        {
            return;
        }

        if (bucketId.Equals("3p-5h", StringComparison.OrdinalIgnoreCase) || isFiveHour)
        {
            partnerFiveHour ??= BuildLimit("partner_session", k_FiveHourWindowMinutes);
        }
        else if (bucketId.Equals("3p-weekly", StringComparison.OrdinalIgnoreCase) || isWeekly)
        {
            partnerWeekly ??= BuildLimit("partner_weekly", k_WeeklyWindowMinutes);
        }
    }

    /// <summary>
    /// Reads remaining quota fraction (0.0..1.0), treating omitted values on exhausted/resetting buckets as 0.0.
    /// </summary>
    private static bool TryReadRemainingFraction(JsonElement bucket, bool treatOmittedAsExhausted, out double fraction)
    {
        foreach (string name in new[] { "remainingFraction", "remaining_fraction", "remaining" })
        {
            if (bucket.TryGetProperty(name, out JsonElement element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetDouble(out fraction)
                && double.IsFinite(fraction))
            {
                return true;
            }
        }

        if (bucket.TryGetProperty("remainingPercent", out JsonElement percentElement)
            && percentElement.ValueKind == JsonValueKind.Number
            && percentElement.TryGetDouble(out double remainingPercent)
            && double.IsFinite(remainingPercent))
        {
            fraction = remainingPercent / 100.0;
            return true;
        }

        if (bucket.TryGetProperty("usedFraction", out JsonElement usedFractionElement)
            && usedFractionElement.ValueKind == JsonValueKind.Number
            && usedFractionElement.TryGetDouble(out double usedFraction)
            && double.IsFinite(usedFraction))
        {
            fraction = 1.0 - usedFraction;
            return true;
        }

        if (treatOmittedAsExhausted)
        {
            fraction = 0.0;
            return true;
        }

        fraction = 0.0;
        return false;
    }

    /// <summary>
    /// Parses a reset timestamp from RFC 3339 or numeric epoch properties.
    /// </summary>
    private static bool TryParseResetTime(JsonElement bucket, out DateTimeOffset resetAt)
    {
        resetAt = default;
        foreach (string name in new[] { "resetTime", "reset_time", "resetTimestamp", "resetAt" })
        {
            if (!bucket.TryGetProperty(name, out JsonElement property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out resetAt))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long epoch))
            {
                resetAt = epoch > 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                    : DateTimeOffset.FromUnixTimeSeconds(epoch);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses OAuth token expiration timestamps expressed as millisecond epoch integers or ISO strings.
    /// </summary>
    private static DateTimeOffset? ParseExpiryTimestamp(JsonElement root)
    {
        foreach (string name in new[] { "expiry_date", "expiryDate", "expires_at", "expiresAt", "expiry" })
        {
            if (!root.TryGetProperty(name, out JsonElement property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long epoch))
            {
                return epoch > 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                    : DateTimeOffset.FromUnixTimeSeconds(epoch);
            }

            if (property.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// Enumerates the root object plus optional response/quotaSummary wrapper objects.
    /// </summary>
    private static IEnumerable<JsonElement> EnumerateCandidateObjects(JsonElement root)
    {
        yield return root;
        if (root.TryGetProperty("response", out JsonElement response) && response.ValueKind == JsonValueKind.Object)
        {
            yield return response;
        }

        if (root.TryGetProperty("quotaSummary", out JsonElement quotaSummary) && quotaSummary.ValueKind == JsonValueKind.Object)
        {
            yield return quotaSummary;
        }
    }

    /// <summary>
    /// Extracts the Cloud Code project identifier from a loadCodeAssist response.
    /// </summary>
    private static string ExtractProjectId(JsonElement root)
    {
        if (root.TryGetProperty("cloudaicompanionProject", out JsonElement project))
        {
            if (project.ValueKind == JsonValueKind.String)
            {
                return project.GetString()?.Trim() ?? string.Empty;
            }

            if (project.ValueKind == JsonValueKind.Object)
            {
                return ReadFirstString(project, "id", "projectId", "project");
            }
        }

        return ReadFirstString(root, "projectId", "project");
    }

    /// <summary>
    /// Extracts the highest-priority subscription tier string from Cloud Code or local credential JSON.
    /// </summary>
    private static string ExtractTierFromObject(JsonElement root)
    {
        foreach (string field in new[] { "paidTier", "currentTier", "userTier", "cloudaicompanionTier", "subscriptionTier", "subscription_tier", "tier" })
        {
            if (!root.TryGetProperty(field, out JsonElement element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))
            {
                return element.GetString()!.Trim();
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                string nested = ReadFirstString(element, "tierDisplayName", "displayName", "userTier", "id", "name", "tier");
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested.Trim();
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns the first non-empty string among candidate JSON property names.
    /// </summary>
    private static string ReadFirstString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement property)
                && property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString()))
            {
                return property.GetString()!.Trim();
            }
        }

        return string.Empty;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out nint credentialPtr);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsCredential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    internal enum GeminiCredentialSourceKind
    {
        Unknown,
        LocalFile,
        WindowsCredentialManager,
        AntigravityIdeDatabase,
        InjectedStore,
    }

    internal sealed record AgyCliResult(int ExitCode, string StdOut, bool TimedOut, bool OutputTruncated);

    private sealed record RecoveryResult(GeminiCredential? Credential, GeminiUsageSnapshot? DirectSnapshot);

    private sealed record BackoffState(
        string SourceKey,
        GeminiUsageFailureKind FailureKind,
        GeminiCredentialOwner Owner,
        string Message,
        DateTimeOffset NextAttemptAfter,
        bool WaitForChange,
        int ConsecutiveFailures);

    private enum AgyVersionProbeStatus
    {
        Supported,
        UnsupportedVersion,
        ProbeFailed,
        TimedOut,
    }

    private sealed record CachedAgyVersion(string Path, DateTime LastWriteUtc, long Length, Version Version);

    internal sealed record GeminiCredential(
        string? SourceFilePath,
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt,
        string? ClientId,
        string? ClientSecret,
        string TierHint,
        bool PreferGeminiCliClient)
    {
        public string? SourceFileContents { get; init; }

        public GeminiCredentialSourceKind SourceKind { get; init; }

        public GeminiCredentialOwner Owner { get; init; }

        public string SourceLocation { get; init; } = string.Empty;

        public string RawFingerprint { get; init; } = string.Empty;

        public string? AuthMethod { get; init; }

        public bool CanInvokeAgyHelper { get; init; }

        public string SourceKey => $"{SourceKind}|{SourceLocation}|{RawFingerprint}";
    }

    private sealed class GeminiDataException(
        string message,
        GeminiUsageFailureKind failureKind = GeminiUsageFailureKind.InvalidResponse,
        GeminiCredentialOwner owner = GeminiCredentialOwner.Unknown) : InvalidOperationException(message)
    {
        public GeminiUsageFailureKind FailureKind { get; } = failureKind;

        public GeminiCredentialOwner Owner { get; } = owner;
    }

    private sealed class GeminiAuthException(HttpStatusCode statusCode)
        : InvalidOperationException(statusCode == HttpStatusCode.Forbidden
            ? "Gemini quota access was denied (HTTP 403)."
            : "Gemini OAuth token expired or unauthorized. Sign in again.")
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}
