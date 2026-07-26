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
        string reauthenticationHint,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        request.Content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
        ]);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{providerLabel} OAuth refresh failed: HTTP {(int)response.StatusCode}. {reauthenticationHint}");
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
    /// Reads the JWT sub claim without validating the signature.
    /// </summary>
    internal static bool TryGetJwtSubject(string token, out string subject)
    {
        subject = string.Empty;
        if (!TryReadJwtPayload(token, out JsonElement payload) ||
            !TryGetRequiredString(payload, "sub", out string parsedSubject))
        {
            return false;
        }

        subject = parsedSubject;
        return true;
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
}
