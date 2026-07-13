namespace CodexTray.Core;

public static class TrafficMonitorPluginInstaller
{
    /// <summary>
    /// Installs the TrafficMonitor plugin DLL and local configuration into the selected directory.
    /// </summary>
    public static string Install(string trafficMonitorDirectory, int port, string? pluginBinaryPath = null)
    {
        if (!TrafficMonitorLocator.IsTrafficMonitorDirectory(trafficMonitorDirectory))
        {
            throw new DirectoryNotFoundException($"TrafficMonitor.exe not found in {trafficMonitorDirectory}");
        }

        string sourcePath = ResolvePluginBinaryPath(pluginBinaryPath);
        string pluginDirectory = Path.Combine(trafficMonitorDirectory, "plugins");
        Directory.CreateDirectory(pluginDirectory);

        string targetPath = Path.Combine(pluginDirectory, CodexTrayDefaults.TrafficMonitorPluginFileName);
        File.Copy(sourcePath, targetPath, true);

        string configPath = Path.Combine(pluginDirectory, CodexTrayDefaults.TrafficMonitorPluginConfigFileName);
        File.WriteAllText(configPath, BuildPluginConfig(port));
        return targetPath;
    }

    /// <summary>
    /// Builds the TrafficMonitor plugin configuration content.
    /// </summary>
    private static string BuildPluginConfig(int port)
    {
        string bridgeUrl = CodexTrayDefaults.BuildBridgeTextUrl(port);
        return SetUsageUrl(ReadTemplateConfig(), bridgeUrl);
    }

    /// <summary>
    /// Sets the backend URL in the TrafficMonitor plugin configuration content.
    /// </summary>
    private static string SetUsageUrl(string content, string bridgeUrl)
    {
        string normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = normalizedContent.TrimEnd('\n').Split('\n');
        bool updated = false;
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("UsageUrl=", StringComparison.Ordinal))
            {
                lines[index] = $"UsageUrl={bridgeUrl}";
                updated = true;
            }
        }

        List<string> outputLines = [.. lines];
        if (!updated)
        {
            outputLines.Add($"UsageUrl={bridgeUrl}");
        }

        return string.Join(Environment.NewLine, outputLines) + Environment.NewLine;
    }

    /// <summary>
    /// Reads the packaged TrafficMonitor configuration template when available.
    /// </summary>
    private static string ReadTemplateConfig()
    {
        string templatePath = Path.Combine(AppContext.BaseDirectory, CodexTrayDefaults.PluginsDirectoryName, CodexTrayDefaults.TrafficMonitorPluginSubdirectory, CodexTrayDefaults.TrafficMonitorPluginConfigFileName);
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException($"TrafficMonitor plugin config template was not found: {templatePath}");
        }

        return File.ReadAllText(templatePath);
    }

    /// <summary>
    /// Resolves the compiled TrafficMonitor plugin DLL path.
    /// </summary>
    private static string ResolvePluginBinaryPath(string? pluginBinaryPath)
    {
        if (!string.IsNullOrWhiteSpace(pluginBinaryPath))
        {
            return EnsurePluginBinaryExists(pluginBinaryPath);
        }

        foreach (string candidate in EnumeratePluginBinaryCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"TrafficMonitor plugin DLL was not found. Run Scripts\\Build-TrafficMonitorPlugin.ps1 first, then install {CodexTrayDefaults.TrafficMonitorPluginFileName}.");
    }

    /// <summary>
    /// Returns a plugin binary path when it exists.
    /// </summary>
    private static string EnsurePluginBinaryExists(string pluginBinaryPath)
    {
        if (!File.Exists(pluginBinaryPath))
        {
            throw new FileNotFoundException($"TrafficMonitor plugin DLL was not found: {pluginBinaryPath}");
        }

        return pluginBinaryPath;
    }

    /// <summary>
    /// Enumerates likely locations for the compiled TrafficMonitor plugin DLL.
    /// </summary>
    private static IEnumerable<string> EnumeratePluginBinaryCandidates()
    {
        string pluginsDirectory = CodexTrayDefaults.PluginsDirectoryName;
        string trafficMonitorSubdirectory = CodexTrayDefaults.TrafficMonitorPluginSubdirectory;
        string pluginFileName = CodexTrayDefaults.TrafficMonitorPluginFileName;

        string baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, pluginsDirectory, trafficMonitorSubdirectory, pluginFileName);
        yield return Path.Combine(baseDirectory, pluginFileName);

        string currentDirectory = Directory.GetCurrentDirectory();
        yield return Path.Combine(currentDirectory, pluginsDirectory, trafficMonitorSubdirectory, pluginFileName);
        yield return Path.Combine(currentDirectory, pluginsDirectory, trafficMonitorSubdirectory, "Builds", "x64", "Release", pluginFileName);
        yield return Path.Combine(currentDirectory, pluginsDirectory, trafficMonitorSubdirectory, "Builds", "x64", "Debug", pluginFileName);
    }
}
