# CodexUsage-LiteMonitor-Plugin

## 目录

- [概览](#概览)
- [功能](#功能)
- [工作方式](#工作方式)
- [快速开始](#快速开始)
- [LiteMonitor 插件](#litemonitor-插件)
- [开机自启](#开机自启)
- [HTTP API](#http-api)
- [安全说明](#安全说明)
- [开发](#开发)

## 概览

`CodexUsage-LiteMonitor-Plugin` 为 LiteMonitor 提供 OpenAI Codex 使用量显示能力. 它包含一个只监听 `127.0.0.1` 的本地桥接服务, 以及一个 LiteMonitor JSON 插件.

桥接服务参考 `CodexBar-Win` 的 OpenAI Codex 实现思路, 从 `~/.codex/sessions/**/*.jsonl` 中读取 Codex Desktop 写入的 `token_count` 事件, 然后把 5 小时额度和一周额度转换成 LiteMonitor 可解析的 JSON.

## 功能

- 显示 5 小时额度剩余百分比和重置时间.
- 显示一周额度剩余百分比和重置日期或时间.
- 暂时不显示 Cost 相关信息.
- 不读取 `~/.codex/auth.json`, 不接触 access token.
- 仅使用 Python 标准库, 不需要安装第三方依赖.

## 工作方式

数据流如下:

1. Codex Desktop 在 `~/.codex/sessions` 下写入 JSONL session 文件.
2. 本地桥接服务扫描 JSONL 文件中的 `payload.type == "token_count"` 事件.
3. 服务读取 `payload.rate_limits.primary` 作为 5 小时窗口, 读取 `payload.rate_limits.secondary` 作为一周窗口.
4. 服务按 5 小时窗口输出 `Codex 5h          {剩余百分比}  {重置时间}`.
5. 服务按一周窗口输出 `Codex Weekly  {剩余百分比}  {重置日期或时间}`.
6. LiteMonitor 插件请求 `http://127.0.0.1:17890/codex-usage`, 并把返回结果显示到任务栏.

## 快速开始

启动桥接服务:

```powershell
python .\src\codex_usage_bridge.py
```

如果系统 `python` 不可用, 可以指定 Codex Desktop 自带 Python 或其他 Python 3.10+ 解释器:

```powershell
& "$env:USERPROFILE\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe" .\src\codex_usage_bridge.py
```

服务启动后访问:

```text
http://127.0.0.1:17890/codex-usage
```

也可以使用脚本启动:

```powershell
.\scripts\start_bridge.ps1
```

PowerShell 辅助脚本默认会在结束时等待 Enter, 方便双击运行时查看输出. 自动化调用时可以追加 `-NoPause`.

## LiteMonitor 插件

插件文件位于:

```text
litemonitor/CodexUsage.json
```

安装方式:

1. 将 `litemonitor/CodexUsage.json` 放入 LiteMonitor 的 `resources/plugins/` 目录.
2. 重启 LiteMonitor, 或在 LiteMonitor 插件页面重载插件.
3. 启用 `Codex 使用量` 插件.
4. 在 LiteMonitor 的监控项显示页面开启对应任务栏显示项.

如果 LiteMonitor 安装在默认测试路径, 可以使用:

```powershell
.\scripts\install_litemonitor_plugin.ps1 -LiteMonitorDir "D:\Tools\LiteMonitor_v1.3.6-win-x64"
```

## 开机自启

可以注册一个登录时启动的计划任务:

```powershell
.\scripts\install_startup_task.ps1
```

如果系统默认 `python` 不可用, 可以指定 Python 路径:

```powershell
.\scripts\install_startup_task.ps1 -Python "$env:USERPROFILE\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
```

计划任务只启动本地桥接服务, 默认监听 `127.0.0.1:17890`.

## HTTP API

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

桥接服务只读取 `~/.codex/sessions/**/*.jsonl`. 它不会读取 `~/.codex/auth.json`, 不会访问 OpenAI API, 不会读取浏览器 cookie, 也不会暴露 access token.

默认监听地址是 `127.0.0.1`, 不接受局域网访问. 如果修改监听地址, 需要自行确认网络暴露风险.

## 开发

运行测试:

```powershell
python -m unittest discover -s tests
```

主要文件:

- `src/codex_usage_bridge.py`: 本地 HTTP 服务和 JSONL 解析逻辑.
- `litemonitor/CodexUsage.json`: LiteMonitor 插件定义.
- `scripts/start_bridge.ps1`: 启动桥接服务.
- `scripts/install_litemonitor_plugin.ps1`: 安装 LiteMonitor 插件文件.
