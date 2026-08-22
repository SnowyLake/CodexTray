# CodexTray

## 目录

- [概览](#概览)
- [效果展示](#效果展示)
- [功能](#功能)
- [安装](#安装)
- [使用](#使用)
- [Cursor 页面](#cursor-页面)
- [Grok 页面](#grok-页面)
- [API 监控](#api-监控)
- [插件支持](#插件支持)
- [数据与隐私](#数据与隐私)
- [常见问题](#常见问题)

## 概览

`CodexTray` 是一个适用于 Windows x64 的托盘应用. 它读取当前 Windows 用户的 Codex, Cursor 和 Grok Build 登录信息, 并通过本地服务把 Codex Weekly, Cursor Monthly 和 Grok Weekly 剩余额度提供给 LiteMonitor 和 TrafficMonitor 插件.

应用还会统计本机 Codex 会话的 token 用量, 按模型价格估算 API 等价成本. Grok 页面会另外统计本机 Grok Build session 中记录的 token 与费用. 所有信息都集中显示在托盘弹窗中, 无需持续打开主窗口.

除了 Codex 额度, 应用还可以在独立的 Cursor 页面查看 Cursor 额度与 token 账单统计, 在独立的 Grok 页面查看 Weekly 额度, 并在 APIs 页面监控 DeepSeek, OpenRouter, Vercel AI Gateway, NanoGPT 和 NewAPI 的余额或用量.

## 效果展示

![CodexTray showcase](Docs/showcase.png)

## 功能

- 显示 Codex 计划状态, Weekly 剩余额度和重置时间.
- 显示可用 Reset Credits 数量及最近到期时间.
- Codex, Cursor 和 Grok Token Cost 在滚动模式显示 24H, 7D, 30D 和 Lifetime, 在自然周期模式显示 Today, Week, Month 和 Lifetime. 24H 按当前时刻精确向前滚动 24 小时. 圆环模型占比与趋势图会同步切换周期. 圆环中心显示当前时段 token 总量, 以及成本与缓存命中率同行 (`$N · N%`). 自然月按整月固定宽度展示, 未来日期保留空白. Token 数量使用英制单位 K, M, B.
- Cursor 页面显示 Monthly, First party 和 APIs 剩余额度, 后两项以半宽卡片并排显示. 页面还会显示 Monthly 重置时间, 实际账单 token, 成本, 模型占比和可切换的 30 日趋势.
- Grok 页面显示订阅类型, Weekly 剩余额度和重置时间, 并统计本机 Grok Build session 的 24H, 7D, 30D 和 Lifetime token 与费用. 登录信息仅从本机 Grok Build OAuth session 读取.
- 支持 DeepSeek CNY 余额, OpenRouter 剩余与已用 credits, Vercel AI Gateway 剩余与累计已用 credits, NanoGPT USD 余额与最近 30 天用量, 以及 NewAPI 剩余与已用额度.
- 支持添加, 命名, 排序和删除多个 API 监控卡片, 并显示单项与汇总刷新状态.
- 默认每 1 分钟自动刷新, 支持 1 到 1440 分钟的自定义间隔和手动刷新.
- 支持 `System`, `Light`, `Dark` 主题和 Windows 11 Mica 背景材质, 主面板固定为 360 x 620. Windows 10 固定使用纯色背景.
- 支持隐藏 Codex, Cursor, Grok 或 APIs 页面并停止对应后台采集.
- 自动检测 LiteMonitor 与 TrafficMonitor 安装目录, 并一键安装对应插件.
- 插件固定显示 Codex, Cursor 和 Grok 三项剩余额度百分比.
- 支持随 Windows 启动, 自定义本地 HTTP 端口和单实例运行.

## 安装

1. 从 [GitHub Releases](https://github.com/SnowyLake/CodexTray/releases) 下载 `CodexTray-vX.Y.Z-win-x64.zip`.
2. 解压完整目录, 不要只复制 `CodexTray.exe`.
3. 确认系统已安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
4. 运行 `CodexTray.exe`.

发布包中的 `Resources` 保存图标和模型价格, `Plugins` 保存 LiteMonitor 与 TrafficMonitor 插件文件. 缺少这些目录时, 部分界面或插件安装功能将不可用.

## 使用

首次启动时, 应用会保存默认设置并打开主面板. 之后应用常驻 Windows 系统托盘.

- 左键单击托盘图标: 打开或隐藏主面板.
- 右键单击托盘图标: 使用 `Open Panel`, `Refresh Now` 或 `Exit`.
- Codex 页: 查看 Weekly 额度, Reset Credits, 四个时段的 Token Cost, 当前时段模型 token 占比, 可切换的最近 30 天与当前自然月趋势, 以及最近更新时间.
- Cursor 页: 查看 Monthly, First party, APIs 额度和实际账单 Token Cost.
- Grok 页: 查看从本机 Grok Build 登录信息获取的 Weekly 额度, 重置时间和本地 Token Cost 统计.
- APIs 页: 添加和查看 DeepSeek, OpenRouter, Vercel AI Gateway, NanoGPT 或 NewAPI 监控卡片.
- Settings 页: 调整可见页面, 刷新, 显示, 自启动, 插件目录和 HTTP 端口设置. `Visible pages` 可分别隐藏 Codex, Cursor, Grok 和 APIs 入口并停止对应后台采集. 同时隐藏 Codex, Cursor 和 Grok 时会停止本地 HTTP 服务.
- About 页: 查看当前版本, 项目主页和许可证信息.

再次运行 `CodexTray.exe` 不会启动第二个实例, 而是通知已有实例打开主面板.

## Cursor 页面

Cursor 页面直接读取本机 Cursor IDE 已保存的 OAuth session (`state.vscdb`). 页面会请求 Cursor 官方 usage-summary 和 usage-events 接口. 两个数据区域共享一次本地凭据读取和最多一次 OAuth refresh 重试. Monthly, First party 和 APIs 显示独立剩余额度, First party 与 APIs 以半宽卡片并排显示, 只有 Monthly 显示重置时间. Monthly 同时进入本地插件接口. Token Cost 与 Codex, Grok 页面共享时段列表, 模型占比圆环和可切换的 30 日趋势图.

Cursor Token Cost 使用 Cursor usage events 返回的实际 `totalCents`, 不会用本地模型价格表重算. OAuth token 不会写入 `settings.json`.

## Grok 页面

Grok 页面只读取本机 Grok Build 保存在 `~/.grok/auth.json` 中的 xAI OAuth session, 并向 Grok 官方 billing 接口查询 Weekly 额度和重置时间. 订阅类型显示在页面标题旁, 使用 Grok Build 本地 billing 日志中的最近类型; 日志中没有有效记录时, 仅检查当前 access token 对应的 auth 条目, 不会从未知 protobuf 字段猜测套餐. 常见返回值包括 `Free`, `SuperGrok`, `SuperGrok Heavy` 和 `X Premium` 系列. 上方额度区域只有一张 Weekly 大卡片, 重置时间遵循 Settings 页中的倒计时或绝对时间格式设置.

页面下方会读取 `~/.grok/sessions/**/updates.jsonl` 与 `~/.grok/archived_sessions/**/updates.jsonl`, 按 `turn_completed` 事件汇总 24H, 7D, 30D 和 Lifetime token 与费用, 并显示模型占比和可切换的 30 日趋势. 每轮 token 按 `inputTokens + outputTokens` 统计, `reasoningTokens` 已包含在 output 中, 不会重复相加.

费用规则与 [CCSwitch 的 Grok Build 会话导入](https://github.com/farion1231/cc-switch/blob/c0050623194303ecc95c3ce7ca8e362bce21e762/src-tauri/src/services/session_usage_grokbuild.rs) 保持一致. 完整的 `costUsdTicks` 是 Grok Build 自报的本轮精确费用, CodexTray 会优先使用它, 因而可以保留工具调用等官方已计入的费用. 当自报费用缺失或被 `costIsPartial` 标记为部分费用时, 再使用 `Resources/model-pricing.json` 中对应模型的输入, 缓存输入和输出价格回算. 未收录价格的模型仍统计 token, 成本按 `$0.00` 计入.

access token 临近过期或被接口拒绝时, CodexTray 会使用 refresh token 自动续期并写回 Grok Build 的 `auth.json`. OAuth token 不会复制到 `settings.json`. 隐藏 Grok 页面后会停止 Grok 用量采集.

## API 监控

在 APIs 页点击右上角的添加按钮, 选择 provider 并填写对应信息, 再点击卡片右上角的保存按钮. API 监控会按刷新间隔自动更新, 也可以手动刷新. 隐藏 APIs 页后会停止 API 监控采集.

- DeepSeek: 填写 Base URL 和 API key, 默认 Base URL 为 `https://api.deepseek.com`.
- OpenRouter: 填写 Base URL 和 Management Key, 默认 Base URL 为 `https://openrouter.ai`. 普通 API key 的 `/key` limit 不是账户余额, 因此不用于此卡片.
- Vercel: 填写 Base URL 和 AI Gateway API key, 默认 Base URL 为 `https://ai-gateway.vercel.sh`. 卡片显示团队剩余 credits 和累计已用量; 普通 Vercel 账号 token 不能查询此接口.
- NanoGPT: 填写 Base URL 和 API key, 默认 Base URL 为 `https://nano-gpt.com`. 卡片优先显示 USD 余额, 并在接口可用时显示最近 30 个 UTC 日的已用金额; 用量查询失败不会影响余额显示.
- NewAPI: 填写实例 Base URL, access token 和 User ID.

卡片支持自定义显示名称, 调整顺序和删除. Cursor 与 Grok 均使用独立页面, 不作为 API 监控卡片.

## 插件支持

CodexTray 支持 LiteMonitor 与 TrafficMonitor. 在 Settings 页找到对应监控器, 使用 `Browse` 手动选择目录或 `Auto detect` 自动定位, 然后点击 `Setup` 安装插件. 安装完成后重启对应监控器或重新加载插件.

LiteMonitor 显示 `Codex`, `Cursor` 和 `Grok` 三项, 从 JSON 接口依次读取 Codex Weekly, Cursor Monthly 和 Grok Weekly. TrafficMonitor 原生插件显示同样三项, 从三行文本接口读取数据. 三项仅显示剩余百分比, 不附加重置时间.

隐藏 Codex, Cursor 或 Grok 页面会停止对应数据采集, 并让对应插件项显示 `N/A`. 同时隐藏这三个页面后, 本地 HTTP 服务会停止.

如果修改了 CodexTray 的 HTTP 端口, 请重新执行 `Setup`, 让插件配置同步到新端口.

## 数据与隐私

- 额度和 Reset Credits 来自 ChatGPT 官方接口. 应用读取 `~/.codex/auth.json` 中的 Codex OAuth 凭据.
- Codex Token Cost 来自本机 Codex session 日志, 并使用发布包中的 `Resources/model-pricing.json` 计算 API 等价成本和缓存命中率. 它不读取 OpenCode. Grok Token Cost 只读取本机 `~/.grok/sessions` 和 `~/.grok/archived_sessions` 中的逐轮用量, 优先采用 Grok Build 自报费用并以本地价格表兜底.
- DeepSeek, OpenRouter, Vercel, NanoGPT 与 NewAPI 请求直接发送到卡片中配置的 Base URL. API key, Management Key, access token 和 User ID 以明文保存在 `CodexTray.exe` 同级目录的 `settings.json` 中.
- Cursor 页面读取本机 Cursor IDE 的 `state.vscdb` OAuth session, 并向 Cursor 官方 usage-summary 与 usage-events 接口查询额度和账单用量. access token 过期前会通过 Cursor OAuth refresh 自动续期并写回原数据库. OAuth token 不会复制到 `settings.json`.
- Grok 页面只读取 Grok Build 已保存的本地 OAuth session, 并向 Grok 官方接口查询用量. access token 过期前会通过 xAI OAuth refresh 自动续期并写回 `~/.grok/auth.json`. OAuth token 不会复制到 `settings.json`.
- 本地 HTTP 服务默认仅监听 `127.0.0.1:17890`, 不向局域网开放.
- OAuth token 不会写入日志, 插件配置或本地 HTTP 响应.
- 应用设置保存在 `CodexTray.exe` 同级目录的 `settings.json`.

## 常见问题

### 为什么额度显示 N/A

请确认当前 Windows 用户已登录 Codex, `~/.codex/auth.json` 存在且凭据有效, 并且网络可以访问 ChatGPT.

### 为什么 Token Cost 显示 N/A

Codex 页请确认发布目录包含 `Resources/model-pricing.json`, 并且当前用户存在 Codex session 日志. 未收录价格的模型可以统计 token, 成本按 `$0.00` 计入. Grok 页请确认 session 日志中存在 `turn_completed` usage. 完整的自报费用不依赖本地价格表; 缺少自报费用且未收录价格的模型同样按 `$0.00` 计入.

### 为什么 API 卡片显示 N/A

将鼠标悬停在卡片名称或状态圆点上查看错误信息. DeepSeek, OpenRouter, Vercel, NanoGPT 和 NewAPI 需要有效的 Base URL 与凭据, NewAPI 还需要 User ID, OpenRouter 必须使用 Management Key, Vercel 必须使用 AI Gateway API key.

### 为什么 Cursor 页面显示 N/A

请确认本机 Cursor IDE 已登录. CodexTray 会自动刷新 Cursor access token; 若 refresh token 也失效, 请回到 Cursor IDE 重新登录. 额度与 Token Cost 分别来自两个接口, 因此其中一部分失败时另一部分仍可能正常显示.

### 为什么 Grok 页面显示 N/A

请先在 Grok Build 中完成 xAI OAuth 登录, 并确认 `~/.grok/auth.json` 存在. CodexTray 会自动刷新 Grok access token; 若 refresh token 也失效, 请运行 `grok login` 重新登录.

### 为什么 LiteMonitor 或 TrafficMonitor 没有更新

请确认 CodexTray 正在运行, 且 Settings 中 Codex, Cursor 或 Grok 至少一个页面可见. 在托盘菜单中点击 `Refresh Now`, 再检查监控器路径并重新执行 `Setup`. 如果修改过 HTTP 端口, 必须重新安装插件配置.

### 为什么找不到 LiteMonitor 或 TrafficMonitor

自动检测会搜索本机磁盘中的 `LiteMonitor.exe` 或 `TrafficMonitor.exe`. 也可以使用 `Browse` 直接选择包含对应可执行文件的目录.

### 为什么托盘图标没有直接显示在任务栏

Windows 负责管理托盘图标的可见区域. 请在系统托盘展开区或 Windows 的任务栏设置中调整 CodexTray 的显示状态.
