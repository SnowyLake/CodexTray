using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexTray.Core;

public static class LiteMonitorPluginInstaller
{
    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>
    /// Installs the LiteMonitor plugin JSON into the selected directory.
    /// </summary>
    public static string Install(string liteMonitorDirectory, int port = CodexTrayDefaults.Port)
    {
        if (!LiteMonitorLocator.IsLiteMonitorDirectory(liteMonitorDirectory))
        {
            throw new DirectoryNotFoundException($"LiteMonitor.exe not found in {liteMonitorDirectory}");
        }

        string resourcesDirectory = Path.Combine(liteMonitorDirectory, "resources");
        if (!Directory.Exists(resourcesDirectory))
        {
            throw new DirectoryNotFoundException($"LiteMonitor resources directory not found: {resourcesDirectory}");
        }

        string pluginDirectory = Path.Combine(resourcesDirectory, "plugins");
        Directory.CreateDirectory(pluginDirectory);
        string targetPath = Path.Combine(pluginDirectory, CodexTrayDefaults.PluginFileName);
        File.WriteAllText(targetPath, BuildPluginJson(port));
        return targetPath;
    }

    /// <summary>
    /// Builds the LiteMonitor plugin JSON from the packaged template.
    /// </summary>
    private static string BuildPluginJson(int port)
    {
        string bridgeUrl = CodexTrayDefaults.BuildBridgeUrl(port);
        JsonNode root = JsonNode.Parse(ReadTemplateJson()) ?? throw new InvalidOperationException("LiteMonitor plugin template is empty.");
        JsonArray inputs = root["inputs"]?.AsArray() ?? throw new InvalidOperationException("LiteMonitor plugin template has no inputs.");
        bool bridgeInputUpdated = false;

        foreach (JsonNode? input in inputs)
        {
            if (input is not JsonObject inputObject)
            {
                continue;
            }

            if (string.Equals(inputObject["key"]?.GetValue<string>(), "bridge_url", StringComparison.Ordinal))
            {
                inputObject["default"] = bridgeUrl;
                inputObject["placeholder"] = bridgeUrl;
                bridgeInputUpdated = true;
                break;
            }
        }

        if (!bridgeInputUpdated)
        {
            throw new InvalidOperationException("LiteMonitor plugin template has no bridge_url input.");
        }

        return root.ToJsonString(s_JsonOptions);
    }

    /// <summary>
    /// Reads the packaged LiteMonitor plugin template when available.
    /// </summary>
    private static string ReadTemplateJson()
    {
        string templatePath = Path.Combine(AppContext.BaseDirectory, CodexTrayDefaults.PluginsDirectoryName, CodexTrayDefaults.LiteMonitorPluginSubdirectory, CodexTrayDefaults.PluginFileName);
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException($"LiteMonitor plugin template was not found: {templatePath}");
        }

        return File.ReadAllText(templatePath);
    }
}
