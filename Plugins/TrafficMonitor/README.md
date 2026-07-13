# TrafficMonitor Plugin

## 目录

- [概览](#概览)
- [构建](#构建)
- [安装](#安装)
- [配置](#配置)

## 概览

这个目录包含 CodexTray 的 TrafficMonitor 原生插件源码. 插件实现 TrafficMonitor 的 `ITMPlugin` 和 `IPluginItem` 接口, 提供 `Codex 5-Hour` 和 `Codex 7-Day` 两个显示项.

插件会请求本机 CodexTray 桥接服务的 `/codex-tray.txt` 接口, 并读取两行文本值.

## 构建

需要安装 Visual Studio Build Tools, 并包含 C++ 桌面开发工作负载.

```powershell
.\Scripts\Build-TrafficMonitorPlugin.ps1
```

默认输出:

```text
Plugins\TrafficMonitor\Builds\x64\Release\CodexTray.dll
```

## 安装

启动 `CodexTray.exe`, 在设置窗口选择 TrafficMonitor 文件夹, 然后点击 `Install TrafficMonitor Plugin`. 托盘右键菜单里也有同名安装入口.

安装器会把 `CodexTray.dll` 复制到 TrafficMonitor 主程序目录的 `plugins` 文件夹, 并基于模板写入 `CodexTray.ini`.

## 配置

安装器生成的配置内容类似:

```ini
[CodexTray]
UsageUrl=http://127.0.0.1:17890/codex-tray.txt
```

如果 CodexTray 的服务端口变更, 可以在 TrafficMonitor 的插件管理中打开 CodexTray 选项面板并更新 `UsageUrl`, 也可以重新点击 `Install TrafficMonitor Plugin` 同步写入当前后端网址.
