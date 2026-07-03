# TrafficMonitor Plugin

## 目录

- [概览](#概览)
- [构建](#构建)
- [安装](#安装)
- [配置](#配置)

## 概览

这个目录包含 CodexMonitor 的 TrafficMonitor 原生插件源码. 插件实现 TrafficMonitor 的 `ITMPlugin` 和 `IPluginItem` 接口, 提供 `Codex 5h` 和 `Codex Weekly` 两个显示项.

插件会请求本机 CodexMonitor 桥接服务的 `/codex-monitor` 接口, 并读取 JSON 里的 `display.codex_5h` 和 `display.codex_weekly`.

## 构建

需要安装 Visual Studio Build Tools, 并包含 C++ 桌面开发工作负载.

```powershell
.\Scripts\Build-TrafficMonitorPlugin.ps1
```

默认输出:

```text
Plugins\TrafficMonitor\Builds\x64\Release\CodexMonitor.dll
```

## 安装

启动 `CodexMonitor.exe`, 在设置窗口选择 TrafficMonitor 文件夹, 然后点击 `Install TrafficMonitor Plugin`. 托盘右键菜单里也有同名安装入口.

安装器会把 `CodexMonitor.dll` 复制到 TrafficMonitor 主程序目录的 `plugins` 文件夹, 并基于模板写入 `CodexMonitor.ini`.

## 配置

安装器生成的配置内容类似:

```ini
[CodexMonitor]
UsageUrl=http://127.0.0.1:17890/codex-monitor
RequestIntervalSeconds=60
```

`RequestIntervalSeconds` 控制插件向 CodexMonitor 桥接服务发起请求的最小间隔, 默认值为 `60`, 单位为秒. 如果 CodexMonitor 的服务端口变更, 重新点击 `Install TrafficMonitor Plugin` 即可同步更新这个配置.
