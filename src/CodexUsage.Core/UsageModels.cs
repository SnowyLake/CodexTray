using System.Text.Json.Serialization;

namespace CodexUsage.Core;

public static class CodexUsageDefaults
{
    public const string Host = "127.0.0.1";
    public const int Port = 17890;
    public const string AppName = "CodexUsageLiteMonitor";
    public const string SettingsDirectoryName = "CodexUsageLiteMonitor";
    public const string SettingsFileName = "settings.json";
    public const string StartupRunValueName = "CodexUsageLiteMonitorTray";
    public const string PluginFileName = "CodexUsage.json";
}

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

    [JsonPropertyName("scanned")]
    public ScanStats Scanned { get; set; } = new();

    [JsonPropertyName("limits")]
    public UsageLimits Limits { get; set; } = new();

    [JsonPropertyName("display")]
    public UsageDisplay Display { get; set; } = new();
}

public sealed class ScanStats
{
    [JsonPropertyName("files")]
    public int Files { get; set; }

    [JsonPropertyName("token_count_events")]
    public int TokenCountEvents { get; set; }
}

public sealed class UsageLimits
{
    [JsonPropertyName("five_hour")]
    public UsageLimit FiveHour { get; set; } = new() { Name = "five_hour" };

    [JsonPropertyName("weekly")]
    public UsageLimit Weekly { get; set; } = new() { Name = "weekly" };
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
}

public sealed class UsageDisplay
{
    [JsonPropertyName("codex_5h")]
    public string Codex5H { get; set; } = "unavailable";

    [JsonPropertyName("codex_weekly")]
    public string CodexWeekly { get; set; } = "unavailable";

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "Codex unavailable";
}
