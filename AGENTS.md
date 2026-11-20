# CodexTray Agent Guide

## 目录

- [文档职责与维护规范](#文档职责与维护规范)
- [项目架构与演进边界](#项目架构与演进边界)
- [核心技术决策与红线](#核心技术决策与红线)
- [外部参考](#外部参考)
- [修改关联检查清单](#修改关联检查清单)
- [构建与验证](#构建与验证)
- [发布流程](#发布流程)

## 文档职责与维护规范

仓库根目录维护两份定位互补的核心文档, 各自职责与边界如下:

| 文档 | 目标读者 | 应包含内容 | 不应包含内容 |
| --- | --- | --- | --- |
| `README.md` | 最终用户 | 项目定位, 核心功能, 安装与前置登录要求, 界面与插件操作, 费用口径, API 卡片配置, 数据与隐私说明, 高频常见问题 | 内部接口路径, JSON 字段, 重试参数, 布局像素尺寸, 动画参数, 内部模型 ID 映射与代码结构 |
| `AGENTS.md` | Agent 与维护者 | 文档分工规范, 架构与演进边界, 长期有效的技术决策与避坑原因 ("为什么"与"不能做什么"), 安全/隐私红线, 改动关联检查清单, 外部参考链接, 构建/验证/发布工作流 | 通过阅读代码即可直接获取的常量默认值, JSON 字段名, URL 路径, 超时/重试数值, 具体模型单价/别名清单, UI 尺寸/色值/动画参数, 逐文件清单 |

维护两份文档时必须遵守以下原则:

1. **以代码为单一事实源**: 不把易变的实现细节抄写进文档. 当实现细节可以从源码直接读取时, 文档只保留背后的设计判断, 约束边界或指向权威源码位置 (如 `CodexTrayDefaults`, `Resources/model-pricing.json`).
2. **只描述当前态**: 两份文档均描述项目当前行为与规范, 禁止按时间把单次功能改动或 Bug 修复记录堆砌成流水账.
3. **同步更新与清理漂移**: 用户可见的功能, 配置项或行为变化时更新 `README.md`; 架构边界, 不可破坏的不变量, 避坑决策或工程流程变化时更新 `AGENTS.md`. 修改相关区域若发现文档过时或冗余, 顺手清理.

## 项目架构与演进边界

### 模块划分

- `CodexTray.Core` (`net10.0`): 平台无关/无 UI 的核心能力, 包含各来源额度与 Token Cost 采集, 缓存, 设置存储, 本地 HTTP 插件服务, 监控器定位与插件安装, 应用内更新与 Windows 自启动.
- `CodexTray.App` (`net10.0-windows`): WPF + `WPF-UI` 桌面界面与 `System.Windows.Forms.NotifyIcon` 托盘编排, 使用 `CommunityToolkit.Mvvm`. 入口为 `Program.cs` (单实例互斥锁与 `--apply-update` 分支), `App.cs`, `TrayController.cs`, `TrayPopupWindow` 与 `TrayPopupViewModel`.
- `CodexTray.Tests`: 自包含 C# 测试运行器.
- `Plugins/LiteMonitor` 与 `Plugins/TrafficMonitor`: LiteMonitor JSON 模板与 TrafficMonitor C++ 原生插件源码, 模板及独立原生测试.
- `Resources`: 应用图标与外部可加载的 `model-pricing.json`.
- `Scripts`: 本地发布, 重启预览, 原生插件构建/测试与正式发版打包脚本, 公共逻辑集中在 `Scripts/Publish-Shared.ps1`.
- `Builds`: 本地发布 (`Builds/Output/win-x64`) 与正式版本输出 (`Builds/Release/vX.Y.Z`), 均不纳入版本控制.

### 演进与工程边界

- **为未来桌面客户端留口但不过度设计**: 当前交付物是托盘应用, 未来可能与独立桌面客户端共存. `CodexTray.Core` 必须保持不依赖 UI 以便复用; 但在真实桌面客户端入口和进程模型确定前, **不得**预先引入通用 Host, DI 容器, 单实现 interface 或跨进程抽象.
- **既有基础设施约定**:
  - Namespace 与项目目录保持一致 (`CodexTray.Core`, `CodexTray.App`, `CodexTray.Tests`).
  - ViewModel 统一复用 `CommunityToolkit.Mvvm` (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`, `AsyncRelayCommand`), 不手写自有 MVVM 基类.
  - 共享默认值, 端口范围, HTTP 路径, 文件名与发布校验路径集中在 `CodexTray.Core/CodexTrayDefaults.cs`.
  - 数值输入与凭据遮挡复用既有 `NumericUpDown` / `NumericInput` / `CredentialInput`; 监控器扫描复用 `MonitorLocator` 体系.
- **生命周期与并发不变量**:
  - 所有可共享长时任务必须支持 `CancellationToken`. 正常退出和本地服务异常重启时, 必须先取消并排空后台任务或活动连接, 再停止/释放 `LightweightHttpServer`.
  - 各采集源以及 Codex/Grok/Gemini 的额度与本地 Token Cost 必须独立发布, 单源失败不得阻塞其他源; 每次发布前必须复核退出状态, 设置引用与页面可见性.
  - `VisiblePages` 为空时必须停止定时刷新与本地 HTTP 服务; 隐藏对应页面或恢复备份时必须清理对应插件缓存与退避状态.
  - `RefreshRequests` 负责合并待刷新来源与单卡请求并串行排空, 禁止同一采集器并发执行; 迟到结果若对应卡片已修改或删除必须丢弃.

## 核心技术决策与红线

### 安全与隐私

- **严禁硬编码真实密钥**: 绝不得在代码, 配置文件, 测试用例或文档示例中硬编码任何真实密钥, API key, OAuth `client_secret`, token 或账号凭据; 测试与示例一律使用无意义的占位符.
- **本地插件服务隔离**: `LightweightHttpServer` 必须仅监听 `127.0.0.1`, 且强制校验请求 `Host` 与当前监听端口匹配 (仅限 `127.0.0.1` 或 `localhost`), 严禁向局域网开放.
- **插件响应白名单**: 仅 Codex Weekly, Grok Weekly 与 APIs 页**第一张** DeepSeek 卡片的四舍五入整元 CNY 余额可进入插件 HTTP 响应. 第一张 DeepSeek 卡片不可用时返回 `N/A`, **不得**回退到后续 DeepSeek 卡片; 其他页面及 API provider 不得进入插件响应.
- **凭据存储与脱敏**:
  - OAuth token 绝不复制到 `settings.json`, 日志, 诊断文本或插件配置.
  - API 监控凭据目前在 `settings.json`, `settings.last-good.json` 与 `settings.damaged-*.json` 中以明文存储; `CredentialInput` 仅用于屏幕遮挡且离开编辑态即重置, **不得**在文档或界面中将其表述为加密存储.
  - `UsageDiagnostics` 与 `OAuthTokenHelpers` 必须输出固定错误类别或白名单 OAuth 错误码, 严禁回显服务端 `reason phrase`, `error_description`, 原始响应体, URL, 账号或卡片名称.
- **API 监控网络约束**: 卡片保存与采集共用 Base URL 校验. 远程目标强制 HTTPS; HTTP 仅允许 `localhost` 与字面回环 IP 且必须绕过系统代理. 生产 HTTP 客户端禁用自动重定向与 Cookie. 卡片更换 provider 或目标源 (协议/主机/端口) 时必须清空已保存的密钥与 User ID, 同源改路径则保留.

### 凭据读写与重试边界

- **事务写回与并发换号保护**: Grok 与 Gemini 对本地 JSON 凭据文件的续期写回必须使用 `AtomicFile` 的 NTFS 事务写回, 并在网络请求前后校验文件内容指纹 (以及 Gemini 状态代次), 检测到原客户端换号, 登出或应用重置时必须丢弃旧结果, 不得覆盖新凭据. 遇到文件锁定或非 TxF 卷无法事务写回时, 不得强制覆写 (Grok 仅继续使用仍有效的 token, Gemini 回退到按来源绑定的私有内存缓存).
- **只读来源与静默原则**: Windows 凭据管理器, Antigravity IDE `state.vscdb` 与 Cursor `state.vscdb` 一律只读不写回. 后台刷新**永不**拉起 `agy` 等外部 CLI 子进程或弹出浏览器登录窗, 也**不**自动续期 Cursor 登录.
- **OAuth 客户端配对安全**: Gemini / Antigravity 的 OAuth `client_id` / `client_secret` 严禁硬编码进仓库; 仅从凭据文件, 环境变量或本机已安装应用运行时动态提取. 扫描本机路径前必须通过 `AppUpdatePaths.EnsureSafePath` 拒绝重解析点.
- **重试范围限制**: `UsageHttp` 的重试机制 (网络错误, 超时, 429, 5xx) 仅用于只读额度与用量查询, **绝不**用于 OAuth token 刷新 POST. 401/403, 其他 4xx 与解析错误不自动重试; 单次采集已预刷新后遇到 401/403 不再次触发续期. 遇到 `Retry-After` 超出等待预算时直接失败, 不缩短服务端要求的等待时长.

### 外部数据来源与统计口径

- **Grok 额度与订阅**:
  - 额度与产品占比固定走 `cli-chat-proxy.grok.com` (`/v1/billing?format=credits`), **不要**改走 `grok.com/rest/grok/credits` (非浏览器会触发 Cloudflare 挑战), 失败时也不回退其他额度端点.
  - 订阅徽章优先取 `/v1/settings` 的显示名, 不足时才依次回退本机日志/auth 与 billing 字段; **不要**请求 `/v1/user?include=subscription`, 也**不要**用 Cursor 接口 (`GetSandUsageStatus` / `usage-summary`) 替代 Grok 订阅额度与徽章. 旧订阅档位统一映射为 The X Subscription 名称 (需完整匹配避免 `SuperGrok Plus` 误落 `SuperGrok`).
- **Cursor 与 Grok API 云端账本**:
  - 只使用新账本接口 (`query-billing-items`), **不要**查询或拼接旧版 `GetFilteredUsageEvents`.
  - 只纳入 `cursor` 与 `grok-api`, 排除 Bot, Build, Chat 与 Imagine; 账本实付金额 (`pricing.contracted`) 不用于 Token Cost, 统一按本地价格表估算.
  - 对账本中未识别的 token meter **不得**直接累加 (防止把搜索次数或重叠细项当成新增 token); 无法确认分类或价格时保留总 token 并向上传播 `HasUnpricedUsage`.
  - 本地压缩缓存 (`cursor-usage-cache.json.gz`) 按账号校验, 仅在完整分页后落盘; 首次遇到早于已缓存月份的不可读旧月即持久化历史下限, 避免每次重启反复回溯无效月份.
- **Gemini, Antigravity 与本地日志**:
  - Codex, Grok 与 Gemini 均不读取 OpenCode 会话.
  - Gemini CLI 用量严格遵循官方定义: `input` 已含缓存, `tool` 另计普通输入, `output + thoughts` 为输出, **不要**根据 `total` 猜测反推. 恢复旧 `.json` 与同名 `.jsonl` 共存时只计 `.jsonl`.
  - Antigravity 日志缺少模型 ID 时, 仅只读同一数据根下的会话 SQLite 库做明确无冲突补读, **不**向库内写入也不凭空新增用量事件; 无法确认时保留 `Unknown` 且不估价.
  - 服务端标记 `disabled: true` 的额度桶不展示.
- **定价表与统计边界**:
  - `Resources/model-pricing.json` 按官方 Standard 文本非优惠原价的基础上下文档位维护, 不应用限时促销折扣, 不拆长上下文阶梯加价, 历史事件按当前价格表重算.
  - Claude 仅维护 4.6 及之后版本, Gemini 仅维护 3.1 Pro 及之后版本; 旧版本不映射到新模型价格, 保留历史 token 并标记未定价.
  - 本地记录中的 `codex-auto-review` 排除出统计, 占比, Session Logs 与价格表; 跳过 Codex 事件时仍须推进累计水位, 避免后续增量吞入该段用量.
  - Codex 分叉会话在父会话尚未写到分叉截止点前整段跳过, 避免把回放前缀重复计费.
  - OpenAI API 卡片翻页超过上限仍未读完时视为失败, 不把部分合计展示为完整当月用量; `MonthlyStartingCredit` 仅属 OpenAI, 非 OpenAI 卡片规范化时丢弃.
- **设置损坏保护**: `SettingsStore.LoadError` 非空时禁止一切持久化与自动插件路径写入, 保留原文件不覆盖; 恢复备份前须校验备份有效并先另存当前损坏文件.

### WPF UI 与渲染陷阱

- **主题与 Mica 合成**: 仅使用 `WPF-UI` 的 `ThemesDictionary` / `ControlsDictionary`, **禁止**启用 WPF 内置 `Window.ThemeMode` 或加载 `PresentationFramework.Fluent` (两套主题系统互相覆盖). Windows 11 启用 Mica 时必须同时设 `WindowChrome.GlassFrameThickness = -1` 并调用 `WindowBackdrop.RemoveBackground`, 否则透明根容器会露出黑色合成底色.
- **对话框清晰度与生命周期**: `ContentDialog` 加载时须移除库模板外层 Border 的 `DropShadowEffect`, 避免离屏栅格化导致文字发虚; 按钮点击时先释放 host 再执行命令, 防止旧对话框异步收尾误关新请求.
- **图表提示与动画清理**: 图表禁用系统自动 `ToolTipService`, 共用窗口级非命中 `ChartToolTip`, 规避系统 tooltip 安全区导致相邻扇区切换延迟. 所有页面/翻页/图表动画必须遵从 `SystemParameters.ClientAreaAnimation`, 隐藏, 卸载, 重入或关闭时必须移除动画时钟并清理 owner 状态.
- **卡片编辑隔离**: 单张 API 卡片的保存, 排序与删除不得持久化其他卡片未保存的字段草稿或 pending 卡片.

### 应用内更新安全

- 仅接受 `vX.Y.Z` tag 与对应 `https://github.com` release asset, 强制校验 SHA-256 `digest`, 体积上限与 zip 解压路径穿越 (拒绝绝对路径, 盘符与 `..`), 且确认关键文件 (`CodexTray.exe`, `Resources`, 插件文件) 齐全.
- 备份, 覆盖, 还原与清理全程校验并拒绝符号链接与目录联接 (重解析点).
- 覆盖安装期间持有单实例互斥锁, 保留根目录 `settings.json` 及其他用户文件, 失败必须从备份完整还原后才重启旧版.
- 检测到安装目录根目录存在 `CodexTray.dll` 时视为开发编译 (不包括 `Plugins/TrafficMonitor/CodexTray.dll`), 拒绝检查与安装更新且不退出程序.

## 外部参考

以下上游源码与官方文档用于核对协议字段与价格表, 修改相关采集或定价前优先复核:

- Gemini CLI 会话字段与记录实现: [chatRecordingTypes.ts](https://github.com/google-gemini/gemini-cli/blob/main/packages/core/src/services/chatRecordingTypes.ts), [chatRecordingService.ts](https://github.com/google-gemini/gemini-cli/blob/main/packages/core/src/services/chatRecordingService.ts)
- Antigravity 凭据与 IDE protobuf 结构: [agy 脱敏文件示例 (#479)](https://github.com/google-antigravity/antigravity-cli/issues/479), [Antigravity-Manager protobuf.rs](https://github.com/lbjlaq/Antigravity-Manager/blob/main/src-tauri/src/utils/protobuf.rs)
- Grok 本地费用与 Codex 速度估算参考: [CCSwitch Grok Build 导入](https://github.com/farion1231/cc-switch/blob/c0050623194303ecc95c3ce7ca8e362bce21e762/src-tauri/src/services/session_usage_grokbuild.rs), [codex-tps analysis.go](https://github.com/adenta/codex-tps/blob/be549617f50409c8c0e15a8a6838520ec12d2bb1/analysis.go)
- 官方模型价格表: [Claude Pricing](https://platform.claude.com/docs/en/about-claude/pricing), [Gemini Developer API Pricing](https://aistudio.google.com/docs/pricing), [Google Cloud Gemini Pricing](https://cloud.google.com/gemini-enterprise-agent-platform/generative-ai/pricing), [Cursor Models & Pricing](https://cursor.com/docs/models-and-pricing)

## 修改关联检查清单

| 变更范围 | 必须同步检查 |
| --- | --- |
| 应用或插件版本号 | 三处完全一致 (`X.Y.Z`): `CodexTray.App/CodexTray.App.csproj` `<Version>`, `Plugins/LiteMonitor/CodexTray.json` `meta.version`, `Plugins/TrafficMonitor/TrafficMonitorPlugin.cpp` `TMI_VERSION` |
| 插件字段, HTTP 路径或显示格式 | `Plugins/LiteMonitor/CodexTray.json`, `Plugins/TrafficMonitor/CodexTray.ini`, `TrafficMonitorPlugin.cpp`, C# 与原生插件测试, `README.md` |
| API provider, Base URL 或凭据规则 | `ApiUsageCollector`, `ApiMonitorSettings`, `ApiMonitorViewModel`, `TrayPopupWindow.xaml`, 测试, `README.md` |
| Grok / Codex / Gemini 额度或徽章映射 | 对应 `*UsageCollector`, `TrayPopupViewModel`, 测试, `README.md` |
| Token Cost 解析, 周期或定价结构 | `TokenCostCollector`, `TokenCostPeriodAccumulator`, `Resources/model-pricing.json`, 测试, `README.md` |
| `VisiblePages` 或刷新调度 | 页面导航, 定时器, 本地 HTTP 服务启停, 缓存/退避清理, 托盘 tooltip |
| 应用内更新协议, 校验或保留文件 | `AppUpdate*`, `GitHubAppUpdateClient`, `Program.cs`, Settings 页更新逻辑, 测试, `README.md` |
| 发布清理或进程重启逻辑 | 优先修改 `Scripts/Publish-Shared.ps1`, 确认清理保留设置/备份/缓存且停进程校验 exe 完整路径, 运行 `Scripts/Test-PublishCleanup.ps1` |
| WPF 显著布局或视觉改动 | 评估是否需要同步更新 `Docs/showcase.png` |

## 构建与验证

### 产物与环境规则

- 严禁提交 `bin`, `obj`, `Builds` 或 `Plugins/TrafficMonitor/Builds` 下的生成文件.
- App 发布产物为 `net10.0-windows` / `win-x64` 单文件 framework-dependent 应用; `Resources` 与插件模板作为外部文件随包复制. 仅当 `Plugins/TrafficMonitor/Builds/x64/Release/CodexTray.dll` 已预先编译存在时, App 发布才会复制该原生 DLL.
- **改动后强制发布验证**: 每次涉及需要重新编译的代码或 XAML 改动完成后, 最终验证必须执行 `.\Scripts\Publish-App.ps1 -NoPause`, 并确认发布成功且 `Builds\Output\win-x64\CodexTray.exe` 已启动.
- **Windows 沙箱 NuGet 提示**: 在 Codex Windows 沙箱中执行可能触发 NuGet restore 的 build/test/publish/打包命令时, 直接申请沙箱外执行权限 (沙箱内 Schannel 可能返回 `SEC_E_NO_CREDENTIALS` 导致 `NU1900`), **不得**通过关闭 NuGet Audit 或屏蔽 `NU1900` 规避.

### 常用命令

构建全部 C# 项目:

```powershell
dotnet build .\CodexTray.sln -m:1
```

运行 C# 测试:

```powershell
dotnet run --project .\CodexTray.Tests\CodexTray.Tests.csproj
```

验证发布清理保留设置与缓存文件:

```powershell
.\Scripts\Test-PublishCleanup.ps1
```

构建 TrafficMonitor 原生插件 (需 VS C++ Build Tools):

```powershell
.\Scripts\Build-TrafficMonitorPlugin.ps1
```

运行 TrafficMonitor 原生插件回归测试:

```powershell
.\Scripts\Test-TrafficMonitorPlugin.ps1
```

本地发布并重启预览程序 (`Scripts/*.cmd` 供双击运行, 默认结束后停留):

```powershell
.\Scripts\Publish-App.ps1 -NoPause
```

仅重启已发布的预览程序 (不重新编译发布):

```powershell
.\Scripts\Restart-App.ps1 -NoPause
```

## 发布流程

当用户要求发布新版本并提供版本号时 (支持 `X.Y.Z`, `vX.Y.Z` 及 SemVer 后缀; tag 与发布目录统一使用 `v<version>`), 严格按以下顺序执行:

1. 先读取 Git 共享规则, 检查 `git status --short --branch`, 确认当前位于 `develop` 分支并厘清本次发布变更与既有修改.
2. 提交并推送本次发布涉及的源码, 脚本, 文档与资源修改, 不提交任何生成产物.
3. 同步更新三处版本号为规范化不带 `v` 的 `X.Y.Z`: `CodexTray.App/CodexTray.App.csproj` (`<Version>`), `Plugins/LiteMonitor/CodexTray.json` (`meta.version`), `Plugins/TrafficMonitor/TrafficMonitorPlugin.cpp` (`TMI_VERSION`). 使用 `upgrade: X.Y.Z` 单独提交并推送, 不得混入其他修改.
4. 确认工作区干净, `develop` 与 `origin/develop` 同步, 且目标 tag 与 GitHub Release 均不存在.
5. 切换到 `main`, 确认工作区干净且 `main` 与 `origin/main` 同步. 使用 `git merge --no-ff develop -m "feat: 合并 develop 以发布 vX.Y.Z"` 合并 (必须保留 merge commit). 遇到冲突立即停止并报告.
6. 推送 `main`, 确认远端 `main` 指向该 merge commit.
7. 运行 `dotnet build .\CodexTray.sln -m:1` 与 `dotnet run --project .\CodexTray.Tests\CodexTray.Tests.csproj`.
8. 运行 `.\Scripts\Build-TrafficMonitorPlugin.ps1`, 确认原生插件 DLL 已生成.
9. 运行 `.\Scripts\Package-Release.ps1 -Version X.Y.Z -NoPause`, 确认与三处版本声明一致.
10. 确认 `Builds/Release/vX.Y.Z/CodexTray-vX.Y.Z-win-x64.zip` 已生成.
11. 在已推送的 `main` merge commit 上创建 annotated tag `vX.Y.Z` 并推送 tag.
12. 对比上一版本 tag 到 `vX.Y.Z` 的 commit 与实际 diff, 编写准确且面向用户的 Markdown Release Notes (合并同类变更, 剔除内部维护/格式化/发版噪声, 不直接粘贴 commit 列表, 不使用 `--generate-notes`, 不写入未在 diff 中确认的内容), 保存至 `Builds/Release/vX.Y.Z/release-notes.md`.
13. 创建 GitHub Release 并上传压缩包:

    ```powershell
    gh release create vX.Y.Z `
      "Builds\Release\vX.Y.Z\CodexTray-vX.Y.Z-win-x64.zip" `
      --title "CodexTray vX.Y.Z" `
      --notes-file "Builds\Release\vX.Y.Z\release-notes.md" `
      --verify-tag
    ```

14. 核验 GitHub Release 后执行 `git switch develop` 切回 `develop`, 确认工作区干净且与 `origin/develop` 同步.

**发布中止红线**: 目标 tag 或 release 已存在时立即停止并报告, 严禁覆盖 tag; 仅在用户明确同意时才能使用 `gh release upload --clobber`; 推送遇 non-fast-forward 拒绝时立即停止, 由用户决定 rebase 或 merge.
