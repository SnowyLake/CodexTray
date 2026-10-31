using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
    // Grok Build reads credits here. grok.com/rest/grok/credits returns a Cloudflare challenge to non-browser clients.
    private const string k_CreditsEndpoint = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    // Grok Build reads the badge from this settings object. /v1/user subscriptionTier is a different name, such as GrokPro.
    private const string k_SettingsEndpoint = "https://cli-chat-proxy.grok.com/v1/settings";
    private const string k_TokenEndpoint = "https://auth.x.ai/oauth2/token";
    private const string k_DefaultClientId = "b1a00492-073a-47ea-816f-4c329264a828";
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
            usage = await FetchCreditsAsync(accessToken, userId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (IsAuthFailure(exception))
        {
            (accessToken, userId) = await ResolveGrokBuildAccessTokenAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            usage = await FetchCreditsAsync(accessToken, userId, cancellationToken).ConfigureAwait(false);
        }

        string subscriptionTier = await TryFetchSettingsSubscriptionTierAsync(accessToken, userId, cancellationToken).ConfigureAwait(false);
        if (subscriptionTier.Length == 0 && TryLoadLocalSubscriptionTier(accessToken, out string localTier))
        {
            subscriptionTier = localTier;
        }

        return subscriptionTier.Length > 0
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
            string error = exception is GrokDataException ? exception.Message : UsageDiagnostics.Error(exception);
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
            throw new GrokDataException("Could not parse Grok billing usage.");
        }

        bool hasCurrentPeriod = config.TryGetProperty("currentPeriod", out JsonElement currentPeriod) &&
            currentPeriod.ValueKind == JsonValueKind.Object;
        double usedPercent = 0;
        if (config.TryGetProperty("creditUsagePercent", out JsonElement usedPercentElement))
        {
            if (usedPercentElement.ValueKind != JsonValueKind.Number || !usedPercentElement.TryGetDouble(out usedPercent) || !double.IsFinite(usedPercent))
            {
                throw new GrokDataException("Could not parse Grok billing usage.");
            }
        }
        else if (!hasCurrentPeriod)
        {
            throw new GrokDataException("Could not parse Grok billing usage.");
        }

        string resetText = hasCurrentPeriod && currentPeriod.TryGetProperty("end", out JsonElement periodEnd) && periodEnd.ValueKind == JsonValueKind.String
            ? periodEnd.GetString() ?? string.Empty
            : config.TryGetProperty("billingPeriodEnd", out JsonElement billingPeriodEnd) && billingPeriodEnd.ValueKind == JsonValueKind.String
                ? billingPeriodEnd.GetString() ?? string.Empty
                : string.Empty;
        if (!DateTimeOffset.TryParse(resetText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset resetsAt))
        {
            throw new GrokDataException("Could not parse Grok billing usage.");
        }

        List<GrokProductUsage> productUsage = [];
        if (config.TryGetProperty("productUsage", out JsonElement productUsageElement) && productUsageElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in productUsageElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string product = string.Empty;
                if (item.TryGetProperty("product", out JsonElement productElement))
                {
                    if (productElement.ValueKind == JsonValueKind.String)
                    {
                        product = productElement.GetString()?.Trim() ?? string.Empty;
                    }
                    else if (productElement.ValueKind != JsonValueKind.Null)
                    {
                        continue;
                    }
                }

                double productUsedPercent = 0;
                bool hasUsagePercent = item.TryGetProperty("usagePercent", out JsonElement productPercentElement);
                if (hasUsagePercent &&
                    (productPercentElement.ValueKind != JsonValueKind.Number ||
                     !productPercentElement.TryGetDouble(out productUsedPercent) ||
                     !double.IsFinite(productUsedPercent)))
                {
                    continue;
                }

                if (product.Length == 0)
                {
                    if (!hasUsagePercent)
                    {
                        continue;
                    }

                    product = "Cursor";
                }

                productUsage.Add(new GrokProductUsage(product, Math.Clamp(productUsedPercent, 0, 100)));
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
    /// Reads the Grok Build settings display name, then <c>subscription_tier</c> when the display is empty.
    /// </summary>
    public static string ParseSettingsSubscriptionTier(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        string display = ReadJsonString(document.RootElement, "subscription_tier_display").Trim();
        if (display.Length > 0)
        {
            return display;
        }

        return ReadJsonString(document.RootElement, "subscription_tier").Trim();
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
                throw new GrokDataException(error);
            }

            if (!forceRefresh && !NeedsRefresh(credential.ExpiresAt, credential.AccessToken))
            {
                return (credential.AccessToken, credential.UserId);
            }

            if (string.IsNullOrWhiteSpace(credential.RefreshToken))
            {
                throw new GrokDataException(forceRefresh
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
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset expiresAt = response.ExpiresInSeconds is double seconds
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : OAuthTokenHelpers.TryGetJwtExpiry(response.AccessToken, out DateTimeOffset jwtExpiry)
                ? jwtExpiry
                : DateTimeOffset.UtcNow.AddHours(1);

        return new OAuthTokenRefresh(response.AccessToken, response.RefreshToken, response.IdToken, expiresAt);
    }

    /// <summary>
    /// Requests weekly Grok credits for one access token.
    /// </summary>
    private async Task<GrokUsageSnapshot> FetchCreditsAsync(string accessToken, string userId, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, k_CreditsEndpoint);
        ApplyCliProxyHeaders(request, accessToken, userId);

        using HttpResponseMessage response = await UsageHttp.SendAsync(m_HttpClient, request, cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new GrokDataException("Grok Build OAuth token expired or unauthorized. Run grok login again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UsageHttpException(response.StatusCode);
        }

        return ParseCreditsResponse(responseBody);
    }

    /// <summary>
    /// Reads the subscription badge from Grok Build settings. A failed request leaves the local backfill in place.
    /// </summary>
    private async Task<string> TryFetchSettingsSubscriptionTierAsync(string accessToken, string userId, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, k_SettingsEndpoint);
            ApplyCliProxyHeaders(request, accessToken, userId);
            using HttpResponseMessage response = await UsageHttp.SendAsync(m_HttpClient, request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseSettingsSubscriptionTier(responseBody);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or
            InvalidOperationException or IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Applies the CLI-proxy auth headers used by Grok Build billing and settings.
    /// </summary>
    private static void ApplyCliProxyHeaders(HttpRequestMessage request, string accessToken, string userId)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("X-XAI-Token-Auth", "xai-grok-cli");
        if (!string.IsNullOrWhiteSpace(userId))
        {
            request.Headers.TryAddWithoutValidation("x-userid", userId);
        }

        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("CodexTray");
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
        JsonNode root = JsonNode.Parse(text) ?? throw new GrokDataException("Grok Build OAuth file has an invalid format.");
        if (root is not JsonObject rootObject)
        {
            throw new GrokDataException("Grok Build OAuth file has an invalid format.");
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
    /// Used when settings does not provide a display name or tier.
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
    /// Reads one string property, treating missing or non-string values as empty.
    /// </summary>
    private static string ReadJsonString(JsonElement value, string name)
    {
        return value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
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
        return exception is GrokDataException && exception.Message.Contains("expired or unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Identifies subscription display names used to backfill the badge from local logs and auth.
    /// </summary>
    private static bool LooksLikeSubscriptionTier(string value)
    {
        string normalized = value.Trim().Replace('_', ' ');
        // Plus, Super, and Ultra are exact X Subscription display names. A "Super" substring would also match unrelated text.
        if (string.Equals(normalized, "Free", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Plus", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Super", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Ultra", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.Contains("Premium", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("SuperGrok", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Business", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Enterprise", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record OAuthTokenRefresh(string AccessToken, string RefreshToken, string? IdToken, DateTimeOffset ExpiresAt);

    private sealed record GrokBuildCredential(
        string AuthPath,
        string EntryKey,
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt,
        string ClientId,
        string UserId);

    private sealed class GrokDataException(string message) : InvalidOperationException(message);
}
