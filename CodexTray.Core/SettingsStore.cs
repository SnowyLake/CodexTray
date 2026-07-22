using System.Text.Json;

namespace CodexTray.Core;

[Flags]
public enum TokenCostItem
{
    None = 0,
    Today = 1 << 0,
    Yesterday = 1 << 1,
    Week = 1 << 2,
    Month = 1 << 3,
    SevenDay = 1 << 4,
    ThirtyDay = 1 << 5,
    All = Today | Yesterday | Week | Month | SevenDay | ThirtyDay,
}

public sealed class ApiMonitorSettings
{
    public const string DeepSeekProvider = "DeepSeek";

    public const string NewApiProvider = "NewAPI";

    public const string GrokProvider = "Grok";

    public const string CursorProvider = "Cursor";

    public const string GrokBuildOAuthSource = "Grok Build";

    public const string OpenCodeOAuthSource = "OpenCode";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = DeepSeekProvider;

    public string Provider { get; set; } = DeepSeekProvider;

    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    public string ApiKey { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string GrokOAuthSource { get; set; } = GrokBuildOAuthSource;

    /// <summary>
    /// Normalizes one persisted API monitor configuration.
    /// </summary>
    public ApiMonitorSettings Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Provider = Provider?.Trim() switch
        {
            string value when string.Equals(value, NewApiProvider, StringComparison.OrdinalIgnoreCase) => NewApiProvider,
            string value when string.Equals(value, GrokProvider, StringComparison.OrdinalIgnoreCase) => GrokProvider,
            string value when string.Equals(value, CursorProvider, StringComparison.OrdinalIgnoreCase) => CursorProvider,
            _ => DeepSeekProvider,
        };
        Name = string.IsNullOrWhiteSpace(Name) ? Provider : Name.Trim();
        GrokOAuthSource = string.Equals(GrokOAuthSource?.Trim(), OpenCodeOAuthSource, StringComparison.OrdinalIgnoreCase)
            ? OpenCodeOAuthSource
            : GrokBuildOAuthSource;

        if (Provider is GrokProvider or CursorProvider)
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
    public const string ThemeModeSystem = "System";

    public const string ThemeModeLight = "Light";

    public const string ThemeModeDark = "Dark";

    public const string TokenUnitEnglish = "English unit";

    public const string TokenUnitChinese = "Chinese unit";

    public string LiteMonitorDir { get; set; } = string.Empty;

    public string TrafficMonitorDir { get; set; } = string.Empty;

    public int Port { get; set; } = CodexTrayDefaults.Port;

    public int RefreshIntervalMinutes { get; set; } = CodexTrayDefaults.RefreshIntervalMinutes;

    public bool StartWithWindows { get; set; }

    public string ThemeMode { get; set; } = ThemeModeSystem;

    public string TokenUnit { get; set; } = TokenUnitEnglish;

    public TokenCostItem TokenCostItems { get; set; } = TokenCostItem.All;

    public bool AcrylicEnabled { get; set; } = CodexTrayDefaults.AcrylicEnabled;

    public int AcrylicOpacityPercent { get; set; } = CodexTrayDefaults.AcrylicOpacityPercent;

    public bool ShowResetTimeInPlugins { get; set; } = CodexTrayDefaults.ShowResetTimeInPlugins;

    public bool UseAbsoluteResetTime { get; set; } = CodexTrayDefaults.UseAbsoluteResetTime;

    public List<ApiMonitorSettings> ApiMonitors { get; set; } = [];

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

        if (AcrylicOpacityPercent < CodexTrayDefaults.MinimumAcrylicOpacityPercent ||
            AcrylicOpacityPercent > CodexTrayDefaults.MaximumAcrylicOpacityPercent)
        {
            AcrylicOpacityPercent = CodexTrayDefaults.AcrylicOpacityPercent;
        }

        LiteMonitorDir = LiteMonitorDir.Trim();
        TrafficMonitorDir = TrafficMonitorDir.Trim();
        ThemeMode = NormalizeThemeMode(ThemeMode);
        TokenUnit = NormalizeTokenUnit(TokenUnit);
        TokenCostItems &= TokenCostItem.All;
        ApiMonitors ??= [];
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
            AppSettings settings = (JsonSerializer.Deserialize<AppSettings>(json, s_JsonOptions) ?? new AppSettings()).Normalize();
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
    }

    /// <summary>
    /// Saves settings next to the executable.
    /// </summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        settings.Normalize();
        string json = JsonSerializer.Serialize(settings, s_JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
