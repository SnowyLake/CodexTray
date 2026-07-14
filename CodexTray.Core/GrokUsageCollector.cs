using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodexTray.Core;

public sealed record GrokUsageSnapshot(double UsedPercent, long ResetsAt);

public sealed class GrokUsageCollector
{
    private const string k_BillingEndpoint = "https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig";
    private const long k_MinimumUnixTimestamp = 1_700_000_000;
    private const long k_MaximumUnixTimestamp = 2_100_000_000;

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
        if (!TryReadAccessToken(oauthSource, out string accessToken, out string error))
        {
            throw new InvalidOperationException(error);
        }

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
    /// Reads the OAuth access token for the selected source.
    /// </summary>
    private static bool TryReadAccessToken(string oauthSource, out string accessToken, out string error)
    {
        return oauthSource == ApiMonitorSettings.OpenCodeOAuthSource
            ? TryReadOpenCodeAccessToken(out accessToken, out error)
            : TryReadGrokBuildAccessToken(out accessToken, out error);
    }

    /// <summary>
    /// Reads a non-expired Grok Build OAuth access token.
    /// </summary>
    private static bool TryReadGrokBuildAccessToken(out string accessToken, out string error)
    {
        accessToken = string.Empty;
        error = string.Empty;
        string grokHome = Environment.GetEnvironmentVariable("GROK_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");
        string authPath = Path.Combine(grokHome, "auth.json");
        if (!File.Exists(authPath))
        {
            error = "Grok Build OAuth file was not found. Run grok login first.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(authPath));
            JsonElement? fallback = null;
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Grok Build OAuth file has an invalid format.";
                return false;
            }

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
                    return TryReadGrokBuildEntry(entry.Value, out accessToken, out error);
                }

                if (entry.Name.Contains("/sign-in", StringComparison.Ordinal))
                {
                    fallback = entry.Value;
                }
            }

            if (fallback is JsonElement fallbackEntry)
            {
                return TryReadGrokBuildEntry(fallbackEntry, out accessToken, out error);
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
    /// Validates one Grok Build credential entry before returning its access token.
    /// </summary>
    private static bool TryReadGrokBuildEntry(JsonElement entry, out string accessToken, out string error)
    {
        accessToken = entry.GetProperty("key").GetString() ?? string.Empty;
        error = string.Empty;
        if (entry.TryGetProperty("expires_at", out JsonElement expiresAt) &&
            DateTimeOffset.TryParse(expiresAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset expiry) &&
            expiry <= DateTimeOffset.UtcNow)
        {
            accessToken = string.Empty;
            error = "Grok Build OAuth token expired. Run grok login to refresh it.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads a non-expired OpenCode xAI OAuth access token.
    /// </summary>
    private static bool TryReadOpenCodeAccessToken(out string accessToken, out string error)
    {
        accessToken = string.Empty;
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

                if (xai.TryGetProperty("expires", out JsonElement expires) &&
                    expires.TryGetInt64(out long expiryMilliseconds) &&
                    expiryMilliseconds <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                {
                    error = "OpenCode xAI OAuth token expired. Use Grok in OpenCode to refresh it.";
                    return false;
                }

                accessToken = access.GetString() ?? string.Empty;
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
}
