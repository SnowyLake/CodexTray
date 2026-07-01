# CodexMonitor

## 目录

- [概览](#概览)
- [功能](#功能)
- [工作方式](#工作方式)
- [快速开始](#快速开始)
- [托盘应用](#托盘应用)
- [LiteMonitor 插件](#litemonitor-插件)
- [HTTP API](#http-api)
- [安全说明](#安全说明)
- [开发](#开发)

## 概览

`CodexMonitor` 为 LiteMonitor 提供 OpenAI Codex 使用量显示能力. `CodexMonitor.App.exe` 会在 Windows 系统托盘后台运行本地桥接服务, 并提供设置窗口安装 LiteMonitor 插件配置, 配置刷新间隔和启用开机自启动.

桥接服务优先读取 `~/.codex/auth.json` 中的 Codex OAuth 信息, 调用 ChatGPT 官方额度接口获取实时 `rate_limit` 数据, 然后把 5 小时额度和一周额度转换成 LiteMonitor 可解析的 JSON. 当 OAuth 凭据不存在时, 会回退扫描 `~/.codex/sessions/**/*.jsonl` 中的 `token_count` 事件.

当前工程是 C#/.NET 托盘实现.

## 功能

- 托盘后台运行本地桥接服务.
- 首次启动自动打开设置窗口, 后续默认进入托盘.
- 托盘右键支持打开设置, 安装 LiteMonitor 插件配置, 打开 LiteMonitor 文件夹, 重启服务, 停止运行.
- 显示 5 小时额度剩余百分比和重置时间.
- 显示一周额度剩余百分比和重置日期或时间.
- 设置窗口默认每 5 分钟自动刷新额度状态并写入进程内缓存, 可配置刷新间隔, 也可点击 `Refresh Now` 立刻刷新.
- 支持通过 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 启用当前用户开机自启动.
- 暂时不显示 Cost 相关信息.
- 读取 `~/.codex/auth.json` 仅用于请求官方额度接口, 不在本地 HTTP API 或 LiteMonitor 插件中暴露 access token.

## 工作方式

数据流如下:

1. Codex Desktop 在 `~/.codex/sessions` 下写入 JSONL session 文件.
2. `CodexMonitor.App.exe` 读取 `~/.codex/auth.json` 中的 OAuth `access_token` 和 `account_id`.
3. APP 请求 `https://chatgpt.com/backend-api/wham/usage`, 读取 `rate_limit.primary_window` 作为 5 小时窗口, 读取 `rate_limit.secondary_window` 作为一周窗口.
4. APP 将最新结果保存到进程内缓存.
5. 本地 HTTP 服务只返回缓存中的最新结果, 不在 LiteMonitor 请求到达时重新采集.
6. LiteMonitor 插件请求 `http://127.0.0.1:17890/codex-usage`, 并把缓存结果显示到任务栏.

## 快速开始

发布托盘版 exe:

```powershell
dotnet publish .\CodexMonitor.App\CodexMonitor.App.csproj -c Release -f net9.0-windows -r win-x64 -p:PublishSingleFile=true -p:SelfContained=false -o "..\Builds\Release\Publish\win-x64"
```

运行发布后的文件:

```text
Builds/Release/Publish/win-x64/CodexMonitor.App.exe
```

首次运行会打开设置窗口. 设置窗口中可以自动检测 LiteMonitor 路径, 设置刷新间隔, 安装插件配置, 并按需启用 `Start with Windows`.

服务启动后访问:

```text
http://127.0.0.1:17890/codex-usage
```

## 托盘应用

`CodexMonitor.App.exe` 使用 `.NET WinForms` 实现系统托盘体验. 程序启动后会保持后台服务运行, 关闭设置窗口不会停止托盘程序.

设置窗口会按 `Refresh interval (minutes)` 自动刷新当前额度状态, 默认值为 5 分钟. 点击 `Refresh Now` 可以立即请求官方额度接口并更新面板和插件读取的缓存.

托盘右键菜单包含:

- `Open Settings`: 打开设置窗口.
- `Install LiteMonitor Plugin`: 将内置 `CodexMonitor.json` 写入 LiteMonitor 的 `resources/plugins/` 目录.
- `Open LiteMonitor Folder`: 打开当前 LiteMonitor 目录.
- `Restart Service`: 重启本地 HTTP 服务.
- `Exit`: 停止 HTTP 服务并退出托盘程序.

设置保存位置:

```text
%APPDATA%/CodexMonitor/settings.json
```

LiteMonitor 自动搜索顺序:

1. 已保存路径.
2. 当没有可用保存路径时, 在本机已就绪的固定磁盘和可移动磁盘中递归搜索 `LiteMonitor.exe`.

## LiteMonitor 插件

插件文件位于:

```text
LiteMonitorPlugin/CodexMonitor.json
```

推荐通过托盘设置窗口或托盘右键菜单安装插件配置. 手动安装时, 将 `LiteMonitorPlugin/CodexMonitor.json` 放入 LiteMonitor 的 `resources/plugins/` 目录, 然后重启 LiteMonitor 或在 LiteMonitor 插件页面重载插件.

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

## 安全说明

桥接服务会读取 `~/.codex/auth.json` 中的 Codex OAuth token, 并只把 token 作为 `Authorization: Bearer` 请求头发送到 `https://chatgpt.com/backend-api/wham/usage`. 本地 HTTP API 不返回 token, 不读取浏览器 cookie, 也不向 LiteMonitor 暴露 access token.

默认监听地址是 `127.0.0.1`, 不接受局域网访问. 如果修改监听地址, 需要自行确认网络暴露风险.

## 开发

构建全部 .NET 项目:

```powershell
dotnet build .\CodexMonitor.sln
```

构建产物统一输出到 `Builds/{Configuration}/{ProjectName}`. 例如 `CodexMonitor.App` 的 Debug 中间产物位于 `Builds/Debug/CodexMonitor.App/obj`, 编译产物位于 `Builds/Debug/CodexMonitor.App/bin`.

运行 C# 测试:

```powershell
dotnet run --project .\CodexMonitor.Tests\CodexMonitor.Tests.csproj
```

主要文件:

- `CodexMonitor.Core`: C# 额度解析, HTTP 服务, 设置, 插件安装, 自启管理.
- `CodexMonitor.App`: WinForms 托盘应用和设置窗口.
- `CodexMonitor.Tests`: C# 测试运行器.
- `LiteMonitorPlugin/CodexMonitor.json`: LiteMonitor 插件定义.
- `Builds`: 构建与发布产物目录.
