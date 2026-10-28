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

`CodexTray.Core` 负责采集, 设置, 缓存, 本地 HTTP 服务, 插件安装和应用内更新. `CodexTray.App` 负责 WPF 界面与托盘编排. Codex, Grok 和 API 监控分别采集. Codex Weekly, Grok Weekly 和第一张 DeepSeek 卡片的 CNY 余额进入插件响应. Grok Weekly 与产品占比来自同一次 `https://cli-chat-proxy.grok.com/v1/billing?format=credits` 查询. 订阅名称优先使用 `GET https://cli-chat-proxy.grok.com/v1/settings` 的 `subscription_tier_display`. Token Cost 仅在 Codex 与 Grok 页面显示, 其他 API provider 结果仅显示在主面板.

## 架构与数据流

### 应用生命周期

1. `CodexTray.App/Program.cs` 使用 mutex 保证单实例. 后续进程通过 `TrayShowPanel` event 通知已有实例打开面板后退出. `--apply-update` 是更新进程, 不获取 mutex, 也不打开面板.
2. `CodexTray.App/App.cs` 创建 WPF application host, `TrayController` 管理托盘, 设置, 定时刷新, 插件安装和本地服务.
3. 首次启动由 `SettingsStore` 写入默认 `settings.json` 并打开主面板. 后续设置加载时会补齐缺失字段并规范化值.
4. `TrayPopupWindow` 与 `TrayPopupViewModel` 提供 Codex/Grok/APIs/Settings/About 页面. `ApiMonitorViewModel` 管理单张 API 卡片的编辑与显示状态. 左键切换弹窗, 右键菜单仅包含 `Open Panel`, `Refresh Now` 和 `Exit`.
5. `AppSettings.VisiblePages` 控制 Codex, Grok 与 APIs 页的可见性和后台采集. `VisiblePages` 为空时停止本地 HTTP 服务和定时刷新.
6. `TrayController` 统一持有应用生命周期 cancellation token, 跟踪刷新, 插件定位, 单实例信号和本地服务切换任务. 各来源独立发布结果, Codex/Grok 的额度与本地 Token Cost 分开发布; 每次发布前复核退出状态和页面可见性, 单来源失败不阻止其他来源. 刷新所有发布任务仍统一等待, 保持请求合并与退出排空. 正常退出时先取消并等待后台任务, 再异步停止本地服务和关闭 WPF application.
7. `RefreshRequests` 合并待执行的来源位掩码和 API 卡片 ID; 全部 APIs 请求覆盖单卡请求. 当前 owner 排空后才开始下一批, 防止同一采集器并发执行. 页面刷新仅请求该来源; 卡片保存与重试仅请求该卡片. `ApiUsageSnapshots` 合并单卡结果, 按实时配置顺序重建第一页 DeepSeek 插件映射, 配置已修改或卡片已删除时丢弃迟到结果. 隐藏 APIs 时不得重新发布 DeepSeek 缓存.
8. `UsageHttp` 仅用于只读额度与用量查询 (包括只读 POST), 不用于 OAuth token POST. 网络错误, 非调用方取消的超时, HTTP 429 与 5xx 最多尝试 3 次, 默认等待 500/1000 毫秒; `Retry-After` 支持秒数与日期, 累计等待预算 10 秒, 超出预算直接结束而不缩短服务要求的等待. 每次重新构造请求与内容, 中间响应先释放, 等待可取消. 401/403, 其他 4xx 与解析错误不自动重试, 复用 Grok 原有最多一次强制 OAuth 续期. Grok 额度失败后不改走其他额度端点.
9. `UsageDiagnostics` 使用固定 HTTP/异常描述和错误类别; 不回显服务器 reason phrase, JSON message 或任意 exception.Message. Grok 自有解析异常保留固定字段描述, 复制诊断只含应用版本, 固定来源/provider 名称, 成功时间与错误类别, 不含 URL, 账号, 卡片 ID/名称, 请求头或响应原文. 复制诊断和恢复备份入口集中在 Settings 的 Advanced 一栏, 复制按钮传入 `PageItem.All` 汇总所有来源.

### 应用内更新

