using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexTray.Core;

public sealed record GrokUsageSnapshot(double UsedPercent, long ResetsAt);

public sealed class GrokUsageCollector
{
    private const string k_BillingEndpoint = "https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig";
    private const string k_TokenEndpoint = "https://auth.x.ai/oauth2/token";
    private const string k_DefaultClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    private const long k_MinimumUnixTimestamp = 1_700_000_000;
    private const long k_MaximumUnixTimestamp = 2_100_000_000;
    private static readonly TimeSpan s_RefreshBuffer = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim s_GrokBuildAuthLock = new(1, 1);
    private static readonly SemaphoreSlim s_OpenCodeAuthLock = new(1, 1);

    private readonly HttpClient m_HttpClient;

    /// <summary>
    /// Creates a collector using the supplied HTTP client.
    /// </summary>
    public GrokUsageCollector(HttpClient httpClient)
    {
        m_HttpClient = httpClient;
    }

    /// <summary>
    /// Collects Grok billing usage with the selected local OAuth source.
    /// </summary>
    public async Task<GrokUsageSnapshot> CollectAsync(string oauthSource, CancellationToken cancellationToken = default)
    {
        string accessToken = await ResolveAccessTokenAsync(oauthSource, forceRefresh: false, cancellationToken).ConfigureAwait(false);
        try
        {
            return await FetchBillingAsync(accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (IsAuthFailure(exception))
        {
            accessToken = await ResolveAccessTokenAsync(oauthSource, forceRefresh: true, cancellationToken).ConfigureAwait(false);
            return await FetchBillingAsync(accessToken, cancellationToken).ConfigureAwait(false);
        }
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
    /// Resolves a usable access token, refreshing and persisting credentials when needed.
    /// </summary>
    private async Task<string> ResolveAccessTokenAsync(string oauthSource, bool forceRefresh, CancellationToken cancellationToken)
    {
        return oauthSource == ApiMonitorSettings.OpenCodeOAuthSource
            ? await ResolveOpenCodeAccessTokenAsync(forceRefresh, cancellationToken).ConfigureAwait(false)
            : await ResolveGrokBuildAccessTokenAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads Grok Build credentials and refreshes them when expired or forced.
    /// </summary>
    private async Task<string> ResolveGrokBuildAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
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
                return credential.AccessToken;
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
            return refresh.AccessToken;
        }
        finally
        {
            s_GrokBuildAuthLock.Release();
        }
    }

    /// <summary>
    /// Loads OpenCode credentials and refreshes them when expired or forced.
    /// </summary>
    private async Task<string> ResolveOpenCodeAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await s_OpenCodeAuthLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryLoadOpenCodeCredential(out OpenCodeCredential credential, out string error))
            {
                throw new InvalidOperationException(error);
            }

            if (!forceRefresh && !NeedsRefresh(credential.ExpiresAt, credential.AccessToken))
            {
                return credential.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(credential.RefreshToken))
            {
                throw new InvalidOperationException(forceRefresh
                    ? "OpenCode xAI OAuth token expired or unauthorized and no refresh token is available. Reconnect xAI in OpenCode."
                    : "OpenCode xAI OAuth token expired. Use Grok in OpenCode to refresh it.");
            }

            OAuthTokenRefresh refresh = await RefreshOAuthTokenAsync(credential.RefreshToken, k_DefaultClientId, cancellationToken)
                .ConfigureAwait(false);
            SaveOpenCodeCredential(credential, refresh);
            return refresh.AccessToken;
        }
        finally
        {
            s_OpenCodeAuthLock.Release();
        }
    }

    /// <summary>
    /// Requests a new access token from the xAI OAuth token endpoint.
    /// </summary>
    private async Task<OAuthTokenRefresh> RefreshOAuthTokenAsync(string refreshToken, string clientId, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, k_TokenEndpoint);
        request.Content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
        ]);

        using HttpResponseMessage response = await m_HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Grok OAuth refresh failed: HTTP {(int)response.StatusCode}. Run the selected OAuth source login again.");
        }

        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("access_token", out JsonElement accessToken) ||
            accessToken.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(accessToken.GetString()))
        {
            throw new InvalidOperationException("Grok OAuth refresh response did not include an access token.");
        }

        string? nextRefresh = refreshToken;
        if (document.RootElement.TryGetProperty("refresh_token", out JsonElement refreshed) &&
            refreshed.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(refreshed.GetString()))
        {
            nextRefresh = refreshed.GetString();
        }

        string? idToken = null;
        if (document.RootElement.TryGetProperty("id_token", out JsonElement id) &&
            id.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(id.GetString()))
        {
            idToken = id.GetString();
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

        return new OAuthTokenRefresh(accessToken.GetString()!, nextRefresh!, idToken, expiresAt);
    }

    /// <summary>
    /// Sends the Grok billing gRPC-web request for one access token.
    /// </summary>
    private async Task<GrokUsageSnapshot> FetchBillingAsync(string accessToken, CancellationToken cancellationToken)
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
            throw new InvalidOperationException("Grok OAuth token expired or unauthorized. Refresh the selected OAuth source.");
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

        return new GrokBuildCredential(authPath, entryKey, accessToken, refreshToken, expiresAt, clientId);
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
        File.WriteAllText(credential.AuthPath, rootObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Loads the OpenCode xAI OAuth credential from the first available auth.json.
    /// </summary>
    private static bool TryLoadOpenCodeCredential(out OpenCodeCredential credential, out string error)
    {
        credential = default!;
        error = string.Empty;
        foreach (string authPath in GetOpenCodeAuthPaths())
        {
            if (!File.Exists(authPath))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(authPath));
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("xai", out JsonElement xai) ||
                    xai.ValueKind != JsonValueKind.Object ||
                    !xai.TryGetProperty("type", out JsonElement type) ||
                    !string.Equals(type.GetString(), "oauth", StringComparison.OrdinalIgnoreCase) ||
                    !xai.TryGetProperty("access", out JsonElement access) ||
                    string.IsNullOrWhiteSpace(access.GetString()))
                {
                    continue;
                }

                string? refreshToken = null;
                if (xai.TryGetProperty("refresh", out JsonElement refresh) &&
                    refresh.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(refresh.GetString()))
                {
                    refreshToken = refresh.GetString();
                }

                DateTimeOffset? expiresAt = null;
                if (xai.TryGetProperty("expires", out JsonElement expires) &&
                    expires.TryGetInt64(out long expiryMilliseconds))
                {
                    expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiryMilliseconds);
                }

                credential = new OpenCodeCredential(authPath, access.GetString()!, refreshToken, expiresAt);
                return true;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                continue;
            }
        }

        error = "OpenCode xAI OAuth credentials were not found. Connect xAI Grok OAuth in OpenCode first.";
        return false;
    }

    /// <summary>
    /// Writes refreshed OpenCode xAI tokens back into auth.json without dropping other providers.
    /// </summary>
    private static void SaveOpenCodeCredential(OpenCodeCredential credential, OAuthTokenRefresh refresh)
    {
        string text = File.ReadAllText(credential.AuthPath);
        JsonNode root = JsonNode.Parse(text) ?? throw new InvalidOperationException("OpenCode OAuth file has an invalid format.");
        if (root is not JsonObject rootObject)
        {
            throw new InvalidOperationException("OpenCode OAuth file has an invalid format.");
        }

        JsonObject xaiObject = rootObject["xai"] as JsonObject ?? new JsonObject();
        xaiObject["type"] = "oauth";
        xaiObject["access"] = refresh.AccessToken;
        xaiObject["refresh"] = refresh.RefreshToken;
        xaiObject["expires"] = refresh.ExpiresAt.ToUnixTimeMilliseconds();
        rootObject["xai"] = xaiObject;
        File.WriteAllText(credential.AuthPath, rootObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
    /// Returns the OpenCode authentication file locations supported on Windows.
    /// </summary>
    private static IEnumerable<string> GetOpenCodeAuthPaths()
    {
        List<string> paths = [];
        string? xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
        {
            paths.Add(Path.Combine(xdgDataHome, "opencode", "auth.json"));
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        paths.Add(Path.Combine(localAppData, "opencode", "auth.json"));
        paths.Add(Path.Combine(appData, "opencode", "auth.json"));
        paths.Add(Path.Combine(userProfile, ".local", "share", "opencode", "auth.json"));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns whether credentials should be refreshed before the next billing call.
    /// </summary>
    private static bool NeedsRefresh(DateTimeOffset? expiresAt, string accessToken)
    {
        DateTimeOffset? effectiveExpiry = expiresAt;
        if (TryGetJwtExpiry(accessToken, out DateTimeOffset jwtExpiry))
        {
            effectiveExpiry = effectiveExpiry is DateTimeOffset stored
                ? (stored < jwtExpiry ? stored : jwtExpiry)
                : jwtExpiry;
        }

        return effectiveExpiry is DateTimeOffset expiry && expiry <= DateTimeOffset.UtcNow + s_RefreshBuffer;
    }

    /// <summary>
    /// Reads the JWT exp claim without validating the signature.
    /// </summary>
    private static bool TryGetJwtExpiry(string token, out DateTimeOffset expiry)
    {
        expiry = default;
        string[] parts = token.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2:
                    payload += "==";
                    break;
                case 3:
                    payload += "=";
                    break;
            }

            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!document.RootElement.TryGetProperty("exp", out JsonElement exp) ||
                !exp.TryGetInt64(out long seconds))
            {
                return false;
            }

            expiry = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or OverflowException)
        {
            return false;
        }
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
                ? "Grok OAuth token expired or unauthorized. Refresh the selected OAuth source."
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
                ? "Grok OAuth token expired or unauthorized. Refresh the selected OAuth source."
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
        string ClientId);

    private sealed record OpenCodeCredential(
        string AuthPath,
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt);
}
