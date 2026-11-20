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
    AntigravityAcp,
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
    private const int k_ClientScanBufferBytes = 1024 * 1024;
    private const int k_ClientScanOverlapBytes = 256;
    private const int k_ClientPairProximityBytes = 4096;
    private const int k_MaxCrossProductDimension = 4;
    private const int k_MaxDiscoveredClientPairs = 24;
    private const long k_MaxClientScanFileBytes = 256L * 1024 * 1024;
    private const string k_PrimaryCloudCodeBaseUrl = "https://daily-cloudcode-pa.googleapis.com";
    private const string k_FallbackCloudCodeBaseUrl = "https://cloudcode-pa.googleapis.com";
    private const string k_LoadCodeAssistPath = "/v1internal:loadCodeAssist";
    private const string k_RetrieveQuotaSummaryPath = "/v1internal:retrieveUserQuotaSummary";
    private const string k_DefaultUserAgent = "antigravity/hub/2.24.0";
    private const string k_DefaultTokenRefreshUrl = "https://oauth2.googleapis.com/token";

    private static readonly TimeSpan s_RefreshBuffer = TimeSpan.FromMinutes(5);
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
    private readonly bool m_AllowLocalClientDiscovery;
    private readonly string[]? m_ExplicitClientDiscoveryPaths;

    private readonly object m_StateLock = new();
    private long m_StateGeneration;
    private BackoffState? m_BackoffState;
    private CachedRefreshedToken? m_CachedToken;
    private readonly Dictionary<string, CachedClientPairs> m_CachedClientPairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OAuthClientPair> m_VerifiedClientPairs = new(StringComparer.Ordinal);

    [GeneratedRegex(@"ya29\.[A-Za-z0-9._\-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleAccessTokenRegex();

    [GeneratedRegex(@"1//[A-Za-z0-9._\-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleRefreshTokenRegex();

    /// <summary>
    /// Creates a Gemini usage collector using the default credential discovery paths and HTTP client.
    /// </summary>
    public GeminiUsageCollector()
        : this(s_HttpClient, explicitGeminiHomePath: null, baseUrl: null, tokenRefreshUrl: null, credentialStoreReader: null, nowProvider: null, useSystemCredentialStores: true, allowLocalClientDiscovery: true)
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
        bool allowLocalClientDiscovery = false,
        string[]? clientDiscoveryPaths = null)
    {
        m_HttpClient = httpClient;
        m_NowProvider = nowProvider ?? (() => DateTimeOffset.Now);
        m_ExplicitGeminiHomePath = explicitGeminiHomePath;
        m_BaseUrlOverride = baseUrl?.TrimEnd('/');
        m_TokenRefreshUrl = string.IsNullOrWhiteSpace(tokenRefreshUrl) ? k_DefaultTokenRefreshUrl : tokenRefreshUrl;
        m_CredentialStoreReader = credentialStoreReader;
        m_UseSystemCredentialStores = useSystemCredentialStores;
        m_AllowLocalClientDiscovery = allowLocalClientDiscovery || clientDiscoveryPaths != null;
        m_ExplicitClientDiscoveryPaths = clientDiscoveryPaths;
    }

    /// <summary>
    /// Clears transient failure backoff, private token cache, and discovered OAuth client metadata without blocking the caller.
    /// </summary>
    public void ResetState()
    {
        lock (m_StateLock)
        {
            m_StateGeneration++;
            m_BackoffState = null;
            m_CachedToken = null;
            m_CachedClientPairs.Clear();
            m_VerifiedClientPairs.Clear();
        }
    }

    private long ReadStateGeneration()
    {
        lock (m_StateLock)
        {
            return m_StateGeneration;
        }
    }

    private void EnsureStateGenerationUnchanged(long stateGeneration, GeminiCredentialOwner owner)
    {
        if (ReadStateGeneration() != stateGeneration)
        {
            throw new GeminiDataException("Gemini OAuth credentials changed. Refresh again.", GeminiUsageFailureKind.CredentialChanged, owner);
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
        GeminiCredential sourceCredential;
        GeminiCredential effectiveCredential;
        bool refreshed = false;
        try
        {
            now = m_NowProvider();
            if (!TryLoadCredential(geminiHome, out sourceCredential, out string error, out bool hadLocalReadError))
            {
                ClearBackoff(stateGeneration);
                ClearCachedToken(stateGeneration);
                throw new GeminiDataException(error, hadLocalReadError ? GeminiUsageFailureKind.LocalDataError : GeminiUsageFailureKind.LoginRequired, GeminiCredentialOwner.Unknown);
            }

            BackoffState? activeBackoff = ReadBackoffForSource(sourceCredential.SourceKey, stateGeneration);
            if (!forceRefresh && activeBackoff != null && (activeBackoff.WaitForChange || now < activeBackoff.NextAttemptAfter))
            {
                throw new GeminiDataException(activeBackoff.Message, activeBackoff.FailureKind, activeBackoff.Owner);
            }

            effectiveCredential = ApplyCachedToken(sourceCredential, stateGeneration);
            bool isActuallyExpired = NeedsRefresh(effectiveCredential.ExpiresAt, effectiveCredential.AccessToken, now, TimeSpan.Zero);
            bool inRefreshBuffer = isActuallyExpired || NeedsRefresh(effectiveCredential.ExpiresAt, effectiveCredential.AccessToken, now);
            if (inRefreshBuffer)
            {
                RefreshResult refreshResult = await RefreshCredentialLockedAsync(
                    sourceCredential,
                    effectiveCredential,
                    now,
                    allowCurrentOnUnavailable: !isActuallyExpired,
                    stateGeneration,
                    cancellationToken).ConfigureAwait(false);
                sourceCredential = refreshResult.SourceCredential;
                effectiveCredential = refreshResult.EffectiveCredential;
                refreshed = refreshResult.WasRefreshed;
            }
        }
        finally
        {
            s_AuthLock.Release();
        }

        try
        {
            EnsureStateGenerationUnchanged(stateGeneration, sourceCredential.Owner);
            GeminiUsageSnapshot snapshot = await FetchFromCloudCodeAsync(effectiveCredential, useAbsoluteResetTime, now, cancellationToken).ConfigureAwait(false);
            EnsureCredentialUnchanged(sourceCredential);
            EnsureStateGenerationUnchanged(stateGeneration, sourceCredential.Owner);
            ClearBackoff(stateGeneration);
            return snapshot;
        }
        catch (GeminiAuthException) when (!refreshed && CanAttemptRefresh(sourceCredential, effectiveCredential, stateGeneration, cancellationToken))
        {
            await s_AuthLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                now = m_NowProvider();
                EnsureStateGenerationUnchanged(stateGeneration, sourceCredential.Owner);
                EnsureCredentialUnchanged(sourceCredential);
                ClearCachedToken(stateGeneration);
                RefreshResult refreshResult = await RefreshCredentialLockedAsync(
                    sourceCredential,
                    effectiveCredential with { AccessToken = string.Empty },
                    now,
                    allowCurrentOnUnavailable: false,
                    stateGeneration,
                    cancellationToken).ConfigureAwait(false);
                sourceCredential = refreshResult.SourceCredential;
                effectiveCredential = refreshResult.EffectiveCredential;
                refreshed = refreshResult.WasRefreshed;
            }
            finally
            {
                s_AuthLock.Release();
            }

            try
            {
                EnsureStateGenerationUnchanged(stateGeneration, sourceCredential.Owner);
                GeminiUsageSnapshot snapshot = await FetchFromCloudCodeAsync(effectiveCredential, useAbsoluteResetTime, now, cancellationToken).ConfigureAwait(false);
                EnsureCredentialUnchanged(sourceCredential);
                EnsureStateGenerationUnchanged(stateGeneration, sourceCredential.Owner);
                ClearBackoff(stateGeneration);
                return snapshot;
            }
            catch (GeminiAuthException secondAuth)
            {
                ClearCachedToken(stateGeneration);
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
            ClearCachedToken(stateGeneration);
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

    private bool CanAttemptRefresh(GeminiCredential sourceCredential, GeminiCredential effectiveCredential, long stateGeneration, CancellationToken cancellationToken)
    {
        return !string.IsNullOrWhiteSpace(effectiveCredential.RefreshToken)
            && ResolveClientPairs(sourceCredential, stateGeneration, cancellationToken, out _).Count > 0;
    }

    private GeminiCredential ApplyCachedToken(GeminiCredential sourceCredential, long stateGeneration)
    {
        lock (m_StateLock)
        {
            if (m_StateGeneration != stateGeneration || m_CachedToken == null)
            {
                return sourceCredential;
            }

            if (!string.Equals(m_CachedToken.SourceKey, sourceCredential.SourceKey, StringComparison.Ordinal))
            {
                m_CachedToken = null;
                return sourceCredential;
            }

            return sourceCredential with
            {
                AccessToken = m_CachedToken.AccessToken,
                ExpiresAt = m_CachedToken.ExpiresAt,
            };
        }
    }

    private void StoreCachedToken(string sourceKey, string accessToken, DateTimeOffset expiresAt, long stateGeneration)
    {
        lock (m_StateLock)
        {
            if (m_StateGeneration == stateGeneration)
            {
                m_CachedToken = new CachedRefreshedToken(sourceKey, accessToken, expiresAt);
            }
        }
    }

    private void ClearCachedToken(long stateGeneration)
    {
        lock (m_StateLock)
        {
            if (m_StateGeneration == stateGeneration)
            {
                m_CachedToken = null;
            }
        }
    }

    private void StoreVerifiedClientPair(string sourceLocation, OAuthClientPair pair, long stateGeneration)
    {
        lock (m_StateLock)
        {
            if (m_StateGeneration == stateGeneration)
            {
                m_VerifiedClientPairs[sourceLocation] = pair;
            }
        }
    }

    private void ClearVerifiedClientPair(string sourceLocation, long stateGeneration)
    {
        lock (m_StateLock)
        {
            if (m_StateGeneration == stateGeneration)
            {
                m_VerifiedClientPairs.Remove(sourceLocation);
            }
        }
    }

    /// <summary>
    /// Refreshes an OAuth access token directly, persisting to writable local JSON files or caching short-lived access tokens in memory for read-only stores.
    /// </summary>
    private async Task<RefreshResult> RefreshCredentialLockedAsync(
        GeminiCredential sourceCredential,
        GeminiCredential effectiveCredential,
        DateTimeOffset now,
        bool allowCurrentOnUnavailable,
        long stateGeneration,
        CancellationToken cancellationToken)
    {
        EnsureStateGenerationUnchanged(stateGeneration, sourceCredential.Owner);
        EnsureCredentialUnchanged(sourceCredential);
        bool canKeepCurrent = allowCurrentOnUnavailable && !NeedsRefresh(effectiveCredential.ExpiresAt, effectiveCredential.AccessToken, now, TimeSpan.Zero);

        bool hasRefreshToken = !string.IsNullOrWhiteSpace(effectiveCredential.RefreshToken);
        bool hadTransientDiscoveryError = false;
        IReadOnlyList<OAuthClientPair> clientPairs = hasRefreshToken
            ? ResolveClientPairs(sourceCredential, stateGeneration, cancellationToken, out hadTransientDiscoveryError)
            : [];
        if (!hasRefreshToken || clientPairs.Count == 0)
        {
            EnsureStateGenerationUnchanged(stateGeneration, sourceCredential.Owner);
            EnsureCredentialUnchanged(sourceCredential);
            if (canKeepCurrent)
            {
                return new RefreshResult(sourceCredential, effectiveCredential, WasRefreshed: false);
            }

            if (hadTransientDiscoveryError)
            {
                throw RecordTransientBackoff(
                    sourceCredential,
                    GeminiUsageFailureKind.LocalDataError,
                    "Local OAuth client configuration could not be read. " + FormatOwnerRefreshMessage(sourceCredential.Owner),
                    now,
                    stateGeneration);
            }

            string reason = !hasRefreshToken
                ? FormatOwnerRefreshMessage(sourceCredential.Owner)
                : "OAuth client configuration is incomplete. " + FormatOwnerRefreshMessage(sourceCredential.Owner);
            throw RecordPermanentBackoff(sourceCredential, GeminiUsageFailureKind.OwnerRefreshRequired, reason, stateGeneration);
        }

        OAuthTokenResponse? refreshed = null;
        OAuthClientPair? matchedPair = null;
        bool sawInvalidGrant = false;
        for (int index = 0; index < clientPairs.Count; index++)
        {
            EnsureStateGenerationUnchanged(stateGeneration, sourceCredential.Owner);
            OAuthClientPair pair = clientPairs[index];
            try
            {
                refreshed = await OAuthTokenHelpers.RefreshAsync(
                    m_HttpClient,
                    m_TokenRefreshUrl,
                    pair.ClientId,
                    effectiveCredential.RefreshToken!,
                    "Gemini",
                    cancellationToken,
                    pair.ClientSecret).ConfigureAwait(false);
                matchedPair = pair;
                break;
            }
            catch (UsageHttpException httpException) when (httpException.OAuthErrorCode is "invalid_grant" or "invalid_client" or "unauthorized_client")
            {
                if (httpException.OAuthErrorCode == "invalid_grant")
                {
                    sawInvalidGrant = true;
                }

                if (index + 1 < clientPairs.Count)
                {
                    EnsureStateGenerationUnchanged(stateGeneration, sourceCredential.Owner);
                    EnsureCredentialUnchanged(sourceCredential);
                    continue;
                }

                ClearCachedToken(stateGeneration);
                ClearVerifiedClientPair(sourceCredential.SourceLocation, stateGeneration);
                string message = sawInvalidGrant
                    ? FormatOwnerRefreshMessage(sourceCredential.Owner)
                    : "OAuth client configuration did not match this login. " + FormatOwnerRefreshMessage(sourceCredential.Owner);
                throw RecordPermanentBackoff(sourceCredential, GeminiUsageFailureKind.OwnerRefreshRequired, message, stateGeneration);
            }
            catch (Exception exception) when (exception is UsageHttpException or HttpRequestException or TaskCanceledException)
            {
                RecordTransientBackoff(sourceCredential, exception, now, stateGeneration);
                throw;
            }
        }

        EnsureStateGenerationUnchanged(stateGeneration, sourceCredential.Owner);
        EnsureCredentialUnchanged(sourceCredential);
        if (matchedPair != null)
        {
            StoreVerifiedClientPair(sourceCredential.SourceLocation, matchedPair, stateGeneration);
        }

        DateTimeOffset expiresAt = refreshed!.ExpiresInSeconds is double seconds
            ? now.AddSeconds(seconds)
            : OAuthTokenHelpers.TryGetJwtExpiry(refreshed.AccessToken, out DateTimeOffset jwtExpiry)
                ? jwtExpiry
                : now.AddHours(1);

        GeminiCredential updated = effectiveCredential with
        {
            AccessToken = refreshed.AccessToken,
            RefreshToken = refreshed.RefreshToken,
            ExpiresAt = expiresAt,
        };

        // Attempt atomic TxF writeback for local JSON files; fall back to short-lived in-memory caching when the file is locked or read-only while contents remain unchanged.
        if (sourceCredential.SourceFilePath != null && TrySaveUpdatedCredentialFile(sourceCredential, updated, out GeminiCredential persisted))
        {
            ClearCachedToken(stateGeneration);
            return new RefreshResult(persisted, persisted, WasRefreshed: true);
        }

        // Only cache the short-lived access token and expiry; never cache long-lived refresh tokens for read-only stores.
        StoreCachedToken(sourceCredential.SourceKey, updated.AccessToken, expiresAt, stateGeneration);
        return new RefreshResult(sourceCredential, sourceCredential with { AccessToken = updated.AccessToken, ExpiresAt = expiresAt }, WasRefreshed: true);
    }

    private IReadOnlyList<OAuthClientPair> ResolveClientPairs(
        GeminiCredential credential,
        long stateGeneration,
        CancellationToken cancellationToken,
        out bool hadTransientDiscoveryError)
    {
        hadTransientDiscoveryError = false;
        if (credential.ClientId != null || credential.ClientSecret != null)
        {
            return !string.IsNullOrWhiteSpace(credential.ClientId) && !string.IsNullOrWhiteSpace(credential.ClientSecret)
                ? [new OAuthClientPair(credential.ClientId, credential.ClientSecret)]
                : [];
        }

        string prefix = credential.PreferGeminiCliClient ? "CODEXTRAY_GEMINI_CLI" : "CODEXTRAY_ANTIGRAVITY";
        string? envId = Environment.GetEnvironmentVariable(prefix + "_CLIENT_ID");
        string? envSecret = Environment.GetEnvironmentVariable(prefix + "_CLIENT_SECRET");
        if (envId != null || envSecret != null)
        {
            return !string.IsNullOrWhiteSpace(envId) && !string.IsNullOrWhiteSpace(envSecret)
                ? [new OAuthClientPair(envId, envSecret)]
                : [];
        }

        if (!m_AllowLocalClientDiscovery)
        {
            return [];
        }

        List<OAuthClientPair> discovered = DiscoverLocalClientPairs(credential.Owner, stateGeneration, cancellationToken, out hadTransientDiscoveryError);
        if (discovered.Count <= 1)
        {
            return discovered;
        }

        OAuthClientPair? verified = null;
        lock (m_StateLock)
        {
            if (m_StateGeneration == stateGeneration)
            {
                m_VerifiedClientPairs.TryGetValue(credential.SourceLocation, out verified);
            }
        }

        if (verified != null && discovered.Contains(verified))
        {
            List<OAuthClientPair> prioritized = [verified];
            foreach (OAuthClientPair candidate in discovered)
            {
                if (candidate != verified)
                {
                    prioritized.Add(candidate);
                }
            }

            return prioritized;
        }

        return discovered;
    }

    private List<OAuthClientPair> DiscoverLocalClientPairs(
        GeminiCredentialOwner owner,
        long stateGeneration,
        CancellationToken cancellationToken,
        out bool hadTransientError)
    {
        hadTransientError = false;
        List<OAuthClientPair> combined = [];
        HashSet<OAuthClientPair> seen = [];

        foreach (string candidatePath in GetCandidateClientDiscoveryPaths(owner))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTrustedLocalFile(candidatePath))
            {
                continue;
            }

            DateTime lastWriteUtc;
            long length;
            try
            {
                FileInfo info = new(candidatePath);
                if (info.Length <= 0 || info.Length > k_MaxClientScanFileBytes)
                {
                    continue;
                }

                lastWriteUtc = info.LastWriteTimeUtc;
                length = info.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                hadTransientError = true;
                continue;
            }

            CachedClientPairs? cached = null;
            lock (m_StateLock)
            {
                if (m_StateGeneration == stateGeneration
                    && m_CachedClientPairs.TryGetValue(candidatePath, out CachedClientPairs? entry)
                    && entry.LastWriteUtc == lastWriteUtc
                    && entry.Length == length)
                {
                    cached = entry;
                }
            }

            IReadOnlyList<OAuthClientPair> filePairs;
            if (cached != null)
            {
                filePairs = cached.Pairs;
            }
            else
            {
                if (!TryScanOAuthClientPairsFromFile(candidatePath, cancellationToken, out filePairs))
                {
                    hadTransientError = true;
                    continue;
                }

                lock (m_StateLock)
                {
                    if (m_StateGeneration == stateGeneration)
                    {
                        m_CachedClientPairs[candidatePath] = new CachedClientPairs(lastWriteUtc, length, filePairs);
                    }
                }
            }

            foreach (OAuthClientPair pair in filePairs)
            {
                if (seen.Add(pair))
                {
                    combined.Add(pair);
                    if (combined.Count >= k_MaxDiscoveredClientPairs)
                    {
                        return combined;
                    }
                }
            }
        }

        return combined;
    }

    private IEnumerable<string> GetCandidateClientDiscoveryPaths(GeminiCredentialOwner owner)
    {
        if (m_ExplicitClientDiscoveryPaths != null)
        {
            return m_ExplicitClientDiscoveryPaths;
        }

        List<string> paths = [];
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (owner == GeminiCredentialOwner.GeminiCli)
        {
            if (!string.IsNullOrWhiteSpace(appData))
            {
                paths.Add(Path.Combine(appData, "npm", "node_modules", "@google", "gemini-cli", "node_modules", "@google", "gemini-cli-core", "dist", "src", "code_assist", "oauth2.js"));
                paths.Add(Path.Combine(appData, "npm", "node_modules", "@google", "gemini-cli-core", "dist", "src", "code_assist", "oauth2.js"));
            }

            return paths;
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return paths;
        }

        void AddIdeCandidates()
        {
            foreach (string folderName in new[] { "Antigravity", "Antigravity IDE" })
            {
                string resources = Path.Combine(localAppData, "Programs", folderName, "resources");
                paths.Add(Path.Combine(resources, "app", "extensions", "antigravity", "bin", "language_server_windows_x64.exe"));
                paths.Add(Path.Combine(resources, "app.asar"));
                paths.Add(Path.Combine(resources, "app", "out", "main.js"));
            }
        }

        void AddCliCandidates()
        {
            if (owner == GeminiCredentialOwner.AntigravityAcp)
            {
                paths.Add(Path.Combine(localAppData, "agy", "bin", "antigravity-acp.exe"));
            }

            paths.Add(Path.Combine(localAppData, "agy", "bin", "agy.exe"));
        }

        if (owner == GeminiCredentialOwner.AntigravityIde)
        {
            AddIdeCandidates();
            AddCliCandidates();
        }
        else
        {
            AddCliCandidates();
            AddIdeCandidates();
        }

        return paths;
    }

    internal static bool IsTrustedLocalFile(string path)
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

    /// <summary>
    /// Streams a local installed app archive, JS bundle, or native binary to extract candidate Google installed-app OAuth client pairs.
    /// </summary>
    internal static IReadOnlyList<OAuthClientPair> ScanOAuthClientPairsFromFile(string path, CancellationToken cancellationToken = default)
    {
        return TryScanOAuthClientPairsFromFile(path, cancellationToken, out IReadOnlyList<OAuthClientPair> pairs) ? pairs : [];
    }

    internal static bool TryScanOAuthClientPairsFromFile(string path, CancellationToken cancellationToken, out IReadOnlyList<OAuthClientPair> pairs)
    {
        pairs = [];
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > k_MaxClientScanFileBytes)
            {
                return true;
            }

            byte[] buffer = new byte[k_ClientScanBufferBytes + k_ClientScanOverlapBytes];
            List<(string Value, long Offset)> clientIds = [];
            List<(string Value, long Offset)> clientSecrets = [];

            long fileOffset = 0;
            int carried = 0;
            bool isFirstChunk = true;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int bytesRead = ReadFullChunk(stream, buffer, carried, k_ClientScanBufferBytes);
                int totalLength = carried + bytesRead;
                if (totalLength == 0)
                {
                    break;
                }

                bool isLastChunk = bytesRead < k_ClientScanBufferBytes || stream.Position >= stream.Length;
                long chunkStartOffset = fileOffset - carried;
                ReadOnlySpan<byte> span = buffer.AsSpan(0, totalLength);

                int idScanStart = isFirstChunk ? 0 : Math.Max(0, carried - 32);
                int idScanEnd = isLastChunk ? totalLength : Math.Max(idScanStart, totalLength - 32);
                ScanClientIds(span, chunkStartOffset, idScanStart, idScanEnd, clientIds);

                int secretScanStart = isFirstChunk ? 0 : Math.Max(0, carried - 64);
                int secretScanEnd = isLastChunk ? totalLength : Math.Max(secretScanStart, totalLength - 64);
                ScanClientSecrets(span, chunkStartOffset, secretScanStart, secretScanEnd, clientSecrets);

                if (TrySelectNearbyPair(clientIds, clientSecrets, out string nearbyId, out string nearbySecret))
                {
                    pairs = [new OAuthClientPair(nearbyId, nearbySecret)];
                    return true;
                }

                fileOffset += bytesRead;
                if (isLastChunk)
                {
                    break;
                }

                carried = Math.Min(k_ClientScanOverlapBytes, totalLength);
                Buffer.BlockCopy(buffer, totalLength - carried, buffer, 0, carried);
                isFirstChunk = false;
            }

            pairs = SelectOAuthClientPairs(clientIds, clientSecrets);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool TryScanOAuthClientPairFromFile(string path, out string clientId, out string clientSecret)
    {
        IReadOnlyList<OAuthClientPair> pairs = ScanOAuthClientPairsFromFile(path);
        if (pairs.Count > 0)
        {
            clientId = pairs[0].ClientId;
            clientSecret = pairs[0].ClientSecret;
            return true;
        }

        clientId = string.Empty;
        clientSecret = string.Empty;
        return false;
    }

    private static int ReadFullChunk(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static void ScanClientIds(ReadOnlySpan<byte> data, long chunkStartOffset, int scanStart, int scanEnd, List<(string Value, long Offset)> matches)
    {
        ReadOnlySpan<byte> anchor = ".apps.googleusercontent.com"u8;
        int searchOffset = scanStart;
        while (searchOffset < scanEnd)
        {
            int relative = data[searchOffset..].IndexOf(anchor);
            if (relative < 0)
            {
                break;
            }

            int anchorPos = searchOffset + relative;
            searchOffset = anchorPos + anchor.Length;
            if (anchorPos >= scanEnd)
            {
                break;
            }

            int endPos = anchorPos + anchor.Length;
            if (endPos < data.Length && data[endPos] == (byte)'.')
            {
                continue;
            }

            int tokenEnd = anchorPos;
            int tokenStart = tokenEnd;
            while (tokenStart > 0 && tokenEnd - tokenStart < 64 && IsClientIdTokenByte(data[tokenStart - 1]))
            {
                tokenStart--;
            }

            int tokenLength = tokenEnd - tokenStart;
            if (tokenLength < 16 || tokenStart <= 0 || data[tokenStart - 1] != (byte)'-')
            {
                continue;
            }

            int digitsEnd = tokenStart - 1;
            int digitsStart = digitsEnd;
            while (digitsStart > 0 && digitsEnd - digitsStart < 20 && IsAsciiDigit(data[digitsStart - 1]))
            {
                digitsStart--;
            }

            int digitsLength = digitsEnd - digitsStart;
            if (digitsLength < 6)
            {
                continue;
            }

            string value = Encoding.ASCII.GetString(data.Slice(digitsStart, endPos - digitsStart));
            matches.Add((value, chunkStartOffset + digitsStart));
        }
    }

    private static void ScanClientSecrets(ReadOnlySpan<byte> data, long chunkStartOffset, int scanStart, int scanEnd, List<(string Value, long Offset)> matches)
    {
        ReadOnlySpan<byte> anchor = "GOCSPX-"u8;
        int searchOffset = scanStart;
        while (searchOffset < scanEnd)
        {
            int relative = data[searchOffset..].IndexOf(anchor);
            if (relative < 0)
            {
                break;
            }

            int anchorPos = searchOffset + relative;
            searchOffset = anchorPos + anchor.Length;
            if (anchorPos >= scanEnd)
            {
                break;
            }

            int bodyStart = anchorPos + anchor.Length;
            if (bodyStart + 28 > data.Length)
            {
                continue;
            }

            bool validBody = true;
            for (int index = 0; index < 28; index++)
            {
                if (!IsBase64UrlByte(data[bodyStart + index]))
                {
                    validBody = false;
                    break;
                }
            }

            if (!validBody)
            {
                continue;
            }

            // Google GOCSPX client secrets always have a 28-char base64url body; taking exact length avoids swallowing adjacent packed strings in Go .rodata.
            string value = Encoding.ASCII.GetString(data.Slice(anchorPos, anchor.Length + 28));
            matches.Add((value, chunkStartOffset + anchorPos));
        }
    }

    private static bool TrySelectNearbyPair(
        List<(string Value, long Offset)> clientIds,
        List<(string Value, long Offset)> clientSecrets,
        out string clientId,
        out string clientSecret)
    {
        clientId = string.Empty;
        clientSecret = string.Empty;
        if (clientIds.Count == 0 || clientSecrets.Count == 0)
        {
            return false;
        }

        long bestDistance = long.MaxValue;
        foreach ((string idValue, long idOffset) in clientIds)
        {
            foreach ((string secretValue, long secretOffset) in clientSecrets)
            {
                long distance = Math.Abs(idOffset - secretOffset);
                if (distance <= k_ClientPairProximityBytes && distance < bestDistance)
                {
                    bestDistance = distance;
                    clientId = idValue;
                    clientSecret = secretValue;
                }
            }
        }

        return bestDistance <= k_ClientPairProximityBytes;
    }

    private static IReadOnlyList<OAuthClientPair> SelectOAuthClientPairs(
        List<(string Value, long Offset)> clientIds,
        List<(string Value, long Offset)> clientSecrets)
    {
        if (TrySelectNearbyPair(clientIds, clientSecrets, out string nearbyId, out string nearbySecret))
        {
            return [new OAuthClientPair(nearbyId, nearbySecret)];
        }

        List<string> uniqueIds = [];
        HashSet<string> seenIds = new(StringComparer.Ordinal);
        foreach ((string idValue, _) in clientIds)
        {
            if (seenIds.Add(idValue))
            {
                uniqueIds.Add(idValue);
            }
        }

        List<string> uniqueSecrets = [];
        HashSet<string> seenSecrets = new(StringComparer.Ordinal);
        foreach ((string secretValue, _) in clientSecrets)
        {
            if (seenSecrets.Add(secretValue))
            {
                uniqueSecrets.Add(secretValue);
            }
        }

        // Go binaries group .rodata strings by length, separating a small set of OAuth client IDs and secrets across sections.
        if (uniqueIds.Count is >= 1 and <= k_MaxCrossProductDimension
            && uniqueSecrets.Count is >= 1 and <= k_MaxCrossProductDimension)
        {
            List<OAuthClientPair> pairs = new(uniqueIds.Count * uniqueSecrets.Count);
            foreach (string id in uniqueIds)
            {
                foreach (string secret in uniqueSecrets)
                {
                    pairs.Add(new OAuthClientPair(id, secret));
                }
            }

            return pairs;
        }

        return [];
    }

    private static bool IsAsciiDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static bool IsClientIdTokenByte(byte b) => b is (>= (byte)'0' and <= (byte)'9')
        or (>= (byte)'A' and <= (byte)'Z')
        or (>= (byte)'a' and <= (byte)'z')
        or (byte)'_';

    private static bool IsBase64UrlByte(byte b) => b is (>= (byte)'0' and <= (byte)'9')
        or (>= (byte)'A' and <= (byte)'Z')
        or (>= (byte)'a' and <= (byte)'z')
        or (byte)'_'
        or (byte)'-';

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
        GeminiCredentialOwner.AntigravityAcp => "Open Antigravity ACP to renew your Gemini login, then refresh.",
        GeminiCredentialOwner.GeminiCli => "Open Gemini CLI to renew your Gemini login, then refresh.",
        GeminiCredentialOwner.AntigravityIde => "Open Antigravity IDE to renew your Gemini login, then refresh.",
        _ => "Renew your login in Antigravity or Gemini CLI, then refresh.",
    };

    /// <summary>
    /// Attempts to persist refreshed OAuth tokens atomically back to a local JSON credential file; returns false if writeback is unavailable while contents remain unchanged.
    /// </summary>
    private bool TrySaveUpdatedCredentialFile(GeminiCredential original, GeminiCredential updated, out GeminiCredential persisted)
    {
        persisted = default!;
        string filePath = original.SourceFilePath!;
        string rawText;
        try
        {
            rawText = File.ReadAllText(filePath);
        }
        catch (FileNotFoundException)
        {
            throw new GeminiDataException("Gemini OAuth credentials changed. Refresh again.", GeminiUsageFailureKind.CredentialChanged, original.Owner);
        }
        catch (DirectoryNotFoundException)
        {
            throw new GeminiDataException("Gemini OAuth credentials changed. Refresh again.", GeminiUsageFailureKind.CredentialChanged, original.Owner);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            EnsureCredentialUnchanged(original);
            return false;
        }

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
            WriteExpiryTimestamp(tokenObject, expiresAt, original.Owner);
        }

        string contents = rootObject.ToJsonString(s_WriteJsonOptions);
        if (!AtomicFile.TryReplaceAllText(filePath, rawText, contents))
        {
            // Distinguish a transient lock/non-TxF failure (safe to cache in memory) from an external account switch or deletion.
            EnsureCredentialUnchanged(original);
            return false;
        }

        persisted = updated with
        {
            SourceFileContents = contents,
            RawFingerprint = ComputeFingerprint(contents),
        };
        return true;
    }

    private static void WriteExpiryTimestamp(JsonObject tokenObject, DateTimeOffset expiresAt, GeminiCredentialOwner owner)
    {
        foreach (string fieldName in new[] { "expiry_date", "expiryDate", "expires_at", "expiresAt", "expiry" })
        {
            if (!tokenObject.TryGetPropertyValue(fieldName, out JsonNode? existingNode))
            {
                continue;
            }

            if (existingNode is JsonValue value && value.TryGetValue(out long existingEpoch))
            {
                tokenObject[fieldName] = existingEpoch > 100_000_000_000L
                    ? expiresAt.ToUnixTimeMilliseconds()
                    : expiresAt.ToUnixTimeSeconds();
            }
            else
            {
                tokenObject[fieldName] = expiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            }

            return;
        }

        if (owner == GeminiCredentialOwner.GeminiCli)
        {
            tokenObject["expiry_date"] = expiresAt.ToUnixTimeMilliseconds();
        }
        else
        {
            tokenObject["expiry"] = expiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }
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

        (string Path, bool PreferGeminiCliClient, GeminiCredentialOwner Owner)[] candidateFiles =
        [
            (Path.Combine(geminiHome, "antigravity-cli", "antigravity-oauth-token"), false, GeminiCredentialOwner.AntigravityCli),
            (Path.Combine(geminiHome, "antigravity-acp", "acp_token.json"), false, GeminiCredentialOwner.AntigravityAcp),
            (Path.Combine(geminiHome, "oauth_creds.json"), true, GeminiCredentialOwner.GeminiCli),
            (Path.Combine(geminiHome, "antigravity", "oauth_creds.json"), false, GeminiCredentialOwner.AntigravityCli),
        ];

        foreach ((string candidateFile, bool preferGeminiCliClient, GeminiCredentialOwner owner) in candidateFiles)
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
                };
                error = string.Empty;
                return true;
            }
        }
        else if (m_UseSystemCredentialStores && OperatingSystem.IsWindows())
        {
            (string Target, bool PreferGeminiCliClient, GeminiCredentialOwner Owner)[] credentialTargets =
            [
                ("gemini:antigravity", false, GeminiCredentialOwner.AntigravityCli),
                ("gemini:antigravity-acp", false, GeminiCredentialOwner.AntigravityAcp),
                ("gemini-cli", true, GeminiCredentialOwner.GeminiCli),
            ];

            foreach ((string target, bool preferGeminiCliClient, GeminiCredentialOwner owner) in credentialTargets)
            {
                string? secretBlob = TryReadWindowsGenericCredential(target);
                if (!string.IsNullOrWhiteSpace(secretBlob) && TryParseCredentialPayload(secretBlob, sourceFilePath: null, preferGeminiCliClient, out credential))
                {
                    credential = credential with
                    {
                        SourceKind = GeminiCredentialSourceKind.WindowsCredentialManager,
                        Owner = owner,
                        SourceLocation = "wincred:" + target,
                        RawFingerprint = ComputeFingerprint(secretBlob),
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
            Match refreshMatch = GoogleRefreshTokenRegex().Match(candidate);
            if (accessMatch.Success || refreshMatch.Success)
            {
                credential = new GeminiCredential(
                    SourceFilePath: sourceFilePath,
                    AccessToken: accessMatch.Success ? accessMatch.Value : string.Empty,
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

    private sealed record RefreshResult(GeminiCredential SourceCredential, GeminiCredential EffectiveCredential, bool WasRefreshed);

    internal sealed record OAuthClientPair(string ClientId, string ClientSecret);

    private sealed record CachedRefreshedToken(string SourceKey, string AccessToken, DateTimeOffset ExpiresAt);

    private sealed record CachedClientPairs(DateTime LastWriteUtc, long Length, IReadOnlyList<OAuthClientPair> Pairs);

    private sealed record BackoffState(
        string SourceKey,
        GeminiUsageFailureKind FailureKind,
        GeminiCredentialOwner Owner,
        string Message,
        DateTimeOffset NextAttemptAfter,
        bool WaitForChange,
        int ConsecutiveFailures);

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