1. 启动时及运行中每隔 6 小时静默请求 `CodexTrayDefaults.GitHubLatestReleaseUrl`, 间隔统一维护在 `CodexTrayDefaults.UpdateCheckIntervalHours`. 检查循环由 `TrayController` 持有, 独立于数据页可见性, 退出时取消并等待. 自动与手动检查及安装共用 ViewModel busy 状态避免重叠; 自动检查失败不弹窗并保留已发现的版本. 发现新版本后, Settings 导航图标显示消息点, 版本号右侧显示 `New`, `Open About` 左侧按钮切换为安装图标, 点击后确认安装; 无已知更新时按钮执行手动检查. 不自动下载, 不使用 GitHub token. 请求必须带 `User-Agent: CodexTray`, 检查超时 20 秒. 手动检查发现新版本时用内部对话框询问是否安装, 确认后才下载. 手动检查已是最新或失败时, 用同一对话框显示结果.
2. 只接受 tag `vX.Y.Z` 和资产名 `CodexTray-vX.Y.Z-win-x64.zip`. 下载 URL 必须是 `https://github.com` 上对应的 release asset. 用资产 `digest` 的 SHA-256 和 `size` 校验, 包大于 100 MB 时拒绝. 下载超时 120 秒.
3. 解压时拒绝绝对路径, 盘符和 `..`. 包内必须同时有 `CodexTray.exe`, `Resources` 下的图标和 `model-pricing.json`, 以及 LiteMonitor 与 TrafficMonitor 插件文件.
4. 校验通过后, 把新包中的 `CodexTray.exe` 复制到临时目录, 用 `--apply-update --wait-pid --parent-started-utc --source --target --restart` 启动. 主进程再走现有退出流程. 更新进程用 PID 和启动时间确认旧进程, 等待它退出, 最长 10 分钟. 覆盖和还原期间持有 `CodexTrayMutex`, 并在启动新进程之前释放.
5. 覆盖前把安装目录备份到临时目录. 先复制资源和插件, 最后复制 exe. 单个文件最多重试 10 次, 间隔 250 毫秒. 根目录已有的 `settings.json` 不覆盖, 安装目录里的其他用户文件保留. 复制失败时从备份还原; 还原成功才重新启动旧版本.
6. 安装目录不可写, 或当前进程不是 `CodexTray.exe` 时, 不退出程序. 根目录存在 `CodexTray.dll` 时视为开发编译, 检查和安装都拒绝, 也不退出程序. `Plugins/TrafficMonitor/CodexTray.dll` 不算. 超过 1 小时的更新临时目录会在下一次准备更新时删除.

### 额度与插件链路

1. `CodexUsageCollector` 读取 `~/.codex/auth.json` 中的 OAuth 凭据, 从 ChatGPT 官方 usage 与 rate-limit-reset-credits 接口采集 Session, Weekly 和 Reset Credits. 凭据缺失或无效时返回不可用状态. Grok Weekly 插件值由 `GrokUsageCollector.BuildPluginUsage` 从 billing 的 `creditUsagePercent` 生成. `ApiUsageCollector.BuildDeepSeekPluginUsage` 将 APIs 页中第一张 DeepSeek 卡片的 CNY 余额四舍五入为整元后转换为插件数据, 卡片本身仍显示两位小数.
2. `TrayController` 将 Codex, Grok 与 DeepSeek 的最新结果分别写入 `UsageCache`, 由缓存合并为插件响应. 隐藏对应页面时会清除该缓存. 恢复备份时清空这三份插件缓存, 并立刻重新刷新.
3. `LightweightHttpServer` 默认监听 `127.0.0.1:17890`, 暴露以下接口:
   - `/codex-tray`: LiteMonitor 使用的 JSON 响应. DeepSeek 显示 `display.deepseek` 与 `limits.deepseek`; 余额格式为整元 `¥N`; `remaining_percent` 在余额可用时为 100, 否则为 0, 供 LiteMonitor 着色.
   - `/codex-tray.txt`: TrafficMonitor 使用的三行文本, 依次为 Codex Weekly, Grok Weekly 和 DeepSeek 整元 CNY 余额.
   - `/health`: 返回本地服务健康状态.
4. `LiteMonitorPluginInstaller` 和 `TrafficMonitorPluginInstaller` 从发布目录读取模板, 写入当前端口后安装到监控器目录.

### API 监控链路

