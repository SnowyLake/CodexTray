using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexTray.Core;

public sealed record GrokProductUsage(string Product, double UsedPercent);

public sealed record GrokUsageSnapshot(double UsedPercent, long ResetsAt, string SubscriptionTier = "")
{
    public IReadOnlyList<GrokProductUsage> ProductUsage { get; init; } = [];
}

public sealed record GrokUsageDashboard(GrokUsageSnapshot? Usage, string Error, DateTimeOffset UpdatedAt);

public sealed class GrokUsageCollector
{
    private const string k_CreditsEndpoint = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    private const string k_BillingEndpoint = "https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig";
    private const string k_TokenEndpoint = "https://auth.x.ai/oauth2/token";
    private const string k_DefaultClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    private const long k_MinimumUnixTimestamp = 1_700_000_000;
    private const long k_MaximumUnixTimestamp = 2_100_000_000;
    private static readonly TimeSpan s_RefreshBuffer = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim s_GrokBuildAuthLock = new(1, 1);
    private static readonly HttpClient s_HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private readonly HttpClient m_HttpClient;
    private string? m_SubscriptionLogPath;
    private long m_SubscriptionLogLength;
    private DateTime m_SubscriptionLogWriteUtc;
    private string m_CachedLogSubscriptionTier = string.Empty;
    private bool m_SubscriptionLogScanned;

    /// <summary>
    /// Creates a collector using the shared HTTP client.
    /// </summary>
    public GrokUsageCollector()
        : this(s_HttpClient)
    {
    }

    /// <summary>
    /// Creates a collector using the supplied HTTP client.
    /// </summary>
    public GrokUsageCollector(HttpClient httpClient)
    {
        m_HttpClient = httpClient;
    }

    /// <summary>
    /// Collects Grok billing usage with the local Grok Build OAuth session.
    /// </summary>
    public async Task<GrokUsageSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        (string accessToken, string userId) = await ResolveGrokBuildAccessTokenAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        GrokUsageSnapshot usage;
        try
        {
            usage = await FetchBillingAsync(accessToken, userId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (IsAuthFailure(exception))
        {
            (accessToken, userId) = await ResolveGrokBuildAccessTokenAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            usage = await FetchBillingAsync(accessToken, userId, cancellationToken).ConfigureAwait(false);
        }

        return TryLoadLocalSubscriptionTier(accessToken, out string subscriptionTier)
            ? usage with { SubscriptionTier = subscriptionTier }
            : usage;
    }

    /// <summary>
    /// Collects a display-ready Grok dashboard without failing sibling refreshes.
    /// </summary>
    public async Task<GrokUsageDashboard> CollectDashboardAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            GrokUsageSnapshot usage = await CollectAsync(cancellationToken).ConfigureAwait(false);
            return new GrokUsageDashboard(usage, string.Empty, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            string error = exception is TaskCanceledException ? "Request timed out" : exception.Message;
            return new GrokUsageDashboard(null, error, DateTimeOffset.Now);
        }
    }

    /// <summary>
    /// Builds the Grok Weekly value exposed to monitor plugins.
    /// </summary>
    public static GrokPluginUsage BuildPluginUsage(GrokUsageDashboard dashboard)
    {
        GrokUsageSnapshot? usage = dashboard.Usage;
        if (usage == null)
        {
            return new GrokPluginUsage(
                new UsageLimit { Name = "weekly", UsedPercent = 100, RemainingPercent = 0 },
                CodexTrayDefaults.UnavailableDisplay);
        }

        int usedPercent = (int)Math.Round(Math.Clamp(usage.UsedPercent, 0, 100), MidpointRounding.AwayFromZero);
        int remainingPercent = 100 - usedPercent;
        return new GrokPluginUsage(
            new UsageLimit
            {
                Name = "weekly",
                UsedPercent = usedPercent,
                RemainingPercent = remainingPercent,
                WindowMinutes = 10080,
                ResetsAt = usage.ResetsAt,
            },
            $"{remainingPercent}%");
    }

