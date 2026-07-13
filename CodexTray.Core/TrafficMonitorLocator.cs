namespace CodexTray.Core;

public static class TrafficMonitorLocator
{
    private const string k_ExecutableName = "TrafficMonitor.exe";

    /// <summary>
    /// Returns true when a directory looks like a TrafficMonitor installation.
    /// </summary>
    public static bool IsTrafficMonitorDirectory(string? directory)
    {
        return MonitorLocator.IsMonitorDirectory(directory, k_ExecutableName);
    }

    /// <summary>
    /// Attempts to find a TrafficMonitor installation directory.
    /// </summary>
    public static string AutoDetect(string? savedDirectory = null)
    {
        return MonitorLocator.AutoDetect(k_ExecutableName, savedDirectory);
    }
}
