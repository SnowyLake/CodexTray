using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMonitor.Core;

public static class LiteMonitorPluginInstaller
{
    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private const string k_FallbackPluginJson = """
{
  "id": "CodexMonitor",
  "meta": {
    "name": "Codex Monitor",
    "version": "1.0.0",
    "author": "SnowyLake",
    "description": "显示 OpenAI Codex 5 小时额度和一周额度的剩余百分比与重置时间"
  },
  "inputs": [
    {
      "key": "bridge_url",
      "label": "桥接服务地址",
      "type": "text",
      "default": "http://127.0.0.1:17890/codex-monitor",
      "placeholder": "http://127.0.0.1:17890/codex-monitor",
      "scope": "global"
    }
  ],
  "execution": {
    "type": "chain",
    "interval": 60,
    "steps": [
      {
        "id": "fetch_codex_monitor",
        "url": "{{bridge_url}}",
        "method": "GET",
        "response_format": "json",
        "extract": {
          "codex_5h_display": "display.codex_5h",
          "codex_weekly_display": "display.codex_weekly"
        }
      }
    ]
  },
  "outputs": [
    {
      "key": "codex_5h",
      "label": "Codex 5h",
      "short_label": "Codex 5h",
      "format_val": "     {{codex_5h_display}}",
      "unit": ""
    },
    {
      "key": "codex_weekly",
      "label": "Codex Weekly",
      "short_label": "Codex Weekly",
      "format_val": " {{codex_weekly_display}}",
      "unit": ""
    }
  ]
}
""";

    /// <summary>
    /// Installs the LiteMonitor plugin JSON into the selected directory.
    /// </summary>
    public static string Install(string liteMonitorDirectory, int port = CodexMonitorDefaults.Port)
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
        string targetPath = Path.Combine(pluginDirectory, CodexMonitorDefaults.PluginFileName);
        File.WriteAllText(targetPath, BuildPluginJson(port));
        return targetPath;
    }

    /// <summary>
    /// Builds the LiteMonitor plugin JSON from the packaged template.
    /// </summary>
    private static string BuildPluginJson(int port)
    {
        int normalizedPort = port is > 0 and <= 65535 ? port : CodexMonitorDefaults.Port;
        string bridgeUrl = $"http://127.0.0.1:{normalizedPort}{CodexMonitorDefaults.UsageEndpointPath}";
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
        string templatePath = Path.Combine(AppContext.BaseDirectory, "Plugins", "LiteMonitor", CodexMonitorDefaults.PluginFileName);
        return File.Exists(templatePath) ? File.ReadAllText(templatePath) : k_FallbackPluginJson;
    }
}
