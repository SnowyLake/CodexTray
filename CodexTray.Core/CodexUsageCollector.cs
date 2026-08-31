using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexTray.Core;

public sealed class CodexUsageCollector
{
    private const int k_SessionWindowSeconds = 18000;
    private const int k_WeeklyWindowSeconds = 604800;
    private const string k_ResetCreditsEndpoint = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

    private static readonly HttpClient s_HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly Func<DateTimeOffset> m_NowProvider;
    private readonly HttpClient m_HttpClient;

    /// <summary>
    /// Creates a collector that reads Codex OAuth quota data.
    /// </summary>
    public CodexUsageCollector(Func<DateTimeOffset>? nowProvider = null, HttpClient? httpClient = null)
    {
        m_NowProvider = nowProvider ?? (() => DateTimeOffset.Now);
        m_HttpClient = httpClient ?? s_HttpClient;
    }

    /// <summary>
    /// Collects the latest Codex usage response asynchronously from the default Codex directory.
    /// </summary>
    public Task<UsageResponse> CollectAsync(bool useAbsoluteResetTime = false, CancellationToken cancellationToken = default)
    {
        string codexDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return CollectAsync(codexDirectory, useAbsoluteResetTime, cancellationToken);
    }

    /// <summary>
    /// Collects the latest Codex usage response asynchronously from a Codex directory.
    /// </summary>
    public Task<UsageResponse> CollectAsync(string codexDirectory, bool useAbsoluteResetTime = false, CancellationToken cancellationToken = default)
    {
        return CollectOfficialUsageAsync(codexDirectory, useAbsoluteResetTime, cancellationToken);
    }