1. `AppSettings.ApiMonitors` 保存 API 监控卡片的顺序和 provider 配置. 支持的 provider 及新建卡片下拉顺序为 DeepSeek, OpenRouter, Vercel, NanoGPT 与 NewAPI, 由 `ApiMonitorViewModel.ProviderOptions` 固定. `TrayPopupViewModel` 负责增删, 排序和持久化卡片. 升降箭头仅位于卡片编辑页, 排序立即持久化已有卡片的顺序, 不保存未完成的字段编辑或 pending 卡片. `AppSettings.Normalize` 会移除旧版 Cursor 卡片.
2. `ApiUsageCollector` 并行刷新所有卡片. DeepSeek 使用 `/user/balance`, OpenRouter 使用 `/api/v1/credits`, Vercel 使用 `/v1/credits`, NanoGPT 使用 `/api/check-balance` 和 `/api/v1/usage`, NewAPI 使用 `/api/user/self` 并发送 `New-Api-User` header.
3. 请求发送到各卡片配置的 Base URL. OpenRouter 使用 Management Key, 不使用普通 API key 的 `/key` limit 作为账户余额. Vercel 使用 AI Gateway API key, 不使用普通 Vercel 账号 token. NewAPI 还需要 User ID. NanoGPT 用量按最近 30 个 UTC 日查询, 用量失败不影响余额显示.
4. `TrayController` 将结果交给 `TrayPopupViewModel` 更新单卡片状态与 APIs 页汇总状态, 并将第一张 DeepSeek 卡片写入 `UsageCache`. 其他 API provider 不进入插件 HTTP 响应. 有多张 DeepSeek 卡片时只使用最靠前的一张, 该卡片不可用时显示 `N/A`, 不回退到后续 DeepSeek 卡片.

### Grok 页面链路

1. `GrokUsageCollector` 从 Grok Build 的 `~/.grok/auth.json` 读取 xAI OAuth access token, 不使用 OpenCode 凭据. 临近过期, 或额度请求返回 401/403 时, 使用 refresh token 调用 `auth.x.ai` 刷新并写回原文件. 设置请求失败不触发这次续期. OAuth token 不会复制到 `settings.json`.
2. Grok 额度请求 `GET https://cli-chat-proxy.grok.com/v1/billing?format=credits`, 与 Grok Build 客户端相同. `https://grok.com/rest/grok/credits` 对非浏览器返回 Cloudflare 挑战页, 不使用. 响应提供 Weekly 已用百分比 `creditUsagePercent`, 账期结束时间 (`currentPeriod.end`, 缺失时用 `billingPeriodEnd`) 和 `productUsage` 产品占比. 当前响应可以只有 `config`, 不带 `subscriptionTier`. 已用百分比先限制在 0 到 100. `productUsage` 缺少产品名或名称为空时记为 `Cursor`. 插件把已用百分比四舍五入为整数, 剩余为 `100 - 已用整数`. 卡片把剩余百分比 `100 - 已用` 四舍五入为整数. 重置时间使用该账期结束时间.
3. 订阅徽章在额度采集成功后请求 `GET https://cli-chat-proxy.grok.com/v1/settings`, 请求头与 CLI 额度查询相同. 优先使用响应根对象的 `subscription_tier_display`; 该字段为空时使用同一响应的 `subscription_tier`. 两者都为空, 或这次设置请求失败时, 若 `~/.grok/logs/unified.jsonl` 有更新的 `subscriptionTier`, 或当前 access token 的 auth 条目有订阅档位, 用该本地记录. 本地记录也没有时, 保留 billing 响应根对象或 `config` 的 `subscriptionTier`. 不请求 `GET /v1/user?include=subscription`, 不用其中的 `subscriptionTier` (例如 `GrokPro`) 作为徽章来源. 不读取 Cursor 登录, 也不请求 `GetSandUsageStatus`, `usage-summary` 或 `get-filtered-usage-events`. 常见类型包括 `Free`, `SuperGrok`, `SuperGrok Heavy` 和 `X Premium` 系列. `LooksLikeSubscriptionTier` 只过滤本地日志和 auth 回填, 接受 `Free`, 或包含 `Premium`, `SuperGrok`, `Business`, `Enterprise` 的字符串.
4. `TrayController` 将 dashboard 交给 `TrayPopupViewModel`, 并将 `GrokUsageCollector.BuildPluginUsage` 写入 `UsageCache`. Grok billing 失败会清除已发布的 Weekly. 本地 Token Cost 由 `TokenCostCollector.CollectGrok` 独立统计.

