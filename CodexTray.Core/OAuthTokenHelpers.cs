using System.Text.Json;

namespace CodexTray.Core;

internal sealed record OAuthTokenResponse(
    string AccessToken,
    string RefreshToken,
    string? IdToken,
    double? ExpiresInSeconds);

internal static class OAuthTokenHelpers
{
    /// <summary>
    /// Refreshes an OAuth access token and parses the standard response fields.
    /// </summary>
    internal static async Task<OAuthTokenResponse> RefreshAsync(
        HttpClient httpClient,
        string endpoint,
        string clientId,
        string refreshToken,
        string providerLabel,
        CancellationToken cancellationToken,
        string? clientSecret = null)
    {
        List<KeyValuePair<string, string>> parameters =
        [
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
        ];
        if (!string.IsNullOrEmpty(clientSecret))
        {
            parameters.Add(new KeyValuePair<string, string>("client_secret", clientSecret));
        }

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        request.Content = new FormUrlEncodedContent(parameters);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UsageHttpException(response.StatusCode, TryReadWhitelistedOAuthErrorCode(body), TryReadRetryAfter(response));
        }

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        if (!TryGetRequiredString(root, "access_token", out string accessToken))
        {
            throw new InvalidOperationException($"{providerLabel} OAuth refresh response did not include an access token.");
        }

        string nextRefreshToken = TryGetRequiredString(root, "refresh_token", out string rotatedRefreshToken)
            ? rotatedRefreshToken
            : refreshToken;
        string? idToken = TryGetRequiredString(root, "id_token", out string parsedIdToken) ? parsedIdToken : null;
        double? expiresInSeconds = root.TryGetProperty("expires_in", out JsonElement expiresIn) &&
            expiresIn.TryGetDouble(out double parsedExpiresIn) &&
            double.IsFinite(parsedExpiresIn) &&
            parsedExpiresIn > 0
                ? parsedExpiresIn
                : null;
        return new OAuthTokenResponse(accessToken, nextRefreshToken, idToken, expiresInSeconds);
    }

    /// <summary>
    /// Reads the JWT exp claim without validating the signature.
    /// </summary>
    internal static bool TryGetJwtExpiry(string token, out DateTimeOffset expiry)
    {
        expiry = default;
        try
        {
            if (!TryReadJwtPayload(token, out JsonElement payload) ||
                !payload.TryGetProperty("exp", out JsonElement exp) ||
                !exp.TryGetInt64(out long seconds))
            {
                return false;
            }

            expiry = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads only whitelisted RFC 6749 OAuth error codes from a bounded error body, never error_description.
    /// </summary>
    internal static string? TryReadWhitelistedOAuthErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 65_536)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out JsonElement errorElement)
                || errorElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? code = errorElement.GetString()?.Trim();
            return code is "invalid_grant" or "invalid_client" or "unauthorized_client" or "access_denied"
                ? code
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static TimeSpan? TryReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            TimeSpan remaining = date - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                return remaining;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a nonempty string property from a JSON object.
    /// </summary>
    private static bool TryGetRequiredString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    /// <summary>
    /// Decodes a JWT payload JSON object without validating the signature.
    /// </summary>
    internal static bool TryReadJwtPayload(string token, out JsonElement payload)
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
}
