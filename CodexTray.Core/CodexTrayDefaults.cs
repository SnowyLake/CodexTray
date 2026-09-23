namespace CodexTray.Core;

public static class CodexTrayDefaults
{
    public const string Host = "127.0.0.1";
    public const int Port = 17890;
    public const int MinimumPort = 1;
    public const int MaximumPort = 65535;
    public const int RefreshIntervalMinutes = 1;
    public const int MinimumRefreshIntervalMinutes = 1;
    public const int MaximumRefreshIntervalMinutes = 1440;
    public const bool UseAbsoluteResetTime = true;
    public const double PopupWindowWidth = 360;
    public const double PopupWindowHeight = 620;
    public const string AppName = "CodexTray";
    public const string SettingsFileName = "settings.json";
    public const string ModelPricingFileName = "model-pricing.json";
    public const string ResourcesDirectoryName = "Resources";
    public const string StartupRunValueName = "CodexTray";
    public const string PluginFileName = "CodexTray.json";
    public const string TrafficMonitorPluginFileName = "CodexTray.dll";
    public const string TrafficMonitorPluginConfigFileName = "CodexTray.ini";
    public const string PluginsDirectoryName = "Plugins";
    public const string LiteMonitorPluginSubdirectory = "LiteMonitor";
    public const string TrafficMonitorPluginSubdirectory = "TrafficMonitor";
    public const string PluginPathNone = "None";
    public const string UsageEndpointPath = "/codex-tray";
    public const string UsageTextEndpointPath = "/codex-tray.txt";
    public const string HealthEndpointPath = "/health";
    public const string UnavailableDisplay = "N/A";
    public const string RuntimeIdentifier = "win-x64";
    public const string GitHubOwner = "SnowyLake";
    public const string GitHubRepository = "CodexTray";
    public const string RepositoryUrl = "https://github.com/SnowyLake/CodexTray";
    public const string GitHubLatestReleaseUrl = "https://api.github.com/repos/SnowyLake/CodexTray/releases/latest";
    public const long MaximumReleasePackageBytes = 100L * 1024 * 1024;
    public const int UpdateCheckTimeoutSeconds = 20;
    public const int UpdateDownloadTimeoutSeconds = 120;
    public const int UpdateProcessWaitTimeoutSeconds = 600;
    public const string SingleInstanceMutexName = AppName + "Mutex";
    public const int UpdateFileCopyAttempts = 10;
    public const int UpdateFileCopyRetryDelayMilliseconds = 250;
    public const int UpdateWorkRetentionHours = 1;

    public static IReadOnlyList<string> ReleasePackageRelativePaths { get; } =
    [
        $"{AppName}.exe",
        Path.Combine(ResourcesDirectoryName, "icon.ico"),
        Path.Combine(ResourcesDirectoryName, "icon.png"),
        Path.Combine(ResourcesDirectoryName, ModelPricingFileName),
        Path.Combine(PluginsDirectoryName, LiteMonitorPluginSubdirectory, PluginFileName),
        Path.Combine(PluginsDirectoryName, TrafficMonitorPluginSubdirectory, TrafficMonitorPluginConfigFileName),
        Path.Combine(PluginsDirectoryName, TrafficMonitorPluginSubdirectory, TrafficMonitorPluginFileName),
    ];

    /// <summary>
    /// Builds the GitHub release asset name for a stable version.
    /// </summary>
    public static string BuildReleasePackageName(Version version)
    {
        return $"{AppName}-v{version.ToString(3)}-{RuntimeIdentifier}.zip";
    }

    /// <summary>
    /// Builds the JSON bridge URL for a port.
    /// </summary>
    public static string BuildBridgeUrl(int port)
    {
        return BuildLoopbackUrl(NormalizePort(port), UsageEndpointPath);
    }

    /// <summary>
    /// Builds the text bridge URL for a port.
    /// </summary>
    public static string BuildBridgeTextUrl(int port)
    {
        return BuildLoopbackUrl(NormalizePort(port), UsageTextEndpointPath);
    }

    /// <summary>
    /// Normalizes a TCP port to the supported range.
    /// </summary>
    private static int NormalizePort(int port)
    {
        return port >= MinimumPort && port <= MaximumPort ? port : Port;
    }

    /// <summary>
    /// Builds a loopback HTTP URL for a path.
    /// </summary>
    private static string BuildLoopbackUrl(int port, string path)
    {
        return $"http://{Host}:{port}{path}";
    }
}
