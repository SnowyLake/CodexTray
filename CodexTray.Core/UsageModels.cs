using System.Globalization;
using System.Text.Json.Serialization;

namespace CodexTray.Core;

public sealed class UsageResponse
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("codex_dir")]
    public string CodexDir { get; set; } = string.Empty;

    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = "none";

    [JsonPropertyName("plan_type")]
    public string PlanType { get; set; } = "unknown";

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("limits")]
    public UsageLimits Limits { get; set; } = new();

    [JsonPropertyName("reset_credits")]
    public ResetCredits ResetCredits { get; set; } = new();

    [JsonPropertyName("display")]
    public UsageDisplay Display { get; set; } = new();
}

public sealed class UsageLimits
{
    [JsonIgnore]
    public UsageLimit Session { get; set; } = new() { Name = "session" };

    [JsonPropertyName("weekly")]
    public UsageLimit Weekly { get; set; } = new() { Name = "weekly" };

    [JsonPropertyName("cursor_monthly")]
    public UsageLimit CursorMonthly { get; set; } = new() { Name = "monthly", RemainingPercent = 0 };

    [JsonPropertyName("grok_weekly")]
    public UsageLimit GrokWeekly { get; set; } = new() { Name = "weekly", RemainingPercent = 0 };
}

public sealed class UsageLimit
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("used_percent")]
    public int UsedPercent { get; set; }

    [JsonPropertyName("remaining_percent")]
    public int RemainingPercent { get; set; } = 100;

    [JsonPropertyName("window_minutes")]
    public int WindowMinutes { get; set; }

    [JsonPropertyName("resets_at")]
    public long ResetsAt { get; set; }

    [JsonPropertyName("reset_at_local")]
    public string ResetAtLocal { get; set; } = string.Empty;

    [JsonPropertyName("reset_time")]
    public string ResetTime { get; set; } = "unknown";

    [JsonPropertyName("reset_label")]
    public string ResetLabel { get; set; } = string.Empty;

    /// <summary>
    /// Formats a reset epoch as a countdown label.
    /// </summary>
    public static string FormatResetLabel(long epochSeconds, DateTimeOffset now)
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
    /// Formats a reset epoch as an absolute local month-day label.
    /// </summary>
    public static string FormatResetDate(long epochSeconds, DateTimeOffset now)
    {
        if (epochSeconds <= 0)
        {
            return "unknown";
        }

        return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToOffset(now.Offset).ToString("MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Gets the non-negative remaining time until a reset epoch.
    /// </summary>
    internal static TimeSpan GetRemainingTime(long epochSeconds, DateTimeOffset now)
    {
        DateTimeOffset resetAt = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToOffset(now.Offset);
        TimeSpan remaining = resetAt - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}

public sealed class ResetCredits
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("available_count")]
    public int AvailableCount { get; set; }

    [JsonPropertyName("nearest_expiry_local")]
    public string NearestExpiryLocal { get; set; } = "N/A";

    [JsonPropertyName("other_expiries_local")]
    public string OtherExpiriesLocal { get; set; } = string.Empty;
}

public sealed class UsageDisplay
{
    [JsonPropertyName("weekly")]
    public string Weekly { get; set; } = CodexTrayDefaults.UnavailableDisplay;

    [JsonPropertyName("cursor_monthly")]
    public string CursorMonthly { get; set; } = CodexTrayDefaults.UnavailableDisplay;

    [JsonPropertyName("grok_weekly")]
    public string GrokWeekly { get; set; } = CodexTrayDefaults.UnavailableDisplay;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = CodexTrayDefaults.UnavailableDisplay;
}

public sealed record GrokPluginUsage(UsageLimit Weekly, string Display);

public sealed record CursorPluginUsage(UsageLimit Monthly, string Display);
