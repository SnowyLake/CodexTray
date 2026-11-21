# TrafficMonitor Plugin

## 目录

- [概览](#概览)
- [构建](#构建)
- [安装](#安装)
- [配置](#配置)

## 概览

这个目录包含 CodexTray 的 TrafficMonitor 原生插件源码. 插件实现 TrafficMonitor 的 `ITMPlugin` 和 `IPluginItem` 接口, 提供 `Codex`, `Grok`, `Gemini` 和 `OpenCode` 四个显示项.

插件在后台请求本机 CodexTray 的 `/codex-tray.txt`, 依次读取 Codex Weekly, Grok Weekly, Gemini 5 小时额度 和 OpenCode 5 小时 Session 额度. 刷新期间继续显示上一组值. 请求超过 5 秒或正文超过 4096 字节时, 四项均显示 `N/A`.

## 构建

需要安装 Visual Studio Build Tools, 并包含 C++ 桌面开发工作负载.

```powershell
.\Scripts\Build-TrafficMonitorPlugin.ps1
```

默认输出:

```text
Plugins\TrafficMonitor\Builds\x64\Release\CodexTray.dll
```

以下命令构建 Release DLL 并运行原生插件回归测试.

```powershell
.\Scripts\Test-TrafficMonitorPlugin.ps1
```

## 安装

首次安装或升级插件时, 建议按以下步骤操作:

1. 若 TrafficMonitor 正在运行, 请先退出以解除 `CodexTray.dll` 占用.
2. 启动 `CodexTray.exe`, 在 Settings 页选择 TrafficMonitor 文件夹, 然后点击对应的 `Setup` 按钮.
3. 启动 TrafficMonitor, 在主窗口/任务栏显示设置中勾选需要的监控项 (例如 `Codex`, `Grok`, `Gemini`, `OpenCode`).

安装器会把 `CodexTray.dll` 复制到 TrafficMonitor 主程序目录的 `plugins` 文件夹, 并基于模板写入 `CodexTray.ini`.

## 配置

安装器生成的配置内容类似:

```ini
[CodexTray]
UsageUrl=http://127.0.0.1:17890/codex-tray.txt
```

如果 CodexTray 的服务端口变更, 可以在 TrafficMonitor 的插件管理中打开 CodexTray 选项面板并更新 `UsageUrl`, 也可以重新点击 `Install TrafficMonitor Plugin` 同步写入当前后端网址.
