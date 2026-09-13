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

`README.md` 面向使用者, 本文档面向修改和维护代码的 Agent 与开发者. 两份文档按以下范围维护:

| 文档 | 应包含的内容 |
| --- | --- |
| `README.md` | 功能介绍, 安装与登录要求, 页面操作, 费用含义, API 卡片配置, 插件使用, 数据与隐私, 常见问题. 技术内容仅保留用户完成配置或排错所需的部分. |
| `AGENTS.md` | 项目结构, 代码入口, 数据来源与接口, 凭据读写, 统计规则, UI 约束, 修改时的关联检查, 构建验证与发布流程. |

功能或行为变化时, 更新 README 中受影响的使用说明, 并在本文档维护对应的技术约束. 接口字段, 解析算法, 重试次数, 数据库键和布局尺寸归入本文档, 不在 README 重复展开. 两份文档均描述当前行为, 不写成某次改动的过程记录; 实现细节以代码为准.

## 项目概览

`CodexTray` 是一个 C#/.NET 10 Windows x64 托盘应用. 桌面 UI 与应用内对话框使用 WPF. `System.Windows.Forms` 用于 `NotifyIcon`, 托盘菜单, 屏幕定位, 文件夹选择和应用初始化.

`CodexTray.Core` 负责采集, 设置, 缓存, 本地 HTTP 服务和插件安装. `CodexTray.App` 负责 WPF 界面与托盘编排. Codex, Cursor, Grok 和 API 监控分别采集, 其中 Codex Weekly, Cursor Monthly 和 Grok Weekly 进入插件响应; Token Cost 和 API 监控结果仅显示在主面板.

## 架构与数据流

### 应用生命周期

1. `CodexTray.App/Program.cs` 使用 mutex 保证单实例. 后续进程通过 `TrayShowPanel` event 通知已有实例打开面板后退出.
2. `CodexTray.App/App.cs` 创建 WPF application host, `TrayController` 管理托盘, 设置, 定时刷新, 插件安装和本地服务.
3. 首次启动由 `SettingsStore` 写入默认 `settings.json` 并打开主面板. 后续设置加载时会补齐缺失字段并规范化值.
4. `TrayPopupWindow` 与 `TrayPopupViewModel` 提供 Codex/Cursor/Grok/APIs/Settings/About 页面. `ApiMonitorViewModel` 管理单张 API 卡片的编辑与显示状态. 左键切换弹窗, 右键菜单仅包含 `Open Panel`, `Refresh Now` 和 `Exit`.
5. `AppSettings.VisiblePages` 控制 Codex, Cursor, Grok 与 APIs 页的可见性和后台采集. 同时隐藏 Codex, Cursor 和 Grok 时停止本地 HTTP 服务, 全部隐藏时停止定时刷新.
6. `TrayController` 统一持有应用生命周期 cancellation token, 跟踪刷新, 插件定位, 单实例信号和本地服务切换任务. 正常退出时先取消并等待后台任务, 再异步停止本地服务和关闭 WPF application.

### 额度与插件链路

1. `CodexUsageCollector` 读取 `~/.codex/auth.json` 中的 OAuth 凭据, 从 ChatGPT 官方 usage 与 rate-limit-reset-credits 接口采集 Session, Weekly 和 Reset Credits. 凭据缺失或无效时返回不可用状态. `CursorUsageCollector` 将 dashboard 中的 Monthly 额度转换为插件数据. `GrokUsageCollector` 将 dashboard 中的 Weekly 额度转换为插件数据.
2. `TrayController` 将 Codex, Cursor 与 Grok 的最新结果分别写入 `UsageCache`, 由缓存合并为插件响应. 隐藏任一页面时会清除对应缓存数据.
3. `LightweightHttpServer` 默认监听 `127.0.0.1:17890`, 暴露以下接口:
   - `/codex-tray`: LiteMonitor 使用的 JSON 响应.
   - `/codex-tray.txt`: TrafficMonitor 使用的三行文本, 依次为 Codex Weekly, Cursor Monthly 和 Grok Weekly.
   - `/health`: 返回本地服务健康状态.
4. `LiteMonitorPluginInstaller` 和 `TrafficMonitorPluginInstaller` 从发布目录读取模板, 写入当前端口后安装到监控器目录.

### API 监控链路