    /// <summary>
    /// Collects Codex usage from the official ChatGPT quota endpoint.
    /// </summary>
    private async Task<UsageResponse> CollectOfficialUsageAsync(string codexDirectory, bool useAbsoluteResetTime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = m_NowProvider();
        string authPath = Path.Combine(codexDirectory, "auth.json");
        CodexCredentials credentials = ReadCodexCredentials(authPath);
        if (!credentials.IsAvailable)
        {
            return CreateEmptyResponse(codexDirectory, now, credentials.Error ?? "Codex OAuth credentials unavailable");
        }

        using HttpRequestMessage request = new(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.UserAgent.ParseAdd("codex-cli");
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        }

        try
        {
            using HttpResponseMessage response = await m_HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return CreateEmptyResponse(codexDirectory, now, $"Codex OAuth token expired or unauthorized: HTTP {(int)response.StatusCode}");
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return CreateEmptyResponse(codexDirectory, now, $"Codex usage API failed: HTTP {(int)response.StatusCode}");
            }

            using JsonDocument document = JsonDocument.Parse(body);
            UsageResponse usage = BuildOfficialResponse(codexDirectory, authPath, document.RootElement, now, useAbsoluteResetTime);
            if (usage.Available)
            {
                usage.ResetCredits = await CollectResetCreditsAsync(credentials, now, cancellationToken).ConfigureAwait(false);
            }

            return usage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return CreateEmptyResponse(codexDirectory, now, $"Codex usage API unavailable: {exception.Message}");
        }
    }

    /// <summary>
    /// Builds a usage response from the official quota endpoint JSON.
    /// </summary>
    private UsageResponse BuildOfficialResponse(string codexDirectory, string authPath, JsonElement root, DateTimeOffset now, bool useAbsoluteResetTime)
    {
        JsonElement rateLimit = GetObjectProperty(root, "rate_limit");
        JsonElement primary = GetObjectProperty(rateLimit, "primary_window");
        JsonElement secondary = GetObjectProperty(rateLimit, "secondary_window");
        UsageLimit session = BuildOfficialLimitFromRateLimit("session", rateLimit, k_SessionWindowSeconds, now);
        UsageLimit weekly = BuildOfficialLimitFromRateLimit("weekly", rateLimit, k_WeeklyWindowSeconds, now);

        session.ResetLabel = useAbsoluteResetTime
            ? FormatResetClock(session.ResetsAt, now)
            : FormatSessionResetLabel(session.ResetsAt, now);
        weekly.ResetLabel = useAbsoluteResetTime
            ? UsageLimit.FormatResetDate(weekly.ResetsAt, now)
            : UsageLimit.FormatResetLabel(weekly.ResetsAt, now);

        if (primary.ValueKind != JsonValueKind.Object && secondary.ValueKind != JsonValueKind.Object)
        {
            return CreateEmptyResponse(codexDirectory, now, "Codex usage API did not return rate_limit windows");
        }

        UsageDisplay display = BuildDisplay(weekly);
        string planType = GetStringProperty(root, "plan_type", "unknown");
        return new UsageResponse
        {
            Available = true,
            Error = null,
            CodexDir = codexDirectory,
            SourceFile = authPath,
            Source = "official_api",
            PlanType = planType,
            UpdatedAt = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            Limits = new UsageLimits
            {
                Session = session,
                Weekly = weekly,
            },
            Display = display,
        };
    }

    /// <summary>
    /// Collects reset credit availability without affecting the main quota response.
    /// </summary>
    private async Task<ResetCredits> CollectResetCreditsAsync(CodexCredentials credentials, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            return new ResetCredits();
        }

        using HttpRequestMessage request = new(HttpMethod.Get, k_ResetCreditsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "codex-1");
        request.Headers.TryAddWithoutValidation("originator", "Codex Desktop");

        try
        {
            using HttpResponseMessage response = await m_HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ResetCredits();
            }

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return BuildResetCredits(document.RootElement, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return new ResetCredits();
        }
    }

    /// <summary>
    /// Builds reset credit data from the official credit endpoint JSON.
    /// </summary>
    private static ResetCredits BuildResetCredits(JsonElement root, DateTimeOffset now)
    {
        int availableCount = Math.Max(0, GetInt32Property(root, "available_count", 0));
        List<DateTimeOffset> expiries = [];
        if (availableCount > 0 && root.TryGetProperty("credits", out JsonElement credits) && credits.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement credit in credits.EnumerateArray())
            {
                if (GetStringProperty(credit, "status", string.Empty) != "available" ||
                    !DateTimeOffset.TryParse(GetStringProperty(credit, "expires_at", string.Empty), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset expiry) ||
                    expiry < now)
                {
                    continue;
                }

                expiries.Add(expiry);
            }
        }

        expiries.Sort();
        return new ResetCredits
        {
            Available = true,
            AvailableCount = availableCount,
            NearestExpiryLocal = expiries.Count > 0
                ? expiries[0].ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "N/A",
            OtherExpiriesLocal = string.Join(
                " · ",
                expiries.Skip(1).Select(expiry => expiry.ToLocalTime().ToString("MM-dd", CultureInfo.InvariantCulture))),
        };
    }

    /// <summary>
    /// Builds all display strings for monitor plugins.
    /// </summary>
    private static UsageDisplay BuildDisplay(UsageLimit weekly)
    {
        string weeklyDisplay = FormatDisplayValue(weekly);
        return new UsageDisplay
        {
            Weekly = weeklyDisplay,
            Summary = $"Codex: {weeklyDisplay}",
        };
    }

    /// <summary>
    /// Formats a percentage-only plugin display value.
    /// </summary>
    private static string FormatDisplayValue(UsageLimit limit)
    {
        if (limit.WindowMinutes <= 0)
        {
            return CodexTrayDefaults.UnavailableDisplay;
        }

        return $"{limit.RemainingPercent}%";
    }

    /// <summary>
    /// Builds a usage limit model from an official quota window object.
    /// </summary>
    private static UsageLimit BuildOfficialLimit(string name, JsonElement data, DateTimeOffset now)
    {
        int usedPercent = ClampPercent((int)Math.Round(GetDoubleProperty(data, "used_percent", 0.0), MidpointRounding.AwayFromZero));
        long resetsAt = GetInt64Property(data, "reset_at", 0);
        int windowMinutes = GetInt32Property(data, "limit_window_seconds", 0) / 60;
        return new UsageLimit
        {
            Name = name,
            UsedPercent = usedPercent,
            RemainingPercent = 100 - usedPercent,
            WindowMinutes = windowMinutes,
            ResetsAt = resetsAt,
            ResetAtLocal = FormatResetLocal(resetsAt, now),
            ResetTime = FormatResetClock(resetsAt, now),
        };
    }

    /// <summary>
    /// Builds one quota limit from the matching window in a rate-limit object.
    /// </summary>
    private static UsageLimit BuildOfficialLimitFromRateLimit(string name, JsonElement rateLimit, int windowSeconds, DateTimeOffset now)
    {
        foreach (JsonElement window in new[] { GetObjectProperty(rateLimit, "primary_window"), GetObjectProperty(rateLimit, "secondary_window") })
        {
            if (GetInt32Property(window, "limit_window_seconds", 0) == windowSeconds)
            {
                return BuildOfficialLimit(name, window, now);
            }
        }

        return BuildOfficialLimit(name, default, now);
    }

    /// <summary>
    /// Reads Codex OAuth credentials from auth.json.
    /// </summary>
    private static CodexCredentials ReadCodexCredentials(string authPath)
    {
        if (!File.Exists(authPath))
        {
            return CodexCredentials.Unavailable("Codex auth.json not found");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(authPath));
            JsonElement root = document.RootElement;
            if (GetStringProperty(root, "auth_mode", string.Empty) != "chatgpt")
            {
                return CodexCredentials.Unavailable("Codex is not using ChatGPT OAuth mode");
            }

            JsonElement tokens = GetObjectProperty(root, "tokens");
            string accessToken = GetStringProperty(tokens, "access_token", string.Empty);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return CodexCredentials.Unavailable("Codex access_token is missing");
            }

            return new CodexCredentials(accessToken, GetStringProperty(tokens, "account_id", string.Empty), null);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return CodexCredentials.Unavailable($"Codex auth.json could not be read: {exception.Message}");
        }
    }

    /// <summary>
    /// Creates an unavailable usage response.
    /// </summary>
    private static UsageResponse CreateEmptyResponse(string codexDirectory, DateTimeOffset now, string error)
    {
        return new UsageResponse
        {
            Available = false,
            Error = error,
            CodexDir = codexDirectory,
            Source = "none",
            PlanType = "unknown",
            UpdatedAt = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            Limits = new UsageLimits
            {
                Session = BuildOfficialLimit("session", default, now),
                Weekly = BuildOfficialLimit("weekly", default, now),
            },
            Display = new UsageDisplay(),
        };
    }

    /// <summary>
    /// Formats the reset epoch as a local clock label.
    /// </summary>
    private static string FormatResetClock(long epochSeconds, DateTimeOffset now)
    {
        if (epochSeconds <= 0)
        {
            return "unknown";
        }

        return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToOffset(now.Offset).ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats the session reset as a countdown label.
    /// </summary>
    private static string FormatSessionResetLabel(long epochSeconds, DateTimeOffset now)
    {
        if (epochSeconds <= 0)
        {
            return "unknown";
        }

        TimeSpan remaining = UsageLimit.GetRemainingTime(epochSeconds, now);
        long hours = (long)Math.Floor(remaining.TotalHours);
        return string.Create(CultureInfo.InvariantCulture, $"{hours}h{remaining.Minutes:D2}m");
    }

    /// <summary>
    /// Formats the reset epoch as a local timestamp.
    /// </summary>
    private static string FormatResetLocal(long epochSeconds, DateTimeOffset now)
    {
        if (epochSeconds <= 0)
        {
            return string.Empty;
        }

        string value = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToOffset(now.Offset).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        return value.Length >= 3 ? value.Remove(value.Length - 3, 1) : value;
    }

    /// <summary>
    /// Clamps a percentage value into the display range.
    /// </summary>
    private static int ClampPercent(int value)
    {
        return Math.Max(0, Math.Min(100, value));
    }

    /// <summary>
    /// Gets an object property or an empty element.
    /// </summary>
    private static JsonElement GetObjectProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return default;
    }

    /// <summary>
    /// Gets a string property from a JSON object.
    /// </summary>
    private static string GetStringProperty(JsonElement element, string name, string defaultValue)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? defaultValue;
        }

        return defaultValue;
    }

    /// <summary>
    /// Gets a double property from a JSON object.
    /// </summary>
    private static double GetDoubleProperty(JsonElement element, string name, double defaultValue)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result))
        {
            return result;
        }

        return defaultValue;
    }

    /// <summary>
    /// Gets an integer property from a JSON object.
    /// </summary>
    private static int GetInt32Property(JsonElement element, string name, int defaultValue)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result))
        {
            return result;
        }

        return defaultValue;
    }

    /// <summary>
    /// Gets a long integer property from a JSON object.
    /// </summary>
    private static long GetInt64Property(JsonElement element, string name, long defaultValue)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result))
        {
            return result;
        }

        return defaultValue;
    }

    private sealed record CodexCredentials(string AccessToken, string AccountId, string? Error)
    {
        public bool IsAvailable => string.IsNullOrWhiteSpace(Error);

        /// <summary>
        /// Creates an unavailable credentials result.
        /// </summary>
        public static CodexCredentials Unavailable(string error)
        {
            return new CodexCredentials(string.Empty, string.Empty, error);
        }
    }
}