    /// <summary>
    /// Parses the JSON credits response used by the official Grok Build client.
    /// </summary>
    public static GrokUsageSnapshot ParseCreditsResponse(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("config", out JsonElement config) ||
            config.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Could not parse Grok billing usage.");
        }

        bool hasCurrentPeriod = config.TryGetProperty("currentPeriod", out JsonElement currentPeriod) &&
            currentPeriod.ValueKind == JsonValueKind.Object;
        double usedPercent = 0;
        if (config.TryGetProperty("creditUsagePercent", out JsonElement usedPercentElement))
        {
            if (usedPercentElement.ValueKind != JsonValueKind.Number || !usedPercentElement.TryGetDouble(out usedPercent) || !double.IsFinite(usedPercent))
            {
                throw new InvalidOperationException("Could not parse Grok billing usage.");
            }
        }
        else if (!hasCurrentPeriod)
        {
            throw new InvalidOperationException("Could not parse Grok billing usage.");
        }

        string resetText = hasCurrentPeriod && currentPeriod.TryGetProperty("end", out JsonElement periodEnd) && periodEnd.ValueKind == JsonValueKind.String
            ? periodEnd.GetString() ?? string.Empty
            : config.TryGetProperty("billingPeriodEnd", out JsonElement billingPeriodEnd) && billingPeriodEnd.ValueKind == JsonValueKind.String
                ? billingPeriodEnd.GetString() ?? string.Empty
                : string.Empty;
        if (!DateTimeOffset.TryParse(resetText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset resetsAt))
        {
            throw new InvalidOperationException("Could not parse Grok billing usage.");
        }

        List<GrokProductUsage> productUsage = [];
        if (config.TryGetProperty("productUsage", out JsonElement productUsageElement) && productUsageElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in productUsageElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("product", out JsonElement productElement) ||
                    productElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(productElement.GetString()))
                {
                    continue;
                }

                double productUsedPercent = 0;
                if (item.TryGetProperty("usagePercent", out JsonElement productPercentElement) &&
                    (productPercentElement.ValueKind != JsonValueKind.Number ||
                     !productPercentElement.TryGetDouble(out productUsedPercent) ||
                     !double.IsFinite(productUsedPercent)))
                {
                    continue;
                }