1. `AppSettings.ApiMonitors` 保存 API 监控卡片的顺序和 provider 配置. 支持的 provider 及新建卡片下拉顺序为 DeepSeek, OpenRouter, Vercel, NanoGPT 与 NewAPI, 由 `ApiMonitorViewModel.ProviderOptions` 固定. `TrayPopupViewModel` 负责增删, 排序和持久化卡片. `AppSettings.Normalize` 会移除旧版 Cursor 卡片.
2. `ApiUsageCollector` 并行刷新所有卡片. DeepSeek 使用 `/user/balance`, OpenRouter 使用 `/api/v1/credits`, Vercel 使用 `/v1/credits`, NanoGPT 使用 `/api/check-balance` 和 `/api/v1/usage`, NewAPI 使用 `/api/user/self` 并发送 `New-Api-User` header.
3. 请求发送到各卡片配置的 Base URL. OpenRouter 使用 Management Key, 不使用普通 API key 的 `/key` limit 作为账户余额. Vercel 使用 AI Gateway API key, 不使用普通 Vercel 账号 token. NewAPI 还需要 User ID. NanoGPT 用量按最近 30 个 UTC 日查询, 用量失败不影响余额显示.
4. `TrayController` 将结果交给 `TrayPopupViewModel` 更新单卡片状态与 APIs 页汇总状态. 这些结果不写入 `UsageCache`.

### Cursor 页面链路

1. `CursorUsageCollector` 从 `%APPDATA%\Cursor\User\globalStorage\state.vscdb` 读取 `cursorAuth/accessToken` 与 `cursorAuth/refreshToken`. 临近过期或收到 401/403 时, 使用 refresh token 调用 `api2.cursor.sh/oauth/token` 刷新并写回 vscdb.
2. 同一轮 dashboard 依次请求 `cursor.com/api/usage-summary`, `api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus` 和分页的 `cursor.com/api/dashboard/get-filtered-usage-events`. 三个区域可以独立显示成功或失败状态, 并共享一次凭据读取和最多一次强制 OAuth refresh.
3. usage-summary 提供计划类型, Monthly, First party, APIs 用量与账期结束时间. `GetSandUsageStatus` 提供 Grok Bot Weekly 额度与重置时间. usage-events 使用返回的 token 字段和实际 `totalCents` 统计 Token Cost, 不用本地模型价格表重算.
4. `TrayController` 将完整 `CursorUsageDashboard` 交给 `TrayPopupViewModel`, 并将 Monthly 插件数据写入 `UsageCache`. Grok Bot Weekly, First party, APIs 和 Token Cost 不进入插件 HTTP 响应. OAuth token 不会复制到 `settings.json`.

### Grok 页面链路

1. `GrokUsageCollector` 从 Grok Build 的 `~/.grok/auth.json` 读取 xAI OAuth access token, 不使用 OpenCode 凭据. 临近过期或收到 401/403 时, 使用 refresh token 调用 `auth.x.ai` 刷新并写回原文件. OAuth token 不会复制到 `settings.json`.
2. Grok billing 接口提供 Weekly 剩余额度, 重置时间和 `productUsage` 产品占比, 采集器支持解析 gRPC-web 响应.
3. 订阅类型优先取 Grok Build 本地 billing 日志中的最近记录, 没有有效记录时仅检查当前 access token 对应的 auth 条目. 不从未知 protobuf 字段猜测套餐. 常见类型包括 `Free`, `SuperGrok`, `SuperGrok Heavy` 和 `X Premium` 系列.
4. `TrayController` 将 dashboard 交给 `TrayPopupViewModel`, 将 Weekly 插件数据写入 `UsageCache`. 本地 Token Cost 由 `TokenCostCollector.CollectGrok` 独立统计.

### 本地 Token Cost

