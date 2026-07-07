using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexMonitor.Core;

public sealed class CodexMonitorCollector
{
    private const string k_FiveHourDisplayLabel = "Codex 5-Hour";
    private const string k_SevenDayDisplayLabel = "Codex 7-Day";

    private static readonly HttpClient s_HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly Func<DateTimeOffset> m_NowProvider;
    private readonly HttpClient m_HttpClient;

    /// <summary>
    /// Creates a collector that reads Codex OAuth quota data.
    /// </summary>
    public CodexMonitorCollector(Func<DateTimeOffset>? nowProvider = null, HttpClient? httpClient = null)
    {
        m_NowProvider = nowProvider ?? (() => DateTimeOffset.Now);
        m_HttpClient = httpClient ?? s_HttpClient;
    }

    /// <summary>
    /// Collects the latest Codex monitor response from the default Codex directory.
    /// </summary>
    public UsageResponse Collect(bool showResetTimeInPlugins = true)
    {
        return Collect(GetDefaultCodexDirectory(), showResetTimeInPlugins);
    }

    /// <summary>
    /// Collects the latest Codex monitor response from a Codex directory.
    /// </summary>
    public UsageResponse Collect(string codexDirectory, bool showResetTimeInPlugins = true)
    {
        return CollectOfficialUsage(codexDirectory, showResetTimeInPlugins);
    }

    /// <summary>
    /// Gets the default Codex home directory.
    /// </summary>
    public static string GetDefaultCodexDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    /// <summary>
    /// Collects Codex usage from the official ChatGPT quota endpoint.
    /// </summary>
    private UsageResponse CollectOfficialUsage(string codexDirectory, bool showResetTimeInPlugins)
    {
        DateTimeOffset now = m_NowProvider();
        string authPath = Path.Combine(codexDirectory, "auth.json");
        CodexCredentials credentials = ReadCodexCredentials(authPath);
        if (!credentials.IsAvailable)
        {
            return CreateEmptyResponse(codexDirectory, now, credentials.Error ?? "Codex OAuth credentials unavailable");
        }

        HttpRequestMessage request = new(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.UserAgent.ParseAdd("codex-cli");
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        }

        try
        {
            using HttpResponseMessage response = m_HttpClient.Send(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return CreateEmptyResponse(codexDirectory, now, $"Codex OAuth token expired or unauthorized: HTTP {(int)response.StatusCode}");
            }

            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return CreateEmptyResponse(codexDirectory, now, $"Codex usage API failed: HTTP {(int)response.StatusCode}");
            }

            using JsonDocument document = JsonDocument.Parse(body);
            return BuildOfficialResponse(codexDirectory, authPath, document.RootElement, now, showResetTimeInPlugins);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return CreateEmptyResponse(codexDirectory, now, $"Codex usage API unavailable: {exception.Message}");
        }
    }

    /// <summary>
    /// Builds a usage response from the official quota endpoint JSON.
    /// </summary>
    private UsageResponse BuildOfficialResponse(string codexDirectory, string authPath, JsonElement root, DateTimeOffset now, bool showResetTimeInPlugins)
    {
        JsonElement rateLimit = GetObjectProperty(root, "rate_limit");
        JsonElement primary = GetObjectProperty(rateLimit, "primary_window");
        JsonElement secondary = GetObjectProperty(rateLimit, "secondary_window");
        UsageLimit fiveHour = BuildOfficialLimit("five_hour", primary, now);
        UsageLimit sevenDay = BuildOfficialLimit("seven_day", secondary, now);
        fiveHour.ResetLabel = FormatFiveHourResetLabel(fiveHour.ResetsAt, now);
        sevenDay.ResetLabel = FormatSevenDayResetLabel(sevenDay.ResetsAt, now);

        if (primary.ValueKind != JsonValueKind.Object && secondary.ValueKind != JsonValueKind.Object)
        {
            return CreateEmptyResponse(codexDirectory, now, "Codex usage API did not return rate_limit windows");
        }

        UsageDisplay display = BuildDisplay(fiveHour, sevenDay, showResetTimeInPlugins);
        return new UsageResponse
        {
            Available = true,
            Error = null,
            CodexDir = codexDirectory,
            SourceFile = authPath,
            Source = "official_api",
            PlanType = "chatgpt",
            UpdatedAt = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            Limits = new UsageLimits
            {
                FiveHour = fiveHour,
                SevenDay = sevenDay,
            },
            Display = display,
        };
    }

    /// <summary>
    /// Builds all display strings for monitor plugins.
    /// </summary>
    private static UsageDisplay BuildDisplay(UsageLimit fiveHour, UsageLimit sevenDay, bool showResetTimeInPlugins)
    {
        string codex5HDisplay = FormatDisplayValue(fiveHour, showResetTimeInPlugins);
        string codex7DDisplay = FormatDisplayValue(sevenDay, showResetTimeInPlugins);
        return new UsageDisplay
        {
            Codex5H = codex5HDisplay,
            Codex7D = codex7DDisplay,
            Summary = $"{k_FiveHourDisplayLabel}: {codex5HDisplay} | {k_SevenDayDisplayLabel}: {codex7DDisplay}",
        };
    }

    /// <summary>
    /// Formats a plugin display value, optionally appending the reset time suffix.
    /// </summary>
    private static string FormatDisplayValue(UsageLimit limit, bool showResetTimeInPlugins)
    {
        return showResetTimeInPlugins
            ? $"{limit.RemainingPercent}% {limit.ResetLabel}"
            : $"{limit.RemainingPercent}%";
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
                FiveHour = BuildOfficialLimit("five_hour", default, now),
                SevenDay = BuildOfficialLimit("seven_day", default, now),
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
    /// Formats the five hour reset as a countdown label.
    /// </summary>
    private static string FormatFiveHourResetLabel(long epochSeconds, DateTimeOffset now)
    {
        if (epochSeconds <= 0)
        {
            return "unknown";
        }

        TimeSpan remaining = GetRemainingTime(epochSeconds, now);
        long hours = (long)Math.Floor(remaining.TotalHours);
        return string.Create(CultureInfo.InvariantCulture, $"{hours}h{remaining.Minutes:D2}m");
    }

    /// <summary>
    /// Formats the seven day reset as a countdown label.
    /// </summary>
    private static string FormatSevenDayResetLabel(long epochSeconds, DateTimeOffset now)
    {
        if (epochSeconds <= 0)
        {
            return "unknown";
        }

        TimeSpan remaining = GetRemainingTime(epochSeconds, now);
        long days = (long)Math.Floor(remaining.TotalDays);
        return string.Create(CultureInfo.InvariantCulture, $"{days}d{remaining.Hours:D2}h");
    }

    /// <summary>
    /// Gets the non-negative remaining time until a reset epoch.
    /// </summary>
    private static TimeSpan GetRemainingTime(long epochSeconds, DateTimeOffset now)
    {
        DateTimeOffset resetAt = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToOffset(now.Offset);
        TimeSpan remaining = resetAt - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
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
