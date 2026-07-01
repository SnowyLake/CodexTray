namespace CodexMonitor.Core;

public static class LiteMonitorPluginInstaller
{
    public const string PluginJson = """
{
  "id": "CodexUsage",
  "meta": {
    "name": "Codex 使用量",
    "version": "1.0.0",
    "author": "SnowyLake",
    "description": "显示 OpenAI Codex 5 小时额度和一周额度的剩余百分比与重置时间"
  },
  "inputs": [
    {
      "key": "bridge_url",
      "label": "桥接服务地址",
      "type": "text",
      "default": "http://127.0.0.1:17890/codex-usage",
      "placeholder": "http://127.0.0.1:17890/codex-usage",
      "scope": "global"
    }
  ],
  "execution": {
    "type": "chain",
    "interval": 60,
    "steps": [
      {
        "id": "fetch_codex_usage",
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
      "format_val": "{{codex_5h_display}}",
      "unit": ""
    },
    {
      "key": "codex_weekly",
      "label": "Codex Weekly",
      "short_label": "Codex Weekly",
      "format_val": "{{codex_weekly_display}}",
      "unit": ""
    }
  ]
}
""";

    /// <summary>
    /// Installs the LiteMonitor plugin JSON into the selected directory.
    /// </summary>
    public static string Install(string liteMonitorDirectory)
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
        string targetPath = Path.Combine(pluginDirectory, CodexUsageDefaults.PluginFileName);
        File.WriteAllText(targetPath, PluginJson);
        return targetPath;
    }
}
