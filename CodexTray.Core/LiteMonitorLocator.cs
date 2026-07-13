namespace CodexTray.Core;

public static class LiteMonitorLocator
{
    private const string k_ExecutableName = "LiteMonitor.exe";

    /// <summary>
    /// Returns true when a directory looks like a LiteMonitor installation.
    /// </summary>
    public static bool IsLiteMonitorDirectory(string? directory)
    {
        return MonitorLocator.IsMonitorDirectory(directory, k_ExecutableName);
    }

    /// <summary>
    /// Attempts to find a LiteMonitor installation directory.
    /// </summary>
    public static string AutoDetect(string? savedDirectory = null)
    {
        return MonitorLocator.AutoDetect(k_ExecutableName, savedDirectory);
    }
}
