using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodexTray.Core;

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
    };

    public required GeminiUsageSnapshot Snapshot { get; init; }

    public required string StatusMessage { get; init; }
}

/// <summary>
/// Collects Gemini, Argon, and Claude &amp; GPT partner quota limits using Antigravity or Gemini CLI OAuth credentials.
/// </summary>
public sealed partial class GeminiUsageCollector
{
    private const int k_FiveHourWindowMinutes = 300;
    private const int k_WeeklyWindowMinutes = 10080;
    private const int k_CredTypeGeneric = 1;
    private const string k_PrimaryCloudCodeBaseUrl = "https://daily-cloudcode-pa.googleapis.com";
    private const string k_FallbackCloudCodeBaseUrl = "https://cloudcode-pa.googleapis.com";
    private const string k_LoadCodeAssistPath = "/v1internal:loadCodeAssist";
    private const string k_RetrieveQuotaSummaryPath = "/v1internal:retrieveUserQuotaSummary";
    private const string k_DefaultUserAgent = "antigravity/hub/2.24.0";
    private const string k_DefaultTokenRefreshUrl = "https://oauth2.googleapis.com/token";

    private static readonly TimeSpan s_RefreshBuffer = TimeSpan.FromMinutes(5);
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

    [GeneratedRegex(@"ya29\.[A-Za-z0-9._\-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleAccessTokenRegex();

    [GeneratedRegex(@"1//[A-Za-z0-9._\-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleRefreshTokenRegex();

    /// <summary>
    /// Creates a Gemini usage collector using the default credential discovery paths and HTTP client.
    /// </summary>
    public GeminiUsageCollector()
        : this(s_HttpClient, explicitGeminiHomePath: null, baseUrl: null, tokenRefreshUrl: null, credentialStoreReader: null, nowProvider: null, useSystemCredentialStores: true)
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
        bool useSystemCredentialStores = false)
    {
        m_HttpClient = httpClient;
        m_NowProvider = nowProvider ?? (() => DateTimeOffset.Now);
        m_ExplicitGeminiHomePath = explicitGeminiHomePath;
        m_BaseUrlOverride = baseUrl?.TrimEnd('/');
        m_TokenRefreshUrl = string.IsNullOrWhiteSpace(tokenRefreshUrl) ? k_DefaultTokenRefreshUrl : tokenRefreshUrl;
        m_CredentialStoreReader = credentialStoreReader;
        m_UseSystemCredentialStores = useSystemCredentialStores;
    }

    /// <summary>
    /// Collects Gemini quota windows and subscription tier for the current local login.
    /// </summary>
    public async Task<GeminiUsageSnapshot> CollectAsync(bool useAbsoluteResetTime = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = m_NowProvider();
        string geminiHome = ResolveGeminiHomePath(m_ExplicitGeminiHomePath);

        await s_AuthLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        GeminiCredential credential;
        GeminiCredential sourceCredential;
        bool refreshed = false;
        try
        {
            if (!TryLoadCredential(geminiHome, out credential, out string error))
            {
                throw new GeminiDataException(error);
            }

            sourceCredential = credential;
            if (NeedsRefresh(credential.ExpiresAt, credential.AccessToken, now))
            {
                GeminiCredential original = credential;
                credential = await RefreshCredentialAsync(credential, now, allowCurrentOnUnavailable: true, cancellationToken).ConfigureAwait(false);
                if (credential.SourceFilePath != null)
                {
                    sourceCredential = credential;
                }
                refreshed = !ReferenceEquals(original, credential);
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
            return snapshot;
        }
        catch (GeminiAuthException) when (!refreshed && !string.IsNullOrWhiteSpace(credential.RefreshToken))
        {
            await s_AuthLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                credential = await RefreshCredentialAsync(credential, now, allowCurrentOnUnavailable: false, cancellationToken).ConfigureAwait(false);
                if (credential.SourceFilePath != null)
                {
                    sourceCredential = credential;
                }
            }
            finally
            {
                s_AuthLock.Release();
            }

            GeminiUsageSnapshot snapshot = await FetchFromCloudCodeAsync(credential, useAbsoluteResetTime, now, cancellationToken).ConfigureAwait(false);
            EnsureCredentialUnchanged(sourceCredential);
            return snapshot;
        }
    }

    /// <summary>
    /// Collects a display-ready Gemini dashboard without throwing collector-level failures.
    /// </summary>
    public async Task<GeminiUsageDashboard> CollectDashboardAsync(bool useAbsoluteResetTime = false, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = m_NowProvider();
        try
        {
            GeminiUsageSnapshot snapshot = await CollectAsync(useAbsoluteResetTime, cancellationToken).ConfigureAwait(false);
            return new GeminiUsageDashboard
            {
                Snapshot = snapshot,
                StatusMessage = string.Empty,
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
            string message = exception is GeminiDataException or GeminiAuthException
                ? exception.Message
                : UsageDiagnostics.Error(exception);
            return new GeminiUsageDashboard
            {
                Snapshot = new GeminiUsageSnapshot
                {
                    Source = "gemini",
                    UpdatedAt = now,
                    IsAvailable = false,
                },
                StatusMessage = message,
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
        foreach (string baseUrl in baseUrls)
        {
            try
            {
                (string projectId, string loadCodeAssistTier) = await TryLoadCodeAssistAsync(baseUrl, credential.AccessToken, cancellationToken).ConfigureAwait(false);
                string payload = string.IsNullOrWhiteSpace(projectId)
                    ? "{}"
                    : JsonSerializer.Serialize(new { project = projectId });

                using HttpRequestMessage quotaRequest = new(HttpMethod.Post, baseUrl + k_RetrieveQuotaSummaryPath);
                ApplyCloudCodeHeaders(quotaRequest, credential.AccessToken);
                quotaRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using HttpResponseMessage quotaResponse = await UsageHttp.SendAsync(m_HttpClient, quotaRequest, cancellationToken).ConfigureAwait(false);
                if (quotaResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new GeminiAuthException("Gemini OAuth token expired or unauthorized. Sign in again.");
                }

                string body = await quotaResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!quotaResponse.IsSuccessStatusCode)
                {
                    throw new UsageHttpException(quotaResponse.StatusCode);
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

        throw lastError ?? new GeminiDataException("Gemini quota request failed.");
    }

    /// <summary>
    /// Calls loadCodeAssist to obtain the active Cloud Code project ID and subscription tier when available.
    /// </summary>
    private async Task<(string ProjectId, string Tier)> TryLoadCodeAssistAsync(string baseUrl, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, baseUrl + k_LoadCodeAssistPath);
            ApplyCloudCodeHeaders(request, accessToken);
            request.Content = new StringContent("{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}", Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await UsageHttp.SendAsync(m_HttpClient, request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new GeminiAuthException("Gemini OAuth token expired or unauthorized. Sign in again.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return (string.Empty, string.Empty);
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string projectId = ExtractProjectId(root);
            string tier = ExtractTierFromObject(root);
            return (projectId, tier);
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
            return (string.Empty, string.Empty);
        }
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

    /// <summary>
    /// Refreshes an OAuth access token via Google OAuth2 and writes updated tokens back when stored in a local file.
    /// </summary>
    private async Task<GeminiCredential> RefreshCredentialAsync(GeminiCredential credential, DateTimeOffset now, bool allowCurrentOnUnavailable, CancellationToken cancellationToken)
    {
        EnsureCredentialUnchanged(credential);
        if (string.IsNullOrWhiteSpace(credential.RefreshToken))
        {
            if (allowCurrentOnUnavailable && !NeedsRefresh(credential.ExpiresAt, credential.AccessToken, now, TimeSpan.Zero))
            {
                return credential;
            }

            throw new GeminiDataException("Gemini OAuth token expired and no refresh token is available.");
        }

        string? clientId = credential.ClientId;
        string? clientSecret = credential.ClientSecret;
        if (clientId == null && clientSecret == null)
        {
            string prefix = credential.PreferGeminiCliClient ? "CODEXTRAY_GEMINI_CLI" : "CODEXTRAY_ANTIGRAVITY";
            clientId = Environment.GetEnvironmentVariable(prefix + "_CLIENT_ID");
            clientSecret = Environment.GetEnvironmentVariable(prefix + "_CLIENT_SECRET");
        }
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new GeminiDataException("OAuth client configuration is incomplete. Renew the login in Antigravity or Gemini CLI, then refresh.");
        }

        if (credential.SourceFilePath != null)
        {
            bool supportsWriteback;
            try
            {
                supportsWriteback = JsonNode.Parse(credential.SourceFileContents!) is JsonObject &&
                    AtomicFile.CanReplaceAllText(credential.SourceFilePath, credential.SourceFileContents!);
            }
            catch (JsonException)
            {
                supportsWriteback = false;
            }

            if (!supportsWriteback)
            {
                EnsureCredentialUnchanged(credential);
                if (allowCurrentOnUnavailable && !NeedsRefresh(credential.ExpiresAt, credential.AccessToken, now, TimeSpan.Zero))
                {
                    return credential;
                }

                throw new GeminiDataException("Gemini OAuth file cannot be updated safely. Renew the login in Antigravity or Gemini CLI.");
            }
        }

        OAuthTokenResponse refreshed = await OAuthTokenHelpers.RefreshAsync(
            m_HttpClient,
            m_TokenRefreshUrl,
            clientId,
            credential.RefreshToken,
            "Gemini",
            cancellationToken,
            clientSecret).ConfigureAwait(false);

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

        if (!string.IsNullOrWhiteSpace(credential.SourceFilePath))
        {
            updated = SaveUpdatedCredentialFile(credential, updated);
        }

        return updated;
    }

    /// <summary>
    /// Persists refreshed OAuth tokens atomically back to a writable local JSON credential file.
    /// </summary>
    private static GeminiCredential SaveUpdatedCredentialFile(GeminiCredential original, GeminiCredential updated)
    {
        string filePath = original.SourceFilePath!;
        string rawText = File.ReadAllText(filePath);
        if (!string.Equals(rawText, original.SourceFileContents, StringComparison.Ordinal))
        {
            throw new GeminiDataException("Gemini OAuth credentials changed. Refresh again.");
        }

        JsonObject rootObject = JsonNode.Parse(rawText) as JsonObject ?? throw new GeminiDataException("Gemini OAuth file has an invalid format.");

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
            throw new GeminiDataException("Gemini OAuth credentials changed. Refresh again.");
        }

        return updated with { SourceFileContents = contents };
    }

    private void EnsureCredentialUnchanged(GeminiCredential original)
    {
        if (!TryLoadCredential(ResolveGeminiHomePath(m_ExplicitGeminiHomePath), out GeminiCredential current, out _) ||
            (original.SourceFilePath == null
                ? current != original
                : !string.Equals(current.SourceFilePath, original.SourceFilePath, StringComparison.Ordinal) ||
                  !string.Equals(current.SourceFileContents, original.SourceFileContents, StringComparison.Ordinal)))
        {
            throw new GeminiDataException("Gemini OAuth credentials changed. Refresh again.");
        }
    }

    /// <summary>
    /// Searches local files, Windows Credential Manager, and Antigravity IDE state storage for OAuth credentials.
    /// </summary>
    private bool TryLoadCredential(string geminiHome, out GeminiCredential credential, out string error)
    {
        credential = default!;
        (string Path, bool PreferGeminiCliClient)[] candidateFiles =
        [
            (Path.Combine(geminiHome, "antigravity-cli", "antigravity-oauth-token"), false),
            (Path.Combine(geminiHome, "oauth_creds.json"), true),
            (Path.Combine(geminiHome, "antigravity", "oauth_creds.json"), false),
        ];

        foreach ((string candidateFile, bool preferGeminiCliClient) in candidateFiles)
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
                    credential = credential with { SourceFileContents = contents };
                    error = string.Empty;
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Continue checking remaining credential sources.
            }
        }

        if (m_CredentialStoreReader != null)
        {
            string? injected = m_CredentialStoreReader();
            if (!string.IsNullOrWhiteSpace(injected) && TryParseCredentialPayload(injected, sourceFilePath: null, preferGeminiCliClient: false, out credential))
            {
                error = string.Empty;
                return true;
            }
        }
        else if (m_UseSystemCredentialStores && OperatingSystem.IsWindows())
        {
            foreach (string target in new[] { "gemini:antigravity", "gemini-cli" })
            {
                string? secretBlob = TryReadWindowsGenericCredential(target);
                if (!string.IsNullOrWhiteSpace(secretBlob) && TryParseCredentialPayload(secretBlob, sourceFilePath: null, preferGeminiCliClient: target == "gemini-cli", out credential))
                {
                    error = string.Empty;
                    return true;
                }
            }
        }

        if (m_UseSystemCredentialStores && TryReadAntigravityStateDatabase(out credential))
        {
            error = string.Empty;
            return true;
        }

        error = "Gemini OAuth credentials unavailable. Sign in with Antigravity or gemini first.";
        return false;
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
    private static bool TryReadAntigravityStateDatabase(out GeminiCredential credential)
    {
        credential = default!;
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
                    return true;
                }
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                // Ignore inaccessible or older Antigravity state databases.
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
            string tierHint = ExtractTierFromObject(root);
            credential = new GeminiCredential(
                SourceFilePath: sourceFilePath,
                AccessToken: accessToken,
                RefreshToken: string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken,
                ExpiresAt: expiresAt,
                ClientId: string.IsNullOrWhiteSpace(clientId) ? null : clientId,
                ClientSecret: string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret,
                TierHint: tierHint,
                PreferGeminiCliClient: preferGeminiCliClient);
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
    }

    private sealed class GeminiDataException(string message) : InvalidOperationException(message);

    private sealed class GeminiAuthException(string message) : InvalidOperationException(message);
}
