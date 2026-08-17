using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexTray.Core;

[Flags]
public enum PageItem
{
    None = 0,
    Codex = 1 << 0,
    Cursor = 1 << 1,
    Apis = 1 << 2,
    Grok = 1 << 3,
    All = Codex | Grok | Cursor | Apis,
}

public sealed class ApiMonitorSettings
{
    public const string DeepSeekProvider = "DeepSeek";

    public const string NewApiProvider = "NewAPI";

    public const string OpenRouterProvider = "OpenRouter";

    public const string NanoGptProvider = "NanoGPT";

    public const string VercelProvider = "Vercel";

    public const string CursorProvider = "Cursor";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = DeepSeekProvider;

    public string Provider { get; set; } = DeepSeekProvider;

    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    public string ApiKey { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Normalizes one persisted API monitor configuration.
    /// </summary>
    public ApiMonitorSettings Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Provider = Provider?.Trim() switch
        {
            string value when string.Equals(value, NewApiProvider, StringComparison.OrdinalIgnoreCase) => NewApiProvider,
            string value when string.Equals(value, OpenRouterProvider, StringComparison.OrdinalIgnoreCase) => OpenRouterProvider,
            string value when string.Equals(value, NanoGptProvider, StringComparison.OrdinalIgnoreCase) => NanoGptProvider,
            string value when string.Equals(value, VercelProvider, StringComparison.OrdinalIgnoreCase) => VercelProvider,
            string value when string.Equals(value, CursorProvider, StringComparison.OrdinalIgnoreCase) => CursorProvider,
            _ => DeepSeekProvider,
        };
        Name = string.IsNullOrWhiteSpace(Name) ? Provider : Name.Trim();

        if (Provider == CursorProvider)
        {
            BaseUrl = string.Empty;
            ApiKey = string.Empty;
            UserId = string.Empty;
            return this;
        }

        BaseUrl = (BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        ApiKey = (ApiKey ?? string.Empty).Trim();
        UserId = (UserId ?? string.Empty).Trim();
        return this;
    }
}

public sealed class AppSettings
{
    private const string k_LegacyGrokProvider = "Grok";

    public const int CurrentSettingsSchemaVersion = 1;

    public const string ThemeModeSystem = "System";

    public const string ThemeModeLight = "Light";

    public const string ThemeModeDark = "Dark";

    public const string TokenUnitEnglish = "English unit";

    public const string TokenUnitChinese = "Chinese unit";

    public int SettingsSchemaVersion { get; set; } = CurrentSettingsSchemaVersion;

    public string LiteMonitorDir { get; set; } = string.Empty;

    public string TrafficMonitorDir { get; set; } = string.Empty;

    public int Port { get; set; } = CodexTrayDefaults.Port;

    public int RefreshIntervalMinutes { get; set; } = CodexTrayDefaults.RefreshIntervalMinutes;

    public PageItem VisiblePages { get; set; } = PageItem.All;

    public bool StartWithWindows { get; set; }

    public string ThemeMode { get; set; } = ThemeModeSystem;

    public string TokenUnit { get; set; } = TokenUnitEnglish;

