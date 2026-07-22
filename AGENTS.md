# CodexTray Agent Guide

## 目录

- [文档职责](#文档职责)
- [项目概览](#项目概览)
- [架构与数据流](#架构与数据流)
- [目录结构](#目录结构)
- [关键约束](#关键约束)
- [构建与输出](#构建与输出)
- [验证工作流](#验证工作流)
- [发布流程](#发布流程)

## 文档职责

本文档记录 CodexTray 的项目特定技术上下文, 架构约束和工作流. 面向用户的功能, 安装和使用说明维护在 `README.md`.

## 项目概览

`CodexTray` 是一个 C#/.NET 9 Windows x64 托盘应用. 桌面 UI 使用 WPF, `System.Windows.Forms` 仅用于 `NotifyIcon`, 文件夹选择, 系统消息框和应用初始化.

应用读取 `~/.codex/auth.json` 中的 OAuth 凭据, 请求 ChatGPT 官方 usage 与 rate-limit-reset-credits 接口, 再通过仅监听 loopback 的本地 HTTP 服务向 LiteMonitor 和 TrafficMonitor 提供额度数据. OAuth 凭据缺失或无效时返回不可用状态.

Token Cost 是独立的本地统计: `TokenCostCollector` 读取 `~/.codex/sessions/**/*.jsonl` 和 `~/.codex/archived_sessions/*.jsonl`, 使用 `Resources/model-pricing.json` 计算 token 总量和 API 等价成本, 再显示在 WPF 主面板.

API 监控也是独立链路: `ApiUsageCollector` 查询 DeepSeek 与 NewAPI, 并委托 `GrokUsageCollector` 与 `CursorUsageCollector` 使用本地 OAuth session 查询 Grok 与 Cursor 用量. API 监控结果只显示在 WPF 主面板, 不进入插件 HTTP 响应.

## 架构与数据流

### 应用生命周期

1. `CodexTray.App/Program.cs` 使用 mutex 保证单实例. 后续进程通过 `TrayShowPanel` event 通知已有实例打开面板后退出.
2. `CodexTray.App/App.cs` 创建 WPF application host, `TrayController` 管理托盘, 设置, 定时刷新, 插件安装和本地服务.
3. 首次启动由 `SettingsStore` 写入默认 `settings.json` 并打开主面板. 后续设置加载时会补齐缺失字段并规范化值.
4. `TrayPopupWindow` 与 `TrayPopupViewModel` 提供 Home/APIs/Settings 三页. `ApiMonitorViewModel` 管理单张 API 卡片的编辑与显示状态. 左键切换弹窗, 右键菜单仅包含 `Open Panel`, `Refresh Now` 和 `Exit`.

### 额度与插件链路

1. `CodexTrayCollector` 从官方接口采集 5-Hour, 7-Day 和 Reset Credits.
2. `TrayController` 将最新 `UsageResponse` 写入 `UsageCache`.
3. `LightweightHttpServer` 默认监听 `127.0.0.1:17890`, 暴露以下接口:
   - `/codex-tray`: LiteMonitor 使用的 JSON 响应.
   - `/codex-tray.txt`: TrafficMonitor 使用的两行文本, 依次为 5-Hour 和 7-Day.
   - `/health`: 返回本地服务健康状态.
4. `LiteMonitorPluginInstaller` 和 `TrafficMonitorPluginInstaller` 从发布目录读取模板, 写入当前端口后安装到监控器目录.

### API 监控链路

1. `AppSettings.ApiMonitors` 保存 API 卡片顺序与 provider 配置. `TrayPopupViewModel` 负责增删, 排序和持久化卡片.
2. `ApiUsageCollector` 并行刷新所有卡片. DeepSeek 使用 `/user/balance`, NewAPI 使用 `/api/user/self` 并发送 `New-Api-User` header.
3. `GrokUsageCollector` 从 Grok Build 的 `auth.json` 或 OpenCode 的 `auth.json` 读取 xAI OAuth access token. 临近过期或收到 401/403 时, 使用 refresh token 调用 `auth.x.ai` 刷新并写回对应 auth 文件, 再请求 Grok billing gRPC-web 接口解析剩余额度和重置时间.
4. `CursorUsageCollector` 从 `%APPDATA%\Cursor\User\globalStorage\state.vscdb` 读取 `cursorAuth/accessToken` 与 `refreshToken`. 临近过期或收到 401/403 时, 使用 refresh token 调用 `api2.cursor.sh/oauth/token` 刷新并写回 vscdb, 再请求 `cursor.com/api/usage-summary` 解析剩余额度和重置时间.
5. `TrayController` 将结果交给 `TrayPopupViewModel` 更新单卡片状态与 APIs 页汇总状态. 这些结果不写入 `UsageCache`.

### 设置边界

- 默认值, 端口范围, HTTP 路径, 文件名和发布资源目录统一维护在 `CodexTrayDefaults`.
- 刷新间隔范围为 1 到 1440 分钟, 默认 1 分钟.
- 主题支持 `System`, `Light`, `Dark`. Acrylic 默认开启, 透明度默认 80%, 范围为 10% 到 100%.
- API provider 支持 `DeepSeek`, `NewAPI`, `Grok`, `Cursor`. DeepSeek 与 NewAPI 的凭据以明文保存在 `settings.json`, Grok 只保存 OAuth source 选择, Cursor 不保存 token.
- `settings.json` 位于 `CodexTray.exe` 同级目录.

## 目录结构

- `CodexTray.Core`: 官方额度采集, API 余额与用量采集, Token Cost 统计, 缓存, HTTP 服务, 设置存储, 监控器定位与插件安装, Windows 自启动.
- `CodexTray.App`: WPF 托盘应用, Home/APIs/Settings 弹窗, ViewModel, 命令和自定义数值输入控件.
- `CodexTray.Tests`: 自包含 C# 测试运行器.
- `Plugins/LiteMonitor`: LiteMonitor JSON 模板 `CodexTray.json`.
- `Plugins/TrafficMonitor`: TrafficMonitor 原生插件源码与 `CodexTray.ini` 模板. 原生构建输出位于 `Plugins/TrafficMonitor/Builds/**`.
- `Resources`: 应用图标与 `model-pricing.json`.
- `Docs`: README 展示资源.
- `Scripts`: App 发布, 重启预览, release 打包和 TrafficMonitor 插件构建脚本.
- `Builds/Output/win-x64`: 本地发布与重启预览输出.
- `Builds/Release/vX.Y.Z`: 正式版本目录与 zip.
- `Directory.Build.props` 和 `Directory.Build.targets`: MSBuild 默认配置与 `Builds/**` 编译项排除.

## 关键约束

- namespace 必须与项目目录对应: `CodexTray.Core`, `CodexTray.App`, `CodexTray.Tests`.
- WPF UI 入口为 `App.cs`, `TrayController.cs`, `TrayPopupWindow.xaml`, `TrayPopupWindow.xaml.cs` 和 `TrayPopupViewModel.cs`. 托盘层使用 `System.Windows.Forms.NotifyIcon`.
- 数值设置使用现有 `NumericUpDown` 和 `NumericInput`.
- 监控器磁盘搜索复用 `MonitorLocator`, `LiteMonitorLocator` 和 `TrafficMonitorLocator`.
- LiteMonitor 模板文件名保持为 `Plugins/LiteMonitor/CodexTray.json`. TrafficMonitor 模板文件名保持为 `Plugins/TrafficMonitor/CodexTray.ini`.
- 修改插件字段, HTTP 路径或显示格式时, 同步检查两个插件模板, `TrafficMonitorPlugin.cpp`, `CodexTray.Tests/Program.cs` 和 README 的相关说明.
- 修改 Token Cost 解析或定价结构时, 同步检查 `Resources/model-pricing.json` 和对应测试.
- 修改 API provider, 请求字段或凭据来源时, 同步检查 `ApiUsageCollector`, `GrokUsageCollector`, `CursorUsageCollector`, `ApiMonitorSettings`, `ApiMonitorViewModel`, `TrayPopupWindow.xaml`, 对应测试和 README 的用户说明.
- 修改 WPF 布局或主题时, 检查是否需要更新 `Docs/showcase.png`.
- 本地服务必须保持仅监听 `127.0.0.1`. API 监控结果不得进入插件 HTTP 响应. 不在日志, HTTP 响应, 文档示例或插件配置中暴露 OAuth token 或 API key.
- `Scripts/Publish-App.ps1`, `Scripts/Restart-App.ps1` 和 `Scripts/Package-Release.ps1` 共享 `Scripts/Publish-Shared.ps1`. 发布参数, 清理逻辑或进程重启逻辑优先修改共享脚本. `Scripts/Restart-App.ps1` 只重启当前发布输出中的程序, 不执行发布.

## 构建与输出

- `bin` 和 `obj` 使用项目默认位置.
- 不提交 `bin`, `obj`, `Builds` 或 `Plugins/TrafficMonitor/Builds` 下的生成文件.
- App 发布为 `net9.0-windows`, `win-x64`, 单文件, framework-dependent 应用.
- `Scripts/Publish-App.ps1` 清理已有发布输出时必须保留 `settings.json`.
- `Resources` 和插件模板作为外部文件复制到发布目录.
- 只有 `Plugins/TrafficMonitor/Builds/x64/Release/CodexTray.dll` 已存在时, App 发布才会复制 TrafficMonitor DLL.
- `Directory.Build.targets` 排除 `Builds/**` 下的 `.cs`, 防止发布产物被 SDK 默认编译项重新纳入编译.

## 验证工作流

- 每次涉及需要重新编译的代码或 XAML 改动完成后, 必须在最终验证步骤自动执行 `Scripts/Publish-App.ps1 -NoPause`, 并确认发布成功且发布目录中的程序已启动.

构建全部项目:

```powershell
dotnet build .\CodexTray.sln -m:1
```

运行测试:

```powershell
dotnet run --project .\CodexTray.Tests\CodexTray.Tests.csproj
```

构建 TrafficMonitor 原生插件:

```powershell
.\Scripts\Build-TrafficMonitorPlugin.ps1
```

发布 App 并重启已发布程序:

```powershell
.\Scripts\Publish-App.ps1 -NoPause
```

不重新发布, 只重启当前发布输出中的预览程序:

```powershell
.\Scripts\Restart-App.ps1 -NoPause
```

对应 `.cmd` 入口供资源管理器双击使用, 默认在结束前停留窗口.

## 发布流程

当用户要求发布新版本并提供版本号时, 完成以下流程:

1. 读取 Git 规则模块, 再检查 `git status --short --branch`, 确认当前分支为 `develop`, 并区分本次发布修改与已有修改.
2. 提交并推送本次发布涉及的源码, 脚本, 文档和资源修改. 不提交任何生成产物.
3. 将 `CodexTray.App/CodexTray.App.csproj` 中的 `<Version>` 更新为规范化后的不带 `v` 版本号 `X.Y.Z`, 使用 `upgrade: X.Y.Z` 单独提交并推送. 此提交不得混入其他修改.
4. 再次确认工作区干净, `develop` 与 `origin/develop` 同步, 本地可以安全切换分支. 同时确认目标 tag 和 GitHub Release 尚不存在.
5. 切换到 `main`, 确认工作区干净且 `main` 与 `origin/main` 同步. 使用 `git merge --no-ff develop -m "feat: 合并 develop 以发布 vX.Y.Z"` 合并 `develop`, 必须保留明确的 merge commit. 如果发生冲突, 停止并报告状态.
6. 推送 `main`, 并确认远端 `main` 已指向 merge commit.
7. 执行 `dotnet build .\CodexTray.sln -m:1` 和 `dotnet run --project .\CodexTray.Tests\CodexTray.Tests.csproj`.
8. 执行 `.\Scripts\Build-TrafficMonitorPlugin.ps1`, 确认 release 包需要的原生 DLL 已生成.
9. 执行 `.\Scripts\Package-Release.ps1 -Version X.Y.Z -NoPause`, 传入的版本必须与 `CodexTray.App/CodexTray.App.csproj` 中的 `<Version>` 一致.
10. 确认 `Builds/Release/vX.Y.Z/CodexTray-vX.Y.Z-win-x64.zip` 存在.
11. 在已推送的 `main` merge commit 上创建 annotated tag `vX.Y.Z`, 再推送 tag.
12. 获取上一个版本 tag, 检查从该 tag 到 `vX.Y.Z` 之间的 commit 和实际变更. 由 AI 合并同类改动, 去除仅用于发布, 格式化或内部维护且不影响用户的噪声, 编写准确, 面向用户的 Markdown Release Notes. 不直接复制 commit 列表, 不使用 `--generate-notes`, 不写入未在 diff 中确认的内容. 将结果保存到 `Builds/Release/vX.Y.Z/release-notes.md`.
13. 使用以下命令创建 GitHub Release 并上传 zip:

```powershell
gh release create vX.Y.Z `
  "Builds\Release\vX.Y.Z\CodexTray-vX.Y.Z-win-x64.zip" `
  --title "CodexTray vX.Y.Z" `
  --notes-file "Builds\Release\vX.Y.Z\release-notes.md" `
  --verify-tag
```

14. GitHub Release 创建并核验成功后, 使用 `git switch develop` 切回 `develop`, 再确认工作区干净且 `develop` 与 `origin/develop` 同步.

版本输入支持 `X.Y.Z`, `vX.Y.Z` 和 SemVer 后缀. tag 与版本目录固定使用规范化后的 `v<version>`.

如果 tag 或 release 已存在, 停止并报告状态, 不覆盖 tag. 只有用户明确同意时才能使用 `gh release upload --clobber`. 如果 push 被 non-fast-forward 拒绝, 停止并让用户决定 rebase 或 merge.
