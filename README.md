# CodexUsage-LiteMonitor-Plugin

## 目录

- [概览](#概览)
- [功能](#功能)
- [工作方式](#工作方式)
- [快速开始](#快速开始)
- [托盘应用](#托盘应用)
- [LiteMonitor 插件](#litemonitor-插件)
- [HTTP API](#http-api)
- [Legacy PowerShell 方式](#legacy-powershell-方式)
- [安全说明](#安全说明)
- [开发](#开发)

## 概览

`CodexUsage-LiteMonitor-Plugin` 为 LiteMonitor 提供 OpenAI Codex 使用量显示能力. 推荐使用 `CodexUsageTray.exe`, 它会在 Windows 系统托盘后台运行本地桥接服务, 并提供设置窗口安装 LiteMonitor 插件配置和启用开机自启动.

桥接服务参考 `CodexBar-Win` 的 OpenAI Codex 实现思路, 从 `~/.codex/sessions/**/*.jsonl` 中读取 Codex Desktop 写入的 `token_count` 事件, 然后把 5 小时额度和一周额度转换成 LiteMonitor 可解析的 JSON.

## 功能

- 托盘后台运行, 不需要手动执行 PowerShell 脚本.
- 首次启动自动打开设置窗口, 后续默认进入托盘.
- 托盘右键支持打开设置, 安装 LiteMonitor 插件配置, 打开 LiteMonitor 文件夹, 重启服务, 停止运行.
- 显示 5 小时额度剩余百分比和重置时间.
- 显示一周额度剩余百分比和重置日期或时间.
- 支持通过 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 启用当前用户开机自启动.
- 暂时不显示 Cost 相关信息.
- 不读取 `~/.codex/auth.json`, 不接触 access token.

## 工作方式

数据流如下:

1. Codex Desktop 在 `~/.codex/sessions` 下写入 JSONL session 文件.
2. `CodexUsageTray.exe` 扫描 JSONL 文件中的 `payload.type == "token_count"` 事件.
3. 本地 HTTP 服务读取 `payload.rate_limits.primary` 作为 5 小时窗口, 读取 `payload.rate_limits.secondary` 作为一周窗口.
4. 服务按 5 小时窗口输出 `Codex 5h          {剩余百分比}  {重置时间}`.
5. 服务按一周窗口输出 `Codex Weekly  {剩余百分比}  {重置日期或时间}`.
6. LiteMonitor 插件请求 `http://127.0.0.1:17890/codex-usage`, 并把返回结果显示到任务栏.

## 快速开始

发布托盘版 exe:

```powershell
dotnet publish .\src\CodexUsageTray\CodexUsageTray.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -o .\artifacts\CodexUsageTray\win-x64
```

运行发布后的文件:

```text
artifacts/CodexUsageTray/win-x64/CodexUsageTray.exe
```

首次运行会打开设置窗口. 设置窗口中可以自动检测 LiteMonitor 路径, 安装插件配置, 并按需启用 `Start with Windows`.

服务启动后访问:

```text
http://127.0.0.1:17890/codex-usage
```

## 托盘应用

`CodexUsageTray.exe` 使用 `.NET WinForms` 实现系统托盘体验. 程序启动后会保持后台服务运行, 关闭设置窗口不会停止托盘程序.

托盘右键菜单包含:

- `Open Settings`: 打开设置窗口.
- `Install LiteMonitor Plugin`: 将内置 `CodexUsage.json` 写入 LiteMonitor 的 `resources/plugins/` 目录.
- `Open LiteMonitor Folder`: 打开当前 LiteMonitor 目录.
- `Restart Service`: 重启本地 HTTP 服务.
- `Exit`: 停止 HTTP 服务并退出托盘程序.

设置保存位置:

```text
%APPDATA%/CodexUsageLiteMonitor/settings.json
```

LiteMonitor 自动搜索顺序:

1. 已保存路径.
2. `D:\Tools\LiteMonitor_v1.3.6-win-x64`.
3. `D:\Tools\LiteMonitor*`.
4. `%LOCALAPPDATA%`, `%PROGRAMFILES%`, `%PROGRAMFILES(X86)%` 下的 `LiteMonitor.exe`.

## LiteMonitor 插件

插件文件位于:

```text
litemonitor/CodexUsage.json
```

推荐通过托盘设置窗口或托盘右键菜单安装插件配置. 手动安装时, 将 `litemonitor/CodexUsage.json` 放入 LiteMonitor 的 `resources/plugins/` 目录, 然后重启 LiteMonitor 或在 LiteMonitor 插件页面重载插件.

## HTTP API

`GET /health` 返回:

```json
{"ok":true}
```

`GET /codex-usage` 返回示例:

```json
{
  "available": true,
  "plan_type": "plus",
  "updated_at": "2026-07-01T12:00:00+08:00",
  "limits": {
    "five_hour": {
      "used_percent": 10,
      "remaining_percent": 90,
      "window_minutes": 300,
      "reset_time": "15:45"
    },
    "weekly": {
      "used_percent": 9,
      "remaining_percent": 91,
      "window_minutes": 10080,
      "reset_time": "10:41",
      "reset_label": "07-07"
    }
  },
  "display": {
    "codex_5h": "90%  15:45",
    "codex_weekly": "91%  07-07",
    "summary": "Codex 5h: 90%  15:45 | Codex Weekly: 91%  07-07"
  }
}
```

## Legacy PowerShell 方式

PowerShell 脚本仍然保留为 legacy fallback. 如果不使用托盘版 exe, 可以继续启动 Python bridge:

```powershell
.\scripts\start_bridge.ps1
```

安装 LiteMonitor 插件配置:

```powershell
.\scripts\install_litemonitor_plugin.ps1 -LiteMonitorDir "D:\Tools\LiteMonitor_v1.3.6-win-x64"
```

注册旧版计划任务自启:

```powershell
.\scripts\install_startup_task.ps1
```

自动化调用这些脚本时可以追加 `-NoPause`.

## 安全说明

桥接服务只读取 `~/.codex/sessions/**/*.jsonl`. 它不会读取 `~/.codex/auth.json`, 不会访问 OpenAI API, 不会读取浏览器 cookie, 也不会暴露 access token.

默认监听地址是 `127.0.0.1`, 不接受局域网访问. 如果修改监听地址, 需要自行确认网络暴露风险.

## 开发

构建全部 .NET 项目:

```powershell
dotnet build .\CodexUsage.sln
```

运行 C# 测试:

```powershell
dotnet run --project .\tests\CodexUsage.Tests\CodexUsage.Tests.csproj
```

运行 legacy Python 测试:

```powershell
python -m unittest discover -s tests
```

主要文件:

- `src/CodexUsage.Core`: C# 额度解析, HTTP 服务, 设置, 插件安装, 自启管理.
- `src/CodexUsageTray`: WinForms 托盘应用和设置窗口.
- `tests/CodexUsage.Tests`: C# 测试运行器.
- `src/codex_usage_bridge.py`: legacy Python 本地 HTTP 服务.
- `litemonitor/CodexUsage.json`: LiteMonitor 插件定义.