### 本地 Token Cost

- `TokenCostSummary.HasUnpricedUsage` 按事件与所选周期传播, 保留已知费用小计; Grok 部分自报费用无法完整回算时同样标记. `RefreshState` 保存每部分最后成功时间和当前错误, 失败时 UI 和插件清空数值为 `N/A`. 部分费用在金额旁显示 `*`. 时段列表, 圆环中心与 Codex, Grok 标题栏更新时间不显示 tooltip. 价格表按内容比较并同步加载, Codex 在价格字典变化后清空已计算费用的会话缓存, 不依赖文件长度或修改时间.
- `TokenCostCollector.CollectCodex` 读取 `~/.codex/sessions/**/*.jsonl` 与 `*.jsonl.zst`, 以及 `~/.codex/archived_sessions` 根目录下的同类文件. Codex 的「压缩本地聊天记录」对应 `features.local_thread_store_compression`: 超过 7 天的 rollout 被整文件压成 zstd `.jsonl.zst` 并删除明文, 解压后的行与原 JSONL 相同, `token_count` 仍按事件时间分日. 同一路径同时存在明文和 `.zst` 时只读明文. 使用 `Resources/model-pricing.json` 计算 API 等价成本与缓存命中率. 不读取 OpenCode. 同一 thread 的桌面分页文件按文件名顺序拼接后共享累计水位; 父会话未写到 `forked_from` / `parent_thread_id` 截止点的子会话整段不计.
- `model-pricing.json` 每个模型使用默认一口价 `input`/`cachedInput`/`output`, 可选 `aliases` 与 `periods`. 默认三价覆盖全天. `periods` 按数组顺序命中第一段: 每段可写 `daysUtc` (0=周日到 6=周六), `startUtc`, `endUtc` (UTC `HH:mm`, 含起不含止). 省略星期或起止时间则该段默认全星期或全天. 未命中任何时段时回退默认三价. 分时只影响本地回算, 不拆分 UI, 也不覆盖 Grok 完整自报 `costUsdTicks`.
- `CollectGrok` 读取 `~/.grok/sessions/**/updates.jsonl` 和 `~/.grok/archived_sessions/**/updates.jsonl`, 汇总 `turn_completed` 事件中的用量. 每轮 token 按 `inputTokens + outputTokens` 统计, `reasoningTokens` 已包含在输出中, 不重复相加.
- Grok 费用优先采用完整的 `costUsdTicks`, 保留其中已计入的工具调用等费用. 自报费用缺失或 `costIsPartial` 为真时, 使用本地模型的输入, 缓存输入和输出价格回算. 只有费用而没有 token 的记录仍保留自报费用; 本地价格不可用时保留已有自报费用. 两者都不可用时, 仍统计 token, 成本按 `$0.00` 计入.
- Grok 费用规则参考 [CCSwitch 的 Grok Build 会话导入](https://github.com/farion1231/cc-switch/blob/c0050623194303ecc95c3ce7ca8e362bce21e762/src-tauri/src/services/session_usage_grokbuild.rs). 修改解析或计费时, 以当前代码和测试确认边界行为.
- Codex 和 Grok 模型圆环 tooltip 的 `Speed` 使用所选周期的 `TimedOutputTokens` 总和除以 `TimedDurationMilliseconds` 总秒数, 同名模型与 `Other` 先合并分子和分母后计算, 显示一位小数. 不计入没有有效配对耗时的输出, 不重复相加 reasoning. 没有有效速度样本时不显示 `Speed`; `CostUsd` 为空时圆环中心只保留缓存命中率, tooltip 和趋势图也不显示 `Cost`. `TokenCostDashboardViewModel` 在构造时固定速度显示开关.
- Grok `Speed` 显示 `N.N tok/s`, 使用 `turn_completed.usage.modelUsage[model]` 中同一对象的非负整数 `outputTokens` 与正整数 `apiDurationMs`; 无 `modelUsage` 时沿用顶层 `usage` 的 `unknown` 回退. 先沿现有 session/prompt/model 去重, 不分摊顶层耗时到模型. 只计入输出不少于 100 且 `apiDurationMs` 不少于 100 毫秒的调用. 报告的 API 耗时包含首 token 等待等开销, 部分调用未报告耗时时结果可能偏高.
- Codex `Speed` 显示 `~N.N tok/s`, `TokenCostStatistics.IsSpeedEstimated` 标识估算口径. 仅在有效 `task_started.turn_id` 和匹配的 `turn_context` 下重建响应窗口: 最近输入或已接受用量的时间为起点; 匹配的 `token_usage_record` 优先作为终点, 否则用最后 reasoning, assistant message 或工具调用 `response_item`; 排除输出完成后的工具等待, 并忽略 `token_count` 后 100 毫秒内补写的旧输出项. 只将已接受的 `last_token_usage` 输出配到同模型的窗口; 输出少于 200 或耗时不在 1 秒到 1 小时内的窗口不计入速度. 重复快照不截短窗口, 父会话回放不累计速度, 缓存同时保留输出与耗时. 缺少边界, 乱序, 轮次不匹配, 中断, 错误或压缩后的不完整窗口不估算. 该速度仍包含首 token 等待, 网络和客户端调度, 不使用整轮耗时或减去整轮 TTFT 来推断. 窗口思路参考 [codex-tps](https://github.com/adenta/codex-tps/blob/be549617f50409c8c0e15a8a6838520ec12d2bb1/analysis.go) 与 CC Switch 的会话估算门槛.
- Codex 分叉会话在父会话日志尚未写到分叉时刻时整段跳过, 不把回放前缀当新用量. 双段文件名 `rollout-…-<threadId>_<rolloutId>.jsonl` 按 thread id 拼页后再解析累计增量和父会话截止点; 同一 rollout id 的归档副本仍只计一份.
- `TokenCostStatistics.SessionLogs` 在同一次采集中保留明细, 按时间从新到旧. 本地日期晚于本次采集日的事件不进入列表, 与周期合计相同. Codex 一行是一次计入费用的 `token_count` 增量, Grok 一行是去重后的一次 `turn_completed` 模型用量. 行内速度沿用已配对的 `TimedOutputTokens` 与 `TimedDurationMilliseconds`, 未达门槛则速度为空. Codex 缓存命中时从已缓存事件生成这些行. 该列表不进入插件响应. `SessionLogListViewModel` 每页 20 行. Codex 与 Grok 标题栏刷新按钮左侧使用固定的列表图标切换中间区域, 打开时只把前景改为 `#1ABC89`, 不更换字形. 页眉, 页脚和开关状态不写入 settings; 两个来源各自独立. 再点一次按钮回到额度与 Token Cost. 列表滚到边缘后滚轮翻页. 列表列为 Time, Model, Speed, 不显示费用. 三列文字都从列首开始, Time 列宽 100, Speed 列宽 76, Model 使用剩余宽度. 单元格速度为四舍五入整数加 `tok/s`, Codex 估算值前加 `≈`.

### 设置边界

- `SettingsStore.LoadError` 非空时禁止全部持久化, 自动插件定位也跳过. 读取与保存均验证 JSON object 和可反序列化字段, 运行期间文件损坏同样阻止覆盖. 主配置已成功解析时, 后续备份或规范化写入失败仍返回该有效配置, 同时报告错误并阻止持久化. `settings.last-good.json` 保存上一份有效配置; 恢复先验证备份, 把当前文件复制为 `settings.damaged-<guid>.json`, 再原子替换主文件. 主配置, 备份和保留副本中的 API 凭据均使用相同明文表示, 不得分享这些文件. 恢复会替换 ViewModel 的共享配置引用并阻止旧配置的在途刷新发布.
- 默认值, 端口范围, HTTP 路径, 文件名和发布资源目录统一维护在 `CodexTrayDefaults` (`CodexTray.Core/CodexTrayDefaults.cs`).
- 刷新间隔范围为 1 到 1440 分钟, 默认 1 分钟.
- 主题支持 `System`, `Light`, `Dark`. Windows 11 默认启用 Mica, Windows 10 固定使用纯色背景.
- 主面板尺寸固定为 360 x 620.
- `TrayController.BuildTrayTooltip` 只格式化当前可见来源的插件缓存, 使用 `Codex W`, `Grok W` 和 `DeepSeek` 标签, 总长最多 63 字符. 已停用的页面位不增加托盘行. 额度限制在 0 到 100 的整数, DeepSeek 整元余额超过 11 字符时截短并加省略号; 不回显源错误或凭据. 各来源发布与页面可见性变化后更新托盘 Text. `TrayPopupViewModel.IsPanelPinned` 默认 false, 不写 settings, 开启后 `OnDeactivated` 不隐藏; Esc 与托盘左键仍沿用显式隐藏逻辑, 页脚图钉使用现有绿色表示选中状态. 窗口关闭时保留 ViewModel 的采集状态, 清理应用内对话框及其确认回调, 并取消窗口的 `PropertyChanged` 订阅.
- Codex, Grok 与 APIs 页面默认可见. `VisiblePages` 为空时不运行定时刷新和本地 HTTP 服务. `PageItem` 保留 `Codex = 1`, `Apis = 4`, `Grok = 8`. 位 1 已停用, 旧配置里的该位在 `Normalize` 时被掩掉. 缺少 `SettingsSchemaVersion` 或版本低于当前值, 且 `VisiblePages` 为 7 (旧版 Codex|Cursor|Apis) 时, 视为当时的全部页面并改为 `Codex|Grok|Apis`.
- Codex 和 Grok Token Cost 共用时段列表, 模型占比圆环和趋势图. 滚动周期为 `24H`, `7D`, `30D`, `Lifetime`, 自然周期为 `Today`, `Week`, `Month`, `Year`. `24H` 从当前时刻精确向前滚动 24 小时. `Week` 从本地当周周一开始, `Month` 从本地当月 1 日开始, `Year` 从本地当年 1 月 1 日开始, 都含起始日, 截止到 `asOf` 的本地日期, 更晚的日期不计入. `Lifetime` 仍累计不晚于该本地日期的全部历史. 圆环和趋势图随周期同步切换, 趋势图可切换最近 30 天与当前自然月; 自然月按整月固定宽度展示, 未来日期留白.
- 圆环中心显示当前时段 token 总量, 成本与缓存命中率同行显示 (`$N · N%`). 时段列表与圆环中心不显示 tooltip. Token 数量使用 K, M, B.
- Codex 额度大卡片通过左下角页点或鼠标滚轮切换 Session 与 Weekly. Session 仅在有效时显示, 只有一个有效页面时隐藏页点. Resets 使用全宽小卡片. Codex 订阅徽章来自 usage 响应的 `plan_type`: `free`/`go`/`plus`/`chatgpt` 显示 `FREE`/`GO`/`PLUS`; `prolite` 及 `pro_lite`/`pro_5x`/`pro5x` 显示 `PRO 5X`; `pro` 及 `pro_10x`/`pro10x`/`pro_20x`/`pro20x` 显示 `PRO 10X`; `promax` 及 `pro_max`/`pro_25x`/`pro25x` 显示 `PRO 25X`; 其他值显示 `UNKNOWN`.
- Grok 上方使用 Weekly 大卡片和产品占比小卡片, 这是面板上唯一的订阅额度区域. 订阅徽章优先使用 `/v1/settings` 的 `subscription_tier_display`. Weekly 大卡片显示 billing 剩余整数百分比, 重置时间使用账期结束时间. 产品占比卡片分两行显示分段进度条和三个图例, 进度条与 Weekly 同粗. 进度条最多四段: 最高三项加上 `Others`. 图例固定三项, 全为零时显示 `Build`, `Bot`, `Cursor`; 不足三项时按该默认顺序补齐. 三个及以上非零产品时保留最高三项, 其余仅合并到进度条中的 `Others`. `Others` 不出现在图例中. 鼠标悬停进度条时列出全部返回产品的占比, 包括零值; 没有返回产品时不显示 tooltip. 图例前三项颜色与 Token Cost 圆环前三项一致, `Others` 与圆环第四项一致. 用量不可用时显示 `N/A`. 产品名和占比来自 billing `productUsage`. 显示名去掉 `PRODUCT_GROK_`, `PRODUCT_` 与 `Grok` 前缀, 因此 `PRODUCT_CURSOR` 显示为 `Cursor`. 缺少产品名时按 `Cursor` 计.
- API provider 的 API key, Management Key, access token 和 User ID 以明文保存在 `settings.json`.
- API key 与 NewAPI User ID 使用 `CredentialInput` 的原生 WPF `PasswordBox` 双向编辑; 明文显示仅由当前控件右侧眼睛按钮的显示/隐藏状态控制, 图标, tooltip 与无障碍名称随状态同步, 隐藏, 卸载或更换卡片后重置, 不持久化显示状态. 主配置与备份尚未启用 DPAPI, 不得把屏幕遮挡描述为加密存储.
- `settings.json` 位于 `CodexTray.exe` 同级目录.

### 演进边界

- 当前交付物仍是后台托盘应用. 未来桌面客户端预计与托盘共同存在, 新客户端应复用 `CodexTray.Core` 中不依赖 UI 的采集, 设置, 缓存, HTTP 服务, 应用内更新和插件能力.
- `CodexTray.App` 保持 WPF 与托盘编排职责. 不在真实桌面客户端入口和进程模型确定前预先引入通用 Host, DI container, 单实现 interface 或跨进程抽象.
- 可共享的长时任务必须支持 `CancellationToken`. 服务拥有者必须等待任务退出, 并在释放 `LightweightHttpServer` 前调用和等待 `StopAsync()`.

## 目录结构

- `CodexTray.Core`: Codex 官方额度采集, Grok Weekly, 产品占比与订阅名称采集, API 余额与用量采集, Token Cost 统计, 缓存, HTTP 服务, 设置存储, 应用内更新, 监控器定位与插件安装, Windows 自启动.
- `CodexTray.App`: WPF 托盘应用, Codex/Grok/APIs/Settings/About 页面, 基于 `CommunityToolkit.Mvvm` 的 ViewModel 与命令, 以及自定义数值输入控件.
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
- 修改 Grok 订阅名称来源时, 同步检查 `GrokUsageCollector`, `TrayPopupViewModel`, 对应测试和 README. 不读取 Cursor 登录, 也不请求 `GetSandUsageStatus`.
- 修改 Codex 订阅徽章映射时, 同步检查 `TrayPopupViewModel.FormatPlan`, 对应测试和本文档中的 `plan_type` 显示表.
- 修改 `VisiblePages` 或刷新调度时, 同步检查页面导航, 定时器, 插件本地 HTTP 服务启停, 缓存清理和各采集器调用条件.
- 修改 WPF 布局或主题时, 检查是否需要更新 `Docs/showcase.png`.
- 本地服务必须保持仅监听 `127.0.0.1`. 除第一张 DeepSeek 卡片的 CNY 余额外, 其他 API 监控结果不得进入插件 HTTP 响应. 不在日志, HTTP 响应, 文档示例或插件配置中暴露 OAuth token 或 API key.
- `Scripts/Publish-App.ps1`, `Scripts/Restart-App.ps1` 和 `Scripts/Package-Release.ps1` 共享 `Scripts/Publish-Shared.ps1`. 发布参数, 清理逻辑或进程重启逻辑优先修改共享脚本. `Scripts/Restart-App.ps1` 只重启当前发布输出中的程序, 不执行发布.
- 修改应用内更新的包名, 校验, 保留文件, `--apply-update` 参数或更新对话框时, 同步检查 `AppUpdateRelease`, `AppUpdatePackage`, `AppUpdateInstaller`, `GitHubAppUpdateClient`, `Program.cs`, Settings 页版本行, 对应测试和 README.

## 构建与输出

- `bin` 和 `obj` 使用项目默认位置.
- 不提交 `bin`, `obj`, `Builds` 或 `Plugins/TrafficMonitor/Builds` 下的生成文件.
- App 发布为 `net10.0-windows`, `win-x64`, 单文件, framework-dependent 应用. `CodexTray.Core` 目标框架为 `net10.0`.
- `Scripts/Publish-App.ps1` 清理已有发布输出时必须保留 `settings.json`, `settings.last-good.json` 和 `settings.damaged-*.json`.
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

验证发布清理保留配置文件:

```powershell
.\Scripts\Test-PublishCleanup.ps1
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
