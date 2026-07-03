# CodexMonitor

## 目录

- [概览](#概览)
- [效果展示](#效果展示)
- [主要功能](#主要功能)
- [安装使用](#安装使用)
- [托盘菜单](#托盘菜单)
- [额度刷新](#额度刷新)
- [安全与隐私](#安全与隐私)
- [常见问题](#常见问题)
- [开发](#开发)

## 概览

`CodexMonitor` 是一个 Windows 托盘小工具, 用来把 Codex 额度显示到 LiteMonitor 或 TrafficMonitor.

它会在后台读取当前机器上的 Codex 登录信息, 获取 5 小时额度和 Weekly 额度, 然后把结果提供给监控器插件显示. 启动后程序会常驻系统托盘, 不需要一直打开窗口.

适合已经在使用 LiteMonitor 或 TrafficMonitor, 并且希望在任务栏里直接看到 Codex 剩余额度的用户.

## 效果展示

![CodexMonitor showcase](Docs/showcase.png)

## 主要功能

- 显示 Codex 5 小时额度剩余百分比和到期倒计时.
- 显示 Codex Weekly 额度剩余百分比和到期倒计时.
- 默认每 5 分钟自动刷新一次额度.
- 支持在设置窗口里修改刷新间隔.
- 支持点击 `Refresh Now` 立刻刷新.
- 支持一键安装 LiteMonitor 插件配置.
- 支持一键安装 TrafficMonitor 原生插件.
- 支持自动检测 LiteMonitor 和 TrafficMonitor 路径.
- 支持随 Windows 开机自启动.
- 支持托盘菜单快速打开设置, 安装插件, 重启服务, 退出程序.

## 安装使用

1. 打开项目的 GitHub Releases 页面.
2. 下载 `CodexMonitor-vX.Y.Z-win-x64.zip`.
3. 解压后运行发布目录里的 `CodexMonitor.exe`.
4. 首次启动会打开设置窗口.
5. 确认 LiteMonitor 或 TrafficMonitor 路径正确, 然后点击对应的安装插件按钮.
6. 重启对应监控器, 或在插件页面重载插件.

如果已经登录过 Codex, 通常不需要额外配置账号. 如果程序无法读取额度, 请先确认本机 Codex 可以正常使用.

从 Release zip 解压时, 发布目录会包含 `Resources` 和 `Plugins` 模板目录. TrafficMonitor 插件如果从源码运行, 需要先构建原生 DLL:

```powershell
.\Scripts\Build-TrafficMonitorPlugin.ps1
```

## 托盘菜单

程序启动后会出现在 Windows 系统托盘. 关闭设置窗口不会退出程序.

托盘右键菜单包含:

- `Open Settings`: 打开设置窗口.
- `Install LiteMonitor Plugin`: 安装或覆盖 LiteMonitor 插件配置.
- `Install TrafficMonitor Plugin`: 安装或覆盖 TrafficMonitor 插件 DLL 和配置.
- `Open LiteMonitor Folder`: 打开当前 LiteMonitor 目录.
- `Open TrafficMonitor Folder`: 打开当前 TrafficMonitor 目录.
- `Restart Service`: 重启本地服务.
- `Exit`: 退出程序.

## 额度刷新

默认刷新间隔是 5 分钟. 你可以在设置窗口里修改 `Refresh interval (minutes)`.

点击 `Refresh Now` 会立即刷新一次额度, 并同步更新设置窗口和监控器插件读取到的数据.

额度到期倒计时显示规则:

- 5 小时额度字段值使用 `88% [2h 45m]` 格式.
- Weekly 额度字段值使用 `66% [3d 04h]` 格式.
- 分钟或小时小于 10 时保留两位数字, 例如 `05m` 和 `04h`.
- `display.codex_5h` 和 `display.codex_weekly` 返回纯额度值. LiteMonitor 和 TrafficMonitor 插件保留宿主自己的 label, 并在 value 前按 `Codex Weekly` 的宽度补空格.

## 安全与隐私

`CodexMonitor` 只在本机运行, 默认只监听 `127.0.0.1`.

程序会读取本机 Codex 登录信息来请求官方额度数据. 这些 token 不会显示在 LiteMonitor 中, 也不会通过本地接口返回.

程序设置保存在 `CodexMonitor.exe` 同级目录下的 `settings.json`.

请不要把自己的 Codex 登录文件, 设置文件, 或调试日志上传到公开位置.

## 常见问题

### 为什么 LiteMonitor 或 TrafficMonitor 里没有变化

先确认 `CodexMonitor.exe` 正在运行, 然后在设置窗口点击 `Refresh Now`. 如果仍然没有变化, 重新安装对应插件并重启对应监控器.

### 为什么找不到 LiteMonitor 或 TrafficMonitor

如果没有保存过路径, 程序会在本机磁盘里搜索 `LiteMonitor.exe` 和 `TrafficMonitor.exe`. 搜索可能需要一点时间. 也可以在设置窗口中手动选择对应路径.

### 为什么额度显示不可用

通常是本机没有可用的 Codex 登录信息, 或当前网络无法请求额度接口. 先确认 Codex 本身能正常使用, 再点击 `Refresh Now`.

### 可以只复制单个 exe 吗

不建议. 程序主体仍然发布为单文件 `CodexMonitor.exe`, 但 `Resources` 和 `Plugins` 目录会作为外部资源随包发布, 这样托盘图标和插件模板才能正常读取.

## 开发

当前工程是 C#/.NET Windows 托盘应用.

- `CodexMonitor.Core`: 额度采集, 缓存, 本地服务, 设置存储, LiteMonitor 和 TrafficMonitor 插件安装.
- `CodexMonitor.App`: WinForms 托盘应用和设置窗口.
- `CodexMonitor.Tests`: C# 测试运行器.
- `Plugins/LiteMonitor`: LiteMonitor 插件定义.
- `Plugins/TrafficMonitor`: TrafficMonitor 插件源码和构建脚本.
- `Scripts`: 发布, 重启, release 打包脚本.
- `Builds`: 本地构建和发布产物目录, 不提交生成内容.
  - `Builds/Output/win-x64`: 本地发布和重启预览输出.
  - `Builds/Release/vX.Y.Z`: 正式 release 的版本化目录和 zip.

开发验证命令:

```powershell
dotnet build .\CodexMonitor.sln -m:1
dotnet run --project .\CodexMonitor.Tests\CodexMonitor.Tests.csproj
```
