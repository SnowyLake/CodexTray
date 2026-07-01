# CodexMonitor

## 目录

- [概览](#概览)
- [展示图](#展示图)
- [主要功能](#主要功能)
- [安装使用](#安装使用)
- [托盘菜单](#托盘菜单)
- [额度刷新](#额度刷新)
- [安全与隐私](#安全与隐私)
- [常见问题](#常见问题)
- [工程现状](#工程现状)

## 概览

`CodexMonitor` 是一个 Windows 托盘小工具, 用来把 Codex 额度显示到 LiteMonitor.

它会在后台读取当前机器上的 Codex 登录信息, 获取 5 小时额度和 Weekly 额度, 然后把结果提供给 LiteMonitor 插件显示. 启动后程序会常驻系统托盘, 不需要一直打开窗口.

适合已经在使用 LiteMonitor, 并且希望在任务栏里直接看到 Codex 剩余额度的用户.

## 效果展示

![CodexMonitor showcase](Docs/showcase.png)

## 主要功能

- 显示 Codex 5 小时额度剩余百分比和重置时间.
- 显示 Codex Weekly 额度剩余百分比和重置日期或时间.
- 默认每 5 分钟自动刷新一次额度.
- 支持在设置窗口里修改刷新间隔.
- 支持点击 `Refresh Now` 立刻刷新.
- 支持一键安装 LiteMonitor 插件配置.
- 支持自动检测 LiteMonitor 路径.
- 支持随 Windows 开机自启动.
- 支持托盘菜单快速打开设置, 安装插件, 重启服务, 退出程序.

## 安装使用

1. 打开项目的 GitHub Releases 页面.
2. 下载 `CodexMonitor-vX.Y.Z-win-x64.zip`.
3. 解压后运行 `CodexMonitor.exe`.
4. 首次启动会打开设置窗口.
5. 确认 LiteMonitor 路径正确, 然后点击安装插件.
6. 重启 LiteMonitor, 或在 LiteMonitor 插件页面重载插件.

如果已经登录过 Codex, 通常不需要额外配置账号. 如果程序无法读取额度, 请先确认本机 Codex 可以正常使用.

## 托盘菜单

程序启动后会出现在 Windows 系统托盘. 关闭设置窗口不会退出程序.

托盘右键菜单包含:

- `Open Settings`: 打开设置窗口.
- `Install LiteMonitor Plugin`: 安装或覆盖 LiteMonitor 插件配置.
- `Open LiteMonitor Folder`: 打开当前 LiteMonitor 目录.
- `Restart Service`: 重启本地服务.
- `Exit`: 退出程序.

## 额度刷新

默认刷新间隔是 5 分钟. 你可以在设置窗口里修改 `Refresh interval (minutes)`.

点击 `Refresh Now` 会立即刷新一次额度, 并同步更新设置窗口和 LiteMonitor 插件读取到的数据.

Weekly 额度的重置显示规则:

- 如果重置时间是今天且还没有到达重置时刻, 显示具体时间.
- 如果重置时间不是今天, 显示日期.

## 安全与隐私

`CodexMonitor` 只在本机运行, 默认只监听 `127.0.0.1`.

程序会读取本机 Codex 登录信息来请求官方额度数据. 这些 token 不会显示在 LiteMonitor 中, 也不会通过本地接口返回.

请不要把自己的 Codex 登录文件, 设置文件, 或调试日志上传到公开位置.

## 常见问题

### 为什么 LiteMonitor 里没有变化

先确认 `CodexMonitor.exe` 正在运行, 然后在设置窗口点击 `Refresh Now`. 如果仍然没有变化, 重新安装 LiteMonitor 插件配置并重启 LiteMonitor.

### 为什么找不到 LiteMonitor

如果没有保存过路径, 程序会在本机磁盘里搜索 `LiteMonitor.exe`. 搜索可能需要一点时间. 也可以在设置窗口中手动选择 LiteMonitor 路径.

### 为什么额度显示不可用

通常是本机没有可用的 Codex 登录信息, 或当前网络无法请求额度接口. 先确认 Codex 本身能正常使用, 再点击 `Refresh Now`.

### 可以只运行单个 exe 吗

可以. 发布包里只需要运行 `CodexMonitor.exe`.

## 开发

当前工程是 C#/.NET Windows 托盘应用.

- `CodexMonitor.Core`: 额度采集, 缓存, 本地服务, 设置存储, LiteMonitor 插件安装.
- `CodexMonitor.App`: WinForms 托盘应用和设置窗口.
- `CodexMonitor.Tests`: C# 测试运行器.
- `LiteMonitorPlugin`: LiteMonitor 插件定义.
- `Scripts`: 发布, 重启, release 打包脚本.
- `Builds`: 本地构建和发布产物目录, 不提交生成内容.

开发验证命令:

```powershell
dotnet build .\CodexMonitor.sln -m:1
dotnet run --project .\CodexMonitor.Tests\CodexMonitor.Tests.csproj
```