                productUsage.Add(new GrokProductUsage(productElement.GetString()!.Trim(), Math.Clamp(productUsedPercent, 0, 100)));
            }
        }

        string subscriptionTier = GetSubscriptionTierProperty(document.RootElement);
        if (subscriptionTier.Length == 0)
        {
            subscriptionTier = GetSubscriptionTierProperty(config);
        }

        return new GrokUsageSnapshot(Math.Clamp(usedPercent, 0, 100), resetsAt.ToUnixTimeSeconds(), subscriptionTier)
        {
            ProductUsage = productUsage,
        };
    }

    /// <summary>
    /// Parses a Grok billing gRPC-web or raw protobuf response.
    /// </summary>
    public static GrokUsageSnapshot ParseGrpcWebResponse(byte[] responseBody, DateTimeOffset now)
    {
        List<byte[]> payloads = ExtractProtobufPayloads(responseBody);
        ProtobufScan scan = new();
        foreach (byte[] payload in payloads)
        {
            ScanProtobuf(payload, 0, [], scan);
        }

        Fixed32Field? percentField = scan.Fixed32Fields
            .Where(field => field.Path[^1] == 1 && float.IsFinite(field.Value) && field.Value >= 0 && field.Value <= 100)
            .OrderBy(field => field.Path.Count)
            .ThenBy(field => field.Order)
            .FirstOrDefault();
        VarintField? resetField = scan.VarintFields
            .Where(field => field.Value >= k_MinimumUnixTimestamp && field.Value <= k_MaximumUnixTimestamp && field.Value > (ulong)now.ToUnixTimeSeconds())
            .OrderBy(field => PathEquals(field.Path, 1, 5, 1) ? 0 : 1)
            .ThenBy(field => field.Value)
            .FirstOrDefault();
        bool hasUsagePeriod = scan.VarintFields.Any(field =>
            PathStartsWith(field.Path, 1, 6) ||
            (PathEquals(field.Path, 1, 8, 1) && (field.Value == 1 || field.Value == 2)));
        if (resetField == null || (percentField == null && !hasUsagePeriod))
        {
            throw new InvalidOperationException("Could not parse Grok billing usage.");
        }

        return new GrokUsageSnapshot(percentField?.Value ?? 0, (long)resetField.Value);
    }

    /// <summary>
    /// Loads Grok Build credentials and refreshes them when expired or forced.
    /// </summary>
    private async Task<(string AccessToken, string UserId)> ResolveGrokBuildAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await s_GrokBuildAuthLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryLoadGrokBuildCredential(out GrokBuildCredential credential, out string error))
            {
                throw new InvalidOperationException(error);
            }

            if (!forceRefresh && !NeedsRefresh(credential.ExpiresAt, credential.AccessToken))
            {
                return (credential.AccessToken, credential.UserId);
            }

            if (string.IsNullOrWhiteSpace(credential.RefreshToken))
            {
                throw new InvalidOperationException(forceRefresh
                    ? "Grok Build OAuth token expired or unauthorized and no refresh token is available. Run grok login."
                    : "Grok Build OAuth token expired. Run grok login to refresh it.");
            }

            OAuthTokenRefresh refresh = await RefreshOAuthTokenAsync(credential.RefreshToken, credential.ClientId, cancellationToken)
                .ConfigureAwait(false);
            SaveGrokBuildCredential(credential, refresh);
            return (refresh.AccessToken, credential.UserId);
        }
        finally
        {
            s_GrokBuildAuthLock.Release();
        }
    }

    /// <summary>
    /// Requests a new access token from the xAI OAuth token endpoint.
    /// </summary>
    private async Task<OAuthTokenRefresh> RefreshOAuthTokenAsync(string refreshToken, string clientId, CancellationToken cancellationToken)
    {
        OAuthTokenResponse response = await OAuthTokenHelpers.RefreshAsync(
            m_HttpClient,
            k_TokenEndpoint,
            clientId,
            refreshToken,
            "Grok",
            "Run grok login again.",
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset expiresAt = response.ExpiresInSeconds is double seconds
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : OAuthTokenHelpers.TryGetJwtExpiry(response.AccessToken, out DateTimeOffset jwtExpiry)
                ? jwtExpiry
                : DateTimeOffset.UtcNow.AddHours(1);

        return new OAuthTokenRefresh(response.AccessToken, response.RefreshToken, response.IdToken, expiresAt);
    }

    /// <summary>
    /// Fetches Grok credits through the official JSON endpoint with a gRPC-web fallback.
    /// </summary>
    private async Task<GrokUsageSnapshot> FetchBillingAsync(string accessToken, string userId, CancellationToken cancellationToken)
    {
        try
        {
            return await FetchCreditsAsync(accessToken, userId, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (JsonException)
        {
        }
        catch (FormatException)
        {
        }
        catch (OverflowException)
        {
        }
        catch (InvalidOperationException exception) when (!IsAuthFailure(exception))
        {
        }

        return await FetchGrpcWebBillingAsync(accessToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the Grok credits JSON request for one access token.
    /// </summary>
    private async Task<GrokUsageSnapshot> FetchCreditsAsync(string accessToken, string userId, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, k_CreditsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("X-XAI-Token-Auth", "xai-grok-cli");
        if (!string.IsNullOrWhiteSpace(userId))
        {
            request.Headers.TryAddWithoutValidation("x-userid", userId);
        }

        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("CodexTray");

        using HttpResponseMessage response = await m_HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Grok Build OAuth token expired or unauthorized. Run grok login again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Grok credits request failed: HTTP {(int)response.StatusCode}");
        }

        return ParseCreditsResponse(responseBody);
    }

    /// <summary>
    /// Sends the Grok billing gRPC-web fallback request for one access token.
    /// </summary>
    private async Task<GrokUsageSnapshot> FetchGrpcWebBillingAsync(string accessToken, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, k_BillingEndpoint);
        using ByteArrayContent content = new([0, 0, 0, 0, 0]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web+proto");
        request.Content = content;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Origin", "https://grok.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://grok.com/?_s=usage");
        request.Headers.TryAddWithoutValidation("x-grpc-web", "1");
        request.Headers.TryAddWithoutValidation("x-user-agent", "connect-es/2.1.1");
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.UserAgent.ParseAdd("CodexTray");

        using HttpResponseMessage response = await m_HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        byte[] responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Grok Build OAuth token expired or unauthorized. Run grok login again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Grok billing request failed: HTTP {(int)response.StatusCode}");
        }

        ValidateGrpcStatus(response.Headers);
        return ParseGrpcWebResponse(responseBody, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Loads the preferred Grok Build credential entry from auth.json.
    /// </summary>
    private static bool TryLoadGrokBuildCredential(out GrokBuildCredential credential, out string error)
    {
        credential = default!;
        error = string.Empty;
        string authPath = GetGrokBuildAuthPath();
        if (!File.Exists(authPath))
        {
            error = "Grok Build OAuth file was not found. Run grok login first.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(authPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Grok Build OAuth file has an invalid format.";
                return false;
            }

            string? fallbackKey = null;
            JsonElement? fallbackEntry = null;
            foreach (JsonProperty entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object ||
                    !entry.Value.TryGetProperty("key", out JsonElement key) ||
                    key.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(key.GetString()))
                {
                    continue;
                }

                if (entry.Name.StartsWith("https://auth.x.ai::", StringComparison.Ordinal))
                {
                    credential = CreateGrokBuildCredential(authPath, entry.Name, entry.Value);
                    return true;
                }

                if (entry.Name.Contains("/sign-in", StringComparison.Ordinal))
                {
                    fallbackKey = entry.Name;
                    fallbackEntry = entry.Value;
                }
            }

            if (fallbackKey != null && fallbackEntry is JsonElement fallback)
            {
                credential = CreateGrokBuildCredential(authPath, fallbackKey, fallback);
                return true;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = "Grok Build OAuth file could not be read.";
            return false;
        }

        error = "Grok Build OAuth access token was not found.";
        return false;
    }

    /// <summary>
    /// Builds one Grok Build credential snapshot from an auth.json entry.
    /// </summary>
    private static GrokBuildCredential CreateGrokBuildCredential(string authPath, string entryKey, JsonElement entry)
    {
        string accessToken = entry.GetProperty("key").GetString() ?? string.Empty;
        string? refreshToken = null;
        if (entry.TryGetProperty("refresh_token", out JsonElement refresh) &&
            refresh.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(refresh.GetString()))
        {
            refreshToken = refresh.GetString();
        }
        else if (entry.TryGetProperty("refresh", out JsonElement legacyRefresh) &&
                 legacyRefresh.ValueKind == JsonValueKind.String &&
                 !string.IsNullOrWhiteSpace(legacyRefresh.GetString()))
        {
            refreshToken = legacyRefresh.GetString();
        }

        DateTimeOffset? expiresAt = null;
        if (entry.TryGetProperty("expires_at", out JsonElement expiresAtElement) &&
            DateTimeOffset.TryParse(expiresAtElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsedExpiresAt))
        {
            expiresAt = parsedExpiresAt;
        }
        else if (entry.TryGetProperty("expires", out JsonElement expiresElement) &&
                 DateTimeOffset.TryParse(expiresElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsedExpires))
        {
            expiresAt = parsedExpires;
        }

        string clientId = k_DefaultClientId;
        if (entry.TryGetProperty("oidc_client_id", out JsonElement oidcClientId) &&
            oidcClientId.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(oidcClientId.GetString()))
        {
            clientId = oidcClientId.GetString()!;
        }
        else
        {
            int separator = entryKey.LastIndexOf("::", StringComparison.Ordinal);
            if (separator >= 0)
            {
                string suffix = entryKey[(separator + 2)..].Trim();
                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    clientId = suffix;
                }
            }
        }

        string userId = entry.TryGetProperty("user_id", out JsonElement userIdElement) &&
            userIdElement.ValueKind == JsonValueKind.String
                ? userIdElement.GetString() ?? string.Empty
                : string.Empty;
        return new GrokBuildCredential(authPath, entryKey, accessToken, refreshToken, expiresAt, clientId, userId);
    }

    /// <summary>
    /// Writes refreshed Grok Build tokens back into auth.json without dropping other entries.
    /// </summary>
    private static void SaveGrokBuildCredential(GrokBuildCredential credential, OAuthTokenRefresh refresh)
    {
        string text = File.ReadAllText(credential.AuthPath);
        JsonNode root = JsonNode.Parse(text) ?? throw new InvalidOperationException("Grok Build OAuth file has an invalid format.");
        if (root is not JsonObject rootObject)
        {
            throw new InvalidOperationException("Grok Build OAuth file has an invalid format.");
        }

        JsonObject entryObject = rootObject[credential.EntryKey] as JsonObject ?? new JsonObject();
        entryObject["key"] = refresh.AccessToken;
        entryObject["refresh_token"] = refresh.RefreshToken;
        if (!string.IsNullOrWhiteSpace(refresh.IdToken))
        {
            entryObject["id_token"] = refresh.IdToken;
        }

        entryObject["expires_at"] = refresh.ExpiresAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        rootObject[credential.EntryKey] = entryObject;
        AtomicFile.WriteAllText(credential.AuthPath, rootObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Returns the Grok Build authentication file path.
    /// </summary>
    private static string GetGrokBuildAuthPath()
    {
        string grokHome = Environment.GetEnvironmentVariable("GROK_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");
        return Path.Combine(grokHome, "auth.json");
    }

    /// <summary>
    /// Loads the most recent subscription tier cached by Grok Build, then checks the active auth entry.
    /// </summary>
    private bool TryLoadLocalSubscriptionTier(string accessToken, out string subscriptionTier)
    {
        subscriptionTier = string.Empty;
        string authPath = GetGrokBuildAuthPath();
        string grokHome = Path.GetDirectoryName(authPath)!;
        string logPath = Path.Combine(grokHome, "logs", "unified.jsonl");
        if (File.Exists(logPath))
        {
            try
            {
                FileInfo logInfo = new(logPath);
                if (m_SubscriptionLogScanned &&
                    string.Equals(m_SubscriptionLogPath, logPath, StringComparison.OrdinalIgnoreCase) &&
                    logInfo.Length == m_SubscriptionLogLength &&
                    logInfo.LastWriteTimeUtc == m_SubscriptionLogWriteUtc)
                {
                    if (m_CachedLogSubscriptionTier.Length > 0)
                    {
                        subscriptionTier = m_CachedLogSubscriptionTier;
                        return true;
                    }
                }
                else
                {
                    using FileStream stream = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using StreamReader reader = new(stream);
                    string foundTier = string.Empty;
                    while (reader.ReadLine() is string line)
                    {
                        if (!line.Contains("\"billing: fetched credits config\"", StringComparison.Ordinal) ||
                            !line.Contains("\"subscriptionTier\"", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        try
                        {
                            using JsonDocument document = JsonDocument.Parse(line);
                            JsonElement context = document.RootElement.TryGetProperty("ctx", out JsonElement value) ? value : default;
                            string candidate = GetSubscriptionTierProperty(context);
                            if (LooksLikeSubscriptionTier(candidate))
                            {
                                foundTier = candidate.Trim();
                            }
                        }
                        catch (JsonException)
                        {
                            // Ignore one partially written or legacy Grok Build log line.
                        }
                    }

                    m_SubscriptionLogPath = logPath;
                    m_SubscriptionLogLength = logInfo.Length;
                    m_SubscriptionLogWriteUtc = logInfo.LastWriteTimeUtc;
                    m_CachedLogSubscriptionTier = foundTier;
                    m_SubscriptionLogScanned = true;
                    if (foundTier.Length > 0)
                    {
                        subscriptionTier = foundTier;
                        return true;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Fall through to the active Grok Build auth entry.
            }
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(authPath));
            foreach (JsonProperty entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object ||
                    !entry.Value.TryGetProperty("key", out JsonElement key) ||
                    key.ValueKind != JsonValueKind.String ||
                    !string.Equals(key.GetString(), accessToken, StringComparison.Ordinal))
                {
                    continue;
                }

                string candidate = GetSubscriptionTierProperty(entry.Value);
                if (LooksLikeSubscriptionTier(candidate))
                {
                    subscriptionTier = candidate.Trim();
                    return true;
                }

                return false;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Reads either supported subscription tier property spelling from one JSON object.
    /// </summary>
    private static string GetSubscriptionTierProperty(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (string name in new[] { "subscriptionTier", "subscription_tier" })
        {
            if (value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns whether credentials should be refreshed before the next billing call.
    /// </summary>
    private static bool NeedsRefresh(DateTimeOffset? expiresAt, string accessToken)
    {
        DateTimeOffset? effectiveExpiry = expiresAt;
        if (OAuthTokenHelpers.TryGetJwtExpiry(accessToken, out DateTimeOffset jwtExpiry))
        {
            effectiveExpiry = effectiveExpiry is DateTimeOffset stored
                ? (stored < jwtExpiry ? stored : jwtExpiry)
                : jwtExpiry;
        }

        return effectiveExpiry is DateTimeOffset expiry && expiry <= DateTimeOffset.UtcNow + s_RefreshBuffer;
    }

    /// <summary>
    /// Returns whether a billing failure should trigger one OAuth refresh retry.
    /// </summary>
    private static bool IsAuthFailure(InvalidOperationException exception)
    {
        return exception.Message.Contains("expired or unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates a nonzero gRPC status returned in response headers.
    /// </summary>
    private static void ValidateGrpcStatus(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("grpc-status", out IEnumerable<string>? values) &&
            int.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out int status) &&
            status != 0)
        {
            throw new InvalidOperationException(status == 16
                ? "Grok Build OAuth token expired or unauthorized. Run grok login again."
                : $"Grok billing request failed with gRPC status {status}.");
        }
    }

    /// <summary>
    /// Extracts protobuf messages from a gRPC-web response or accepts raw protobuf.
    /// </summary>
    private static List<byte[]> ExtractProtobufPayloads(byte[] responseBody)
    {
        List<byte[]> payloads = [];
        int index = 0;
        while (index < responseBody.Length)
        {
            if (index + 5 > responseBody.Length)
            {
                payloads.Clear();
                break;
            }

            byte flags = responseBody[index];
            int length = (responseBody[index + 1] << 24) |
                (responseBody[index + 2] << 16) |
                (responseBody[index + 3] << 8) |
                responseBody[index + 4];
            int payloadStart = index + 5;
            int payloadEnd = payloadStart + length;
            if (length < 0 || payloadEnd > responseBody.Length)
            {
                payloads.Clear();
                break;
            }

            if ((flags & 0x80) != 0)
            {
                ValidateGrpcTrailer(responseBody[payloadStart..payloadEnd]);
            }
            else
            {
                payloads.Add(responseBody[payloadStart..payloadEnd]);
            }

            index = payloadEnd;
        }

        if (payloads.Count == 0 && LooksLikeProtobuf(responseBody))
        {
            payloads.Add(responseBody);
        }

        if (payloads.Count == 0)
        {
            throw new InvalidOperationException("Grok billing returned no protobuf payload.");
        }

        return payloads;
    }

    /// <summary>
    /// Validates a gRPC status encoded in a gRPC-web trailer frame.
    /// </summary>
    private static void ValidateGrpcTrailer(byte[] trailer)
    {
        string text = Encoding.UTF8.GetString(trailer);
        foreach (string line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("grpc-status:", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(line["grpc-status:".Length..].Trim(), CultureInfo.InvariantCulture, out int status) ||
                status == 0)
            {
                continue;
            }

            throw new InvalidOperationException(status == 16
                ? "Grok Build OAuth token expired or unauthorized. Run grok login again."
                : $"Grok billing request failed with gRPC status {status}.");
        }
    }

    /// <summary>
    /// Determines whether bytes can start a raw protobuf message.
    /// </summary>
    private static bool LooksLikeProtobuf(byte[] data)
    {
        if (data.Length == 0)
        {
            return false;
        }

        byte first = data[0];
        int fieldNumber = first >> 3;
        int wireType = first & 0x07;
        return fieldNumber > 0 && wireType is 0 or 1 or 2 or 5;
    }

    /// <summary>
    /// Scans nested protobuf fields needed to identify usage and reset values.
    /// </summary>
    private static void ScanProtobuf(byte[] data, int depth, List<ulong> path, ProtobufScan scan)
    {
        int index = 0;
        while (index < data.Length)
        {
            int fieldStart = index;
            if (!TryReadVarint(data, ref index, out ulong key) || key == 0)
            {
                index = fieldStart + 1;
                continue;
            }

            ulong fieldNumber = key >> 3;
            ulong wireType = key & 0x07;
            List<ulong> fieldPath = new(path) { fieldNumber };
            switch (wireType)
            {
                case 0:
                    if (TryReadVarint(data, ref index, out ulong value))
                    {
                        scan.VarintFields.Add(new VarintField(fieldPath, value));
                    }
                    else
                    {
                        index = fieldStart + 1;
                    }

                    break;
                case 1:
                    if (index + 8 > data.Length)
                    {
                        return;
                    }

                    index += 8;
                    break;
                case 2:
                    if (!TryReadVarint(data, ref index, out ulong length) || length > (ulong)(data.Length - index))
                    {
                        index = fieldStart + 1;
                        continue;
                    }

                    int nestedLength = (int)length;
                    if (depth < 4 && nestedLength > 0)
                    {
                        byte[] nested = data[index..(index + nestedLength)];
                        ScanProtobuf(nested, depth + 1, fieldPath, scan);
                    }

                    index += nestedLength;
                    break;
                case 5:
                    if (index + 4 > data.Length)
                    {
                        return;
                    }

                    int bits = data[index] |
                        (data[index + 1] << 8) |
                        (data[index + 2] << 16) |
                        (data[index + 3] << 24);
                    scan.Fixed32Fields.Add(new Fixed32Field(fieldPath, BitConverter.Int32BitsToSingle(bits), scan.NextOrder++));
                    index += 4;
                    break;
                default:
                    index = fieldStart + 1;
                    break;
            }
        }
    }

    /// <summary>
    /// Reads one protobuf varint from the supplied byte array.
    /// </summary>
    private static bool TryReadVarint(byte[] data, ref int index, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (index < data.Length && shift < 64)
        {
            byte current = data[index++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    /// <summary>
    /// Checks whether a protobuf field path exactly matches the expected path.
    /// </summary>
    private static bool PathEquals(IReadOnlyList<ulong> path, params ulong[] expected)
    {
        return path.Count == expected.Length && path.SequenceEqual(expected);
    }

    /// <summary>
    /// Checks whether a protobuf field path starts with the expected prefix.
    /// </summary>
    private static bool PathStartsWith(IReadOnlyList<ulong> path, params ulong[] expected)
    {
        return path.Count >= expected.Length && path.Take(expected.Length).SequenceEqual(expected);
    }

    /// <summary>
    /// Identifies subscription tier strings while allowing future xAI tier names to pass through.
    /// </summary>
    private static bool LooksLikeSubscriptionTier(string value)
    {
        string normalized = value.Trim().Replace('_', ' ');
        return string.Equals(normalized, "Free", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Premium", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("SuperGrok", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Business", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Enterprise", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ProtobufScan
    {
        public List<Fixed32Field> Fixed32Fields { get; } = [];

        public int NextOrder { get; set; }

        public List<VarintField> VarintFields { get; } = [];
    }

    private sealed record Fixed32Field(IReadOnlyList<ulong> Path, float Value, int Order);

    private sealed record VarintField(IReadOnlyList<ulong> Path, ulong Value);

    private sealed record OAuthTokenRefresh(string AccessToken, string RefreshToken, string? IdToken, DateTimeOffset ExpiresAt);

    private sealed record GrokBuildCredential(
        string AuthPath,
        string EntryKey,
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt,
        string ClientId,
        string UserId);

}
