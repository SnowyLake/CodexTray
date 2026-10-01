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
    All = Codex | Cursor | Grok | Apis,
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

    public int SettingsSchemaVersion { get; set; } = CurrentSettingsSchemaVersion;

    public string LiteMonitorDir { get; set; } = string.Empty;

    public string TrafficMonitorDir { get; set; } = string.Empty;

    public int Port { get; set; } = CodexTrayDefaults.Port;

    public int RefreshIntervalMinutes { get; set; } = CodexTrayDefaults.RefreshIntervalMinutes;

    public PageItem VisiblePages { get; set; } = PageItem.All;

    public bool StartWithWindows { get; set; }

    public string ThemeMode { get; set; } = ThemeModeSystem;

    public bool MicaEnabled { get; set; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackdropMode { get; set; }

    public bool UseAbsoluteResetTime { get; set; } = CodexTrayDefaults.UseAbsoluteResetTime;

    public List<ApiMonitorSettings> ApiMonitors { get; set; } = [];

    /// <summary>
    /// Copies editable settings without sharing the mutable API card list.
    /// </summary>
    public AppSettings Copy()
    {
        AppSettings copy = (AppSettings)MemberwiseClone();
        copy.ApiMonitors = ApiMonitors.Select(monitor => new ApiMonitorSettings
        {
            Id = monitor.Id, Name = monitor.Name, Provider = monitor.Provider, BaseUrl = monitor.BaseUrl, ApiKey = monitor.ApiKey, UserId = monitor.UserId,
        }).ToList();
        return copy;
    }

    /// <summary>
    /// Formats a token count using compact English units.
    /// </summary>
    public static string FormatTokenCount(long tokens)
    {
        (decimal divisor, string suffix) = tokens >= 1_000_000_000 ? (1_000_000_000m, "B")
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
    public static string NormalizeThemeMode(string? themeMode)
    {
        return themeMode?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemeModeLight,
            "dark" => ThemeModeDark,
            _ => ThemeModeSystem,
        };
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string SettingsPath { get; }
    public string BackupPath { get; }
    public string LoadError { get; private set; } = string.Empty;
    public bool IsWriteBlocked => LoadError.Length > 0;
    public bool CanRestoreBackup => File.Exists(BackupPath);
    public string? PreservedSettingsPath { get; private set; }

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
        BackupPath = Path.Combine(appDirectory, CodexTrayDefaults.SettingsBackupFileName);
    }

    /// <summary>
    /// Returns true when the settings file already exists.
    /// </summary>
    public bool Exists()
    {
        return File.Exists(SettingsPath) || Directory.Exists(SettingsPath);
    }

    /// <summary>
    /// Loads persisted settings, repairs missing fields, or returns defaults.
    /// </summary>
    public AppSettings Load()
    {
        LoadError = string.Empty;
        try
        {
            string json = File.ReadAllText(SettingsPath);
            AppSettings settings = ParseSettings(json);
            string normalized = JsonSerializer.Serialize(settings, s_JsonOptions);
            if (!File.Exists(BackupPath) || !string.Equals(json, normalized, StringComparison.Ordinal))
            {
                AtomicFile.WriteAllText(BackupPath, json);
            }

            if (!string.Equals(json, normalized, StringComparison.Ordinal))
            {
                AtomicFile.WriteAllText(SettingsPath, normalized);
            }

            return settings;
        }
        catch (FileNotFoundException) when (!Exists())
        {
            return new AppSettings().Normalize();
        }
        catch (DirectoryNotFoundException) when (!Exists())
        {
            return new AppSettings().Normalize();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoadError = FormatLoadError(exception);
            return new AppSettings().Normalize();
        }
    }

    /// <summary>
    /// Saves settings next to the executable.
    /// </summary>
    public void Save(AppSettings settings)
    {
        if (IsWriteBlocked)
        {
            throw new InvalidOperationException("Settings are protected after a load failure. Restore the backup or repair the original file and restart.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            if (Exists())
            {
                string previous = File.ReadAllText(SettingsPath);
                ParseSettings(previous);
                AtomicFile.WriteAllText(BackupPath, previous);
            }

            string json = JsonSerializer.Serialize(settings.Normalize(), s_JsonOptions);
            AtomicFile.WriteAllText(SettingsPath, json);
            if (!File.Exists(BackupPath))
            {
                AtomicFile.WriteAllText(BackupPath, json);
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoadError = FormatLoadError(exception);
            throw new InvalidOperationException(LoadError, exception);
        }
    }

    /// <summary>
    /// Validates the backup before preserving the current file and atomically restoring valid settings.
    /// </summary>
    public AppSettings RestoreBackup()
    {
        string json = File.ReadAllText(BackupPath);
        AppSettings settings = ParseSettings(json);
        if (File.Exists(SettingsPath))
        {
            string preserved = Path.Combine(Path.GetDirectoryName(SettingsPath)!, $"{CodexTrayDefaults.SettingsDamagedFilePrefix}{Guid.NewGuid():N}.json");
            File.Copy(SettingsPath, preserved, overwrite: false);
            PreservedSettingsPath = preserved;
        }

        AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, s_JsonOptions));
        LoadError = string.Empty;
        return settings;
    }

    /// <summary>
    /// Validates the root and applies existing schema migration without writing any file.
    /// </summary>
    private static AppSettings ParseSettings(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Settings root must be an object.");
        }

        bool migrateLegacyAllPages = !document.RootElement.TryGetProperty(nameof(AppSettings.SettingsSchemaVersion), out JsonElement schemaVersion) ||
            !schemaVersion.TryGetInt32(out int version) || version < AppSettings.CurrentSettingsSchemaVersion;
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, s_JsonOptions) ?? throw new JsonException("Settings object is missing.");
        if (migrateLegacyAllPages && settings.VisiblePages == (PageItem.Codex | PageItem.Cursor | PageItem.Apis))
        {
            settings.VisiblePages |= PageItem.Grok;
        }

        return settings.Normalize();
    }

    /// <summary>
    /// Describes load and save failures without echoing configuration contents or credentials.
    /// </summary>
    private static string FormatLoadError(Exception exception)
    {
        return exception switch
        {
            JsonException json => $"Settings JSON is invalid (line {json.LineNumber?.ToString() ?? "unknown"}, byte {json.BytePositionInLine?.ToString() ?? "unknown"}). Original file preserved; saving is disabled.",
            UnauthorizedAccessException => "Settings access denied. Original file preserved; saving is disabled.",
            _ => "Settings could not be read or saved. Original file preserved; saving is disabled.",
        };
    }
}
