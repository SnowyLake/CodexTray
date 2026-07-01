using System.Globalization;
using System.Text.Json;

namespace CodexUsage.Core;

public sealed class CodexUsageCollector
{
    private readonly Func<DateTimeOffset> m_NowProvider;

    /// <summary>
    /// Creates a collector that reads Codex session JSONL files.
    /// </summary>
    public CodexUsageCollector(Func<DateTimeOffset>? nowProvider = null)
    {
        m_NowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// Collects the latest Codex usage response from the default Codex directory.
    /// </summary>
    public UsageResponse Collect()
    {
        return Collect(GetDefaultCodexDirectory());
    }

    /// <summary>
    /// Collects the latest Codex usage response from a Codex directory.
    /// </summary>
    public UsageResponse Collect(string codexDirectory)
    {
        DateTimeOffset now = m_NowProvider();
        string sessionsDirectory = Path.Combine(codexDirectory, "sessions");
        if (!Directory.Exists(sessionsDirectory))
        {
            return CreateEmptyResponse(codexDirectory, now, "No Codex session JSONL files found");
        }

        List<FileInfo> files = EnumerateJsonlFiles(sessionsDirectory);
        if (files.Count == 0)
        {
            return CreateEmptyResponse(codexDirectory, now, "No Codex session JSONL files found");
        }

        TokenCountEvent? latestEvent = null;
        int scannedEvents = 0;
        foreach (FileInfo file in files)
        {
            foreach (TokenCountEvent tokenEvent in ReadTokenCountEvents(file))
            {
                scannedEvents++;
                if (latestEvent == null || tokenEvent.Timestamp > latestEvent.Timestamp)
                {
                    latestEvent = tokenEvent;
                }
            }
        }

        if (latestEvent == null)
        {
            return CreateEmptyResponse(codexDirectory, now, "No token_count events found");
        }

        JsonElement rateLimits = GetObjectProperty(latestEvent.Payload, "rate_limits");
        JsonElement primary = GetObjectProperty(rateLimits, "primary");
        JsonElement secondary = GetObjectProperty(rateLimits, "secondary");

        UsageLimit fiveHour = BuildLimit("five_hour", primary, now);
        UsageLimit weekly = BuildLimit("weekly", secondary, now);
        weekly.ResetLabel = FormatWeeklyResetLabel(weekly.ResetsAt, now);

        string codex5HDisplay = $"{fiveHour.RemainingPercent}%  {fiveHour.ResetTime}";
        string codexWeeklyDisplay = $"{weekly.RemainingPercent}%  {weekly.ResetLabel}";

        return new UsageResponse
        {
            Available = true,
            Error = null,
            CodexDir = codexDirectory,
            SourceFile = latestEvent.SourceFile,
            Source = "sessions",
            PlanType = GetStringProperty(rateLimits, "plan_type", "unknown"),
            UpdatedAt = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            Scanned = new ScanStats
            {
                Files = files.Count,
                TokenCountEvents = scannedEvents,
            },
            Limits = new UsageLimits
            {
                FiveHour = fiveHour,
                Weekly = weekly,
            },
            Display = new UsageDisplay
            {
                Codex5H = codex5HDisplay,
                CodexWeekly = codexWeeklyDisplay,
                Summary = $"Codex 5h: {codex5HDisplay} | Codex Weekly: {codexWeeklyDisplay}",
            },
        };
    }

    /// <summary>
    /// Gets the default Codex home directory.
    /// </summary>
    public static string GetDefaultCodexDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    /// <summary>
    /// Enumerates JSONL files with newest modified files first.
    /// </summary>
    private static List<FileInfo> EnumerateJsonlFiles(string sessionsDirectory)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        return Directory.EnumerateFiles(sessionsDirectory, "*.jsonl", options)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
    }

    /// <summary>
    /// Reads token_count events from a session file.
    /// </summary>
    private static IEnumerable<TokenCountEvent> ReadTokenCountEvents(FileInfo file)
    {
        DateTimeOffset fallbackTimestamp = file.LastWriteTime;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file.FullName);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string line in lines)
        {
            if (!line.Contains("token_count", StringComparison.Ordinal))
            {
                continue;
            }

            using JsonDocument? document = TryParseJson(line);
            if (document == null)
            {
                continue;
            }

            JsonElement root = document.RootElement;
            JsonElement payload = GetObjectProperty(root, "payload");
            if (payload.ValueKind != JsonValueKind.Object || GetStringProperty(payload, "type", string.Empty) != "token_count")
            {
                continue;
            }

            DateTimeOffset timestamp = ParseTimestamp(GetStringProperty(root, "timestamp", string.Empty), fallbackTimestamp);
            yield return new TokenCountEvent(timestamp, payload.Clone(), file.FullName);
        }
    }

    /// <summary>
    /// Parses one JSON line without throwing on invalid input.
    /// </summary>
    private static JsonDocument? TryParseJson(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a usage limit model from a rate limit object.
    /// </summary>
    private static UsageLimit BuildLimit(string name, JsonElement data, DateTimeOffset now)
    {
        int usedPercent = ClampPercent((int)Math.Round(GetDoubleProperty(data, "used_percent", 0.0), MidpointRounding.AwayFromZero));
        long resetsAt = GetInt64Property(data, "resets_at", 0);
        return new UsageLimit
        {
            Name = name,
            UsedPercent = usedPercent,
            RemainingPercent = 100 - usedPercent,
            WindowMinutes = GetInt32Property(data, "window_minutes", 0),
            ResetsAt = resetsAt,
            ResetAtLocal = FormatResetLocal(resetsAt, now),
            ResetTime = FormatResetClock(resetsAt, now),
        };
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
                FiveHour = BuildLimit("five_hour", default, now),
                Weekly = BuildLimit("weekly", default, now),
            },
            Display = new UsageDisplay(),
        };
    }

    /// <summary>
    /// Parses an ISO timestamp with a fallback value.
    /// </summary>
    private static DateTimeOffset ParseTimestamp(string rawValue, DateTimeOffset fallback)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return fallback;
        }

        string normalizedValue = rawValue.EndsWith("Z", StringComparison.Ordinal) ? rawValue[..^1] + "+00:00" : rawValue;
        return DateTimeOffset.TryParse(normalizedValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed.ToLocalTime()
            : fallback;
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
    /// Formats the weekly reset as a date or local clock label.
    /// </summary>
    private static string FormatWeeklyResetLabel(long epochSeconds, DateTimeOffset now)
    {
        if (epochSeconds <= 0)
        {
            return "unknown";
        }

        DateTimeOffset resetAt = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToOffset(now.Offset);
        TimeSpan untilReset = resetAt - now;
        if (untilReset.TotalSeconds < 24 * 60 * 60)
        {
            return resetAt.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return resetAt.ToString("MM-dd", CultureInfo.InvariantCulture);
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

    private sealed record TokenCountEvent(DateTimeOffset Timestamp, JsonElement Payload, string SourceFile);
}