    public bool MicaEnabled { get; set; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackdropMode { get; set; }

    public bool ShowResetTimeInPlugins { get; set; } = CodexTrayDefaults.ShowResetTimeInPlugins;

    public bool UseAbsoluteResetTime { get; set; } = CodexTrayDefaults.UseAbsoluteResetTime;

    public bool HideInvalidProgressBars { get; set; } = CodexTrayDefaults.HideInvalidProgressBars;

    public List<ApiMonitorSettings> ApiMonitors { get; set; } = [];

    /// <summary>
    /// Formats a token count using the selected compact unit family.
    /// </summary>
    public static string FormatTokenCount(long tokens, string tokenUnit)
    {
        (decimal divisor, string suffix) = tokenUnit == TokenUnitChinese
            ? tokens >= 100_000_000 ? (100_000_000m, "亿") : (10_000m, "万")
            : tokens >= 1_000_000_000 ? (1_000_000_000m, "B")
            : tokens >= 1_000_000 ? (1_000_000m, "M")
            : (1_000m, "K");
        return $"{tokens / divisor:0.00}{suffix}";
    }

    /// <summary>
    /// Creates a normalized copy of settings values.
    /// </summary>
    public AppSettings Normalize()
    {
        if (Port < CodexTrayDefaults.MinimumPort || Port > CodexTrayDefaults.MaximumPort)
        {
            Port = CodexTrayDefaults.Port;
        }

        if (RefreshIntervalMinutes < CodexTrayDefaults.MinimumRefreshIntervalMinutes ||
            RefreshIntervalMinutes > CodexTrayDefaults.MaximumRefreshIntervalMinutes)
        {
            RefreshIntervalMinutes = CodexTrayDefaults.RefreshIntervalMinutes;
        }

        LiteMonitorDir = (LiteMonitorDir ?? string.Empty).Trim();
        TrafficMonitorDir = (TrafficMonitorDir ?? string.Empty).Trim();
        ThemeMode = NormalizeThemeMode(ThemeMode);
        TokenUnit = NormalizeTokenUnit(TokenUnit);
        if (!string.IsNullOrWhiteSpace(BackdropMode))
        {
            MicaEnabled = string.Equals(BackdropMode.Trim(), "Mica", StringComparison.OrdinalIgnoreCase);
        }

        BackdropMode = null;
        SettingsSchemaVersion = CurrentSettingsSchemaVersion;
        VisiblePages &= PageItem.All;
        ApiMonitors ??= [];
        bool hadLegacyGrokMonitor = ApiMonitors.Any(monitor =>
            monitor != null && string.Equals(monitor.Provider?.Trim(), k_LegacyGrokProvider, StringComparison.OrdinalIgnoreCase));
        ApiMonitors.RemoveAll(monitor => monitor == null ||
            string.Equals(monitor.Provider?.Trim(), ApiMonitorSettings.CursorProvider, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(monitor.Provider?.Trim(), k_LegacyGrokProvider, StringComparison.OrdinalIgnoreCase));
        if (hadLegacyGrokMonitor)
        {
            VisiblePages |= PageItem.Grok;
        }

        HashSet<string> monitorIds = new(StringComparer.Ordinal);
        foreach (ApiMonitorSettings monitor in ApiMonitors)
        {
            monitor.Normalize();
            if (!monitorIds.Add(monitor.Id))
            {
                monitor.Id = Guid.NewGuid().ToString("N");
                monitorIds.Add(monitor.Id);
            }
        }

        return this;
    }

    /// <summary>
    /// Normalizes a theme mode string to a supported value.
    /// </summary>
    private static string NormalizeThemeMode(string? themeMode)
    {
        return themeMode?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemeModeLight,
            "dark" => ThemeModeDark,
            _ => ThemeModeSystem,
        };
    }

    /// <summary>
    /// Normalizes a token unit string to a supported value.
    /// </summary>
    private static string NormalizeTokenUnit(string? tokenUnit)
    {
        string normalized = tokenUnit?.Trim() ?? string.Empty;
        return string.Equals(normalized, TokenUnitChinese, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "万/亿", StringComparison.Ordinal)
            || string.Equals(normalized, "K/W/E", StringComparison.OrdinalIgnoreCase)
            ? TokenUnitChinese
            : TokenUnitEnglish;
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string SettingsPath { get; }

    /// <summary>
    /// Creates a settings store next to the executable.
    /// </summary>
    public SettingsStore()
        : this(AppContext.BaseDirectory)
    {
    }

    /// <summary>
    /// Creates a settings store under the specified application directory.
    /// </summary>
    public SettingsStore(string appDirectory)
    {
        SettingsPath = Path.Combine(appDirectory, CodexTrayDefaults.SettingsFileName);
    }

    /// <summary>
    /// Returns true when the settings file already exists.
    /// </summary>
    public bool Exists()
    {
        return File.Exists(SettingsPath);
    }

    /// <summary>
    /// Loads persisted settings, repairs missing fields, or returns defaults.
    /// </summary>
    public AppSettings Load()
    {
        if (!Exists())
        {
            return new AppSettings().Normalize();
        }

        try
        {
            string json = File.ReadAllText(SettingsPath);
            using JsonDocument document = JsonDocument.Parse(json);
            bool migrateLegacyAllPages = !document.RootElement.TryGetProperty(nameof(AppSettings.SettingsSchemaVersion), out JsonElement schemaVersion) ||
                !schemaVersion.TryGetInt32(out int version) ||
                version < AppSettings.CurrentSettingsSchemaVersion;
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, s_JsonOptions) ?? new AppSettings();
            if (migrateLegacyAllPages && settings.VisiblePages == (PageItem.Codex | PageItem.Cursor | PageItem.Apis))
            {
                settings.VisiblePages |= PageItem.Grok;
            }

            settings.Normalize();
            Save(settings);
            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings().Normalize();
        }
        catch (IOException)
        {
            return new AppSettings().Normalize();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings().Normalize();
        }
    }

    /// <summary>
    /// Saves settings next to the executable.
    /// </summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        settings.Normalize();
        string json = JsonSerializer.Serialize(settings, s_JsonOptions);
        AtomicFile.WriteAllText(SettingsPath, json);
    }
}