- `TokenCostCollector.CollectCodex` 读取 `~/.codex/sessions/**/*.jsonl` 和 `~/.codex/archived_sessions/*.jsonl`, 使用 `Resources/model-pricing.json` 计算 API 等价成本与缓存命中率. 不读取 OpenCode.
- `CollectGrok` 读取 `~/.grok/sessions/**/updates.jsonl` 和 `~/.grok/archived_sessions/**/updates.jsonl`, 汇总 `turn_completed` 事件中的用量. 每轮 token 按 `inputTokens + outputTokens` 统计, `reasoningTokens` 已包含在输出中, 不重复相加.
- Grok 费用优先采用完整的 `costUsdTicks`, 保留其中已计入的工具调用等费用. 自报费用缺失或 `costIsPartial` 为真时, 使用本地模型的输入, 缓存输入和输出价格回算. 只有费用而没有 token 的记录仍保留自报费用; 本地价格不可用时保留已有自报费用. 两者都不可用时, 仍统计 token, 成本按 `$0.00` 计入.
- Grok 费用规则参考 [CCSwitch 的 Grok Build 会话导入](https://github.com/farion1231/cc-switch/blob/c0050623194303ecc95c3ce7ca8e362bce21e762/src-tauri/src/services/session_usage_grokbuild.rs). 修改解析或计费时, 以当前代码和测试确认边界行为.

### 设置边界

- 默认值, 端口范围, HTTP 路径, 文件名和发布资源目录统一维护在 `CodexTrayDefaults` (`CodexTray.Core/CodexTrayDefaults.cs`).
- 刷新间隔范围为 1 到 1440 分钟, 默认 1 分钟.
- 主题支持 `System`, `Light`, `Dark`. Windows 11 默认启用 Mica, Windows 10 固定使用纯色背景.
- 主面板尺寸固定为 360 x 620.
- Codex, Cursor, Grok 与 APIs 页面默认全部可见. 无可见数据页时不运行定时刷新.
- Codex, Cursor 和 Grok Token Cost 共用时段列表, 模型占比圆环和趋势图. 滚动周期为 `24H`, `7D`, `30D`, `Lifetime`, 自然周期为 `Today`, `Week`, `Month`, `Lifetime`. `24H` 从当前时刻精确向前滚动 24 小时. 圆环和趋势图随周期同步切换, 趋势图可切换最近 30 天与当前自然月; 自然月按整月固定宽度展示, 未来日期留白.
- 圆环中心显示当前时段 token 总量, 成本与缓存命中率同行显示 (`$N · N%`). Token 数量使用 K, M, B.
- Codex 额度大卡片通过左下角页点或鼠标滚轮切换 Session 与 Weekly. Session 仅在有效时显示, 只有一个有效页面时隐藏页点. Resets 使用全宽小卡片.
- Cursor 额度大卡片通过页点或鼠标滚轮切换 Monthly 与 Grok Bot Weekly, 两个页点尺寸相同, 选中项使用现有绿色. First party 与 APIs 以半宽卡片并排显示.
- Grok 上方使用 Weekly 大卡片和产品占比小卡片. 产品占比卡片分两行显示分段进度条和三个图例, 进度条与 Weekly 同粗. 全为零时显示 `Build`, `Chat`, `Others`; 仅一个非零产品时显示该产品, `Build`, `Others`, 若该产品本身为 `Build` 则第二项用 `Chat`; 两个及以上非零产品时保留最高两项, 其余合并到 `Others`. `Others` tooltip 仅列出其中有用量的产品, 为零时显示 `Others 0%`. 用量不可用时显示 `N/A`.
- API provider 的 API key, Management Key, access token 和 User ID 以明文保存在 `settings.json`.
- `settings.json` 位于 `CodexTray.exe` 同级目录.

### 演进边界

- 当前交付物仍是后台托盘应用. 未来桌面客户端预计与托盘共同存在, 新客户端应复用 `CodexTray.Core` 中不依赖 UI 的采集, 设置, 缓存, HTTP 服务和插件能力.
- `CodexTray.App` 保持 WPF 与托盘编排职责. 不在真实桌面客户端入口和进程模型确定前预先引入通用 Host, DI container, 单实现 interface 或跨进程抽象.
- 可共享的长时任务必须支持 `CancellationToken`. 服务拥有者必须等待任务退出, 并在释放 `LightweightHttpServer` 前调用和等待 `StopAsync()`.

## 目录结构

- `CodexTray.Core`: Codex 官方额度采集, Cursor 额度与账单采集, API 余额与用量采集, Token Cost 统计, 缓存, HTTP 服务, 设置存储, 监控器定位与插件安装, Windows 自启动.
- `CodexTray.App`: WPF 托盘应用, Codex/Cursor/Grok/APIs/Settings/About 页面, 基于 `CommunityToolkit.Mvvm` 的 ViewModel 与命令, 以及自定义数值输入控件.
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
- ViewModel 复用 `CommunityToolkit.Mvvm` 的 `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` 和 `AsyncRelayCommand`. 不恢复项目自有的 `ObservableObject` 或 `RelayCommand` 实现.
- 数值设置使用现有 `NumericUpDown` 和 `NumericInput`.
- 监控器磁盘搜索复用 `MonitorLocator`, `LiteMonitorLocator` 和 `TrafficMonitorLocator`.
- LiteMonitor 模板文件名保持为 `Plugins/LiteMonitor/CodexTray.json`. TrafficMonitor 模板文件名保持为 `Plugins/TrafficMonitor/CodexTray.ini`.
- 应用版本与插件版本必须保持一致: `CodexTray.App/CodexTray.App.csproj` 中的 `<Version>`, `Plugins/LiteMonitor/CodexTray.json` 中的 `meta.version`, 以及 `Plugins/TrafficMonitor/TrafficMonitorPlugin.cpp` 中 `TMI_VERSION` 的返回值必须使用同一规范化版本号 `X.Y.Z`.
- 修改插件字段, HTTP 路径或显示格式时, 同步检查两个插件模板, `TrafficMonitorPlugin.cpp`, `CodexTray.Tests/Program.cs` 和 README 的相关说明.
- 修改 Token Cost 解析, 周期或定价结构时, 同步检查 `TokenCostCollector`, `TokenCostPeriodAccumulator`, `Resources/model-pricing.json` 和对应测试.
- 修改 API provider, 请求字段或凭据来源时, 同步检查 `ApiUsageCollector`, `GrokUsageCollector`, `ApiMonitorSettings`, `ApiMonitorViewModel`, `TrayPopupWindow.xaml`, 对应测试和 README 的用户说明.
- 修改 Cursor quota, usage-events, OAuth, 数据库读写或插件 Monthly 映射时, 同步检查 `CursorUsageCollector`, `UsageCache`, `TrayController`, `TrayPopupViewModel`, `TrayPopupWindow.xaml`, `Microsoft.Data.Sqlite` 依赖, 对应测试和 README 的用户说明.
- 修改 `VisiblePages` 或刷新调度时, 同步检查页面导航, 定时器, 插件本地 HTTP 服务启停, 缓存清理和各采集器调用条件.
- 修改 WPF 布局或主题时, 检查是否需要更新 `Docs/showcase.png`.
- 本地服务必须保持仅监听 `127.0.0.1`. API 监控结果不得进入插件 HTTP 响应. 不在日志, HTTP 响应, 文档示例或插件配置中暴露 OAuth token 或 API key.
- `Scripts/Publish-App.ps1`, `Scripts/Restart-App.ps1` 和 `Scripts/Package-Release.ps1` 共享 `Scripts/Publish-Shared.ps1`. 发布参数, 清理逻辑或进程重启逻辑优先修改共享脚本. `Scripts/Restart-App.ps1` 只重启当前发布输出中的程序, 不执行发布.

## 构建与输出

- `bin` 和 `obj` 使用项目默认位置.
- 不提交 `bin`, `obj`, `Builds` 或 `Plugins/TrafficMonitor/Builds` 下的生成文件.
- App 发布为 `net10.0-windows`, `win-x64`, 单文件, framework-dependent 应用. `CodexTray.Core` 目标框架为 `net10.0`.
- `Scripts/Publish-App.ps1` 清理已有发布输出时必须保留 `settings.json`.
- `Resources` 和插件模板作为外部文件复制到发布目录.
- 只有 `Plugins/TrafficMonitor/Builds/x64/Release/CodexTray.dll` 已存在时, App 发布才会复制 TrafficMonitor DLL.
- `Directory.Build.targets` 排除 `Builds/**` 下的 `.cs`, 防止发布产物被 SDK 默认编译项重新纳入编译.

## 验证工作流

- 每次涉及需要重新编译的代码或 XAML 改动完成后, 必须在最终验证步骤自动执行 `Scripts/Publish-App.ps1 -NoPause`, 并确认发布成功且发布目录中的程序已启动.
- 在 Codex Windows 环境执行可能触发 NuGet restore 的 build, test, publish 或打包命令时, 必须直接申请沙箱外执行权限. Windows Codex sandbox 可能导致 Schannel 返回 `SEC_E_NO_CREDENTIALS` 并产生 `NU1900`. 不得通过关闭 NuGet Audit 或屏蔽 `NU1900` 规避.

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
3. 将 `CodexTray.App/CodexTray.App.csproj` 中的 `<Version>`, `Plugins/LiteMonitor/CodexTray.json` 中的 `meta.version`, 以及 `Plugins/TrafficMonitor/TrafficMonitorPlugin.cpp` 中 `TMI_VERSION` 的返回值同时更新为规范化后的不带 `v` 版本号 `X.Y.Z`, 并确认三处完全一致. 使用 `upgrade: X.Y.Z` 单独提交并推送. 此提交不得混入其他修改.
4. 再次确认工作区干净, `develop` 与 `origin/develop` 同步, 本地可以安全切换分支. 同时确认目标 tag 和 GitHub Release 尚不存在.
5. 切换到 `main`, 确认工作区干净且 `main` 与 `origin/main` 同步. 使用 `git merge --no-ff develop -m "feat: 合并 develop 以发布 vX.Y.Z"` 合并 `develop`, 必须保留明确的 merge commit. 如果发生冲突, 停止并报告状态.
6. 推送 `main`, 并确认远端 `main` 已指向 merge commit.
7. 执行 `dotnet build .\CodexTray.sln -m:1` 和 `dotnet run --project .\CodexTray.Tests\CodexTray.Tests.csproj`.
8. 执行 `.\Scripts\Build-TrafficMonitorPlugin.ps1`, 确认 release 包需要的原生 DLL 已生成.
9. 执行 `.\Scripts\Package-Release.ps1 -Version X.Y.Z -NoPause`, 传入的版本必须与 App, LiteMonitor 和 TrafficMonitor 的三处版本声明一致.
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
