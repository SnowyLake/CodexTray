# CodexTray

## 目录

- [概览](#概览)
- [效果展示](#效果展示)
- [功能](#功能)
- [安装](#安装)
- [使用](#使用)
- [Token Cost](#token-cost)
- [Cursor 页面](#cursor-页面)
- [Grok 页面](#grok-页面)
- [API 监控](#api-监控)
- [插件支持](#插件支持)
- [数据与隐私](#数据与隐私)
- [常见问题](#常见问题)

## 概览

`CodexTray` 是一个适用于 Windows x64 的托盘应用, 用于查看 Codex, Cursor 和 Grok 的剩余额度, 重置时间, token 用量与费用, 也支持监控多个 API 账户的余额. 点击托盘图标即可查看, 平时无需保持窗口打开.

配合 LiteMonitor 或 TrafficMonitor, 还可以在监控器中显示 Codex Weekly, Cursor Monthly 和 Grok Weekly 的剩余百分比, 以及第一张 DeepSeek 卡片的 CNY 余额.

本文介绍功能, 安装和使用方法. 开发维护说明见 [AGENTS.md](AGENTS.md).

## 效果展示

![CodexTray showcase](Docs/showcase.png)

## 功能

- 查看 Codex 计划状态, Session 与 Weekly 额度, Resets 数量及最近到期时间.
- 查看 Cursor Monthly, Grok Bot Weekly, First party 和 APIs 额度, 以及 Grok Weekly 额度与各产品使用占比.
- 按时段查看 token 总量, 费用, 缓存命中率, 模型占比和用量趋势.
- 在 APIs 页添加多个余额监控卡片, 自定义名称, 调整顺序或删除.
- 自动刷新并显示刷新状态, 也可以随时手动刷新.
- 支持浅色, 深色和跟随系统的主题, 可隐藏不使用的页面, 设置随 Windows 启动.
- 自动检测 LiteMonitor 与 TrafficMonitor 目录, 一键安装插件.

## 安装

1. 从 [GitHub Releases](https://github.com/SnowyLake/CodexTray/releases) 下载 `CodexTray-vX.Y.Z-win-x64.zip`.
2. 解压完整目录, 不要只复制 `CodexTray.exe`.
3. 确认系统已安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
4. 运行 `CodexTray.exe`.

发布包中的 `Resources` 保存图标和模型价格, `Plugins` 保存 LiteMonitor 与 TrafficMonitor 插件文件. 缺少这些目录时, 部分界面或插件安装功能将不可用.

## 使用

首次启动时, 应用会保存默认设置并打开主面板. 之后应用常驻 Windows 系统托盘.

使用 Codex, Cursor 或 Grok 页面前, 请先在本机登录对应的 Codex, Cursor IDE 或 Grok Build. CodexTray 会使用已有登录信息.

- 左键单击托盘图标: 打开或隐藏主面板.
- 右键单击托盘图标: 使用 `Open Panel`, `Refresh Now` 或 `Exit`.
- Codex 页: 查看额度, Resets 和 Token Cost. 点击额度卡片左下角的页点或使用鼠标滚轮切换 Session 与 Weekly. Session 仅在有效时显示.
- Cursor 页: 查看 Monthly, Grok Bot Weekly, First party, APIs 额度和实际账单 Token Cost.
- Grok 页: 查看订阅类型, Weekly 额度, 重置时间, 各产品使用占比和 Token Cost.
- APIs 页: 添加和查看 DeepSeek, OpenRouter, Vercel AI Gateway, NanoGPT 或 NewAPI 监控卡片.
- Settings 页: 调整刷新间隔, 主题, 重置时间格式, 自启动和插件设置. 在 `Visible pages` 中隐藏页面后, 对应的后台采集也会停止.
- About 页: 查看当前版本, 项目主页和许可证信息.

再次运行 `CodexTray.exe` 不会启动第二个实例, 而是通知已有实例打开主面板.

默认每 1 分钟刷新一次, 可在 Settings 中设为 1 到 1440 分钟. 主题支持 `System`, `Light` 和 `Dark`; Windows 11 支持 Mica 背景, Windows 10 使用纯色背景.

## Token Cost

Codex, Cursor 和 Grok 页面都可以按时段查看 token 总量, 费用, 缓存命中率和模型占比, 并切换最近 30 天或当前自然月的趋势图.

- 滚动周期: `24H`, `7D`, `30D`, `Lifetime`. `24H` 表示从当前时刻向前的 24 小时.
- 自然周期: `Today`, `Week`, `Month`, `Lifetime`.

三个页面的费用来源不同:

| 页面 | 费用含义 |
| --- | --- |
| Codex | 根据本机会话用量和模型价格估算的 API 等价成本, 不代表订阅实际扣费. DeepSeek 按官方高峰和低谷时段计价. |
| Cursor | Cursor 返回的实际账单费用. |
| Grok | 优先使用 Grok Build 会话中记录的费用, 记录缺失或不完整时按模型价格估算. DeepSeek 回算同样区分高峰和低谷. |

没有可用费用记录且价格表未收录的模型, 仍统计 token, 成本按 `$0.00` 计入. Token 数量以 K, M, B 分别表示千, 百万和十亿.

## Cursor 页面

在本机 Cursor IDE 登录后, 打开 Cursor 页面即可查看额度和账单用量. 点击额度卡片左下角的页点或使用鼠标滚轮, 可切换 Monthly 与 Grok Bot Weekly, 并查看各自的重置时间. First party 和 APIs 额度在下方并排显示.

页面下方显示 Token Cost. LiteMonitor 和 TrafficMonitor 仅显示其中的 Monthly 额度.

## Grok 页面

在本机 Grok Build 登录后, 打开 Grok 页面即可查看 Weekly 剩余额度, 重置时间和各产品使用占比. 订阅类型显示在页面标题旁.

产品占比中的 `Others` 汇总其余产品, 鼠标悬停可查看明细. 重置时间可以在 Settings 中切换为倒计时或具体时间.

页面下方的 Token Cost 统计本机 Grok Build 会话中的用量和费用.

## API 监控

在 APIs 页点击右上角的添加按钮, 选择 provider 并填写对应信息, 再点击卡片右上角的保存按钮. API 监控会按刷新间隔自动更新, 也可以手动刷新. 隐藏 APIs 页后会停止 API 监控采集.

- DeepSeek: 填写 Base URL 和 API key, 默认 Base URL 为 `https://api.deepseek.com`. 卡片显示 CNY 余额, 第一张 DeepSeek 卡片也会显示在 LiteMonitor 和 TrafficMonitor 中.
- OpenRouter: 填写 Base URL 和 Management Key, 默认 Base URL 为 `https://openrouter.ai`. 卡片显示剩余与已用 credits, 普通 API key 不适用.
- Vercel: 填写 Base URL 和 AI Gateway API key, 默认 Base URL 为 `https://ai-gateway.vercel.sh`. 卡片显示团队剩余 credits 和累计已用量, 普通 Vercel 账号 token 不适用.
- NanoGPT: 填写 Base URL 和 API key, 默认 Base URL 为 `https://nano-gpt.com`. 卡片优先显示 USD 余额, 并在接口可用时显示最近 30 个 UTC 日的已用金额; 用量查询失败不会影响余额显示.
- NewAPI: 填写实例 Base URL, access token 和 User ID, 查看剩余与已用额度.

卡片支持自定义显示名称, 调整顺序和删除. Cursor 与 Grok 均使用独立页面, 不作为 API 监控卡片.

## 插件支持

CodexTray 支持 LiteMonitor 与 TrafficMonitor. 在 Settings 页找到对应监控器, 使用 `Browse` 手动选择目录或 `Auto detect` 自动定位, 然后点击 `Setup` 安装插件. 安装完成后重启对应监控器或重新加载插件.

两个插件都显示 `Codex`, `Cursor`, `Grok` 和 `DeepSeek` 四项, 分别对应 Codex Weekly, Cursor Monthly, Grok Weekly 和第一张 DeepSeek 卡片的 CNY 余额. 插件不显示重置时间, Token Cost 或其他 API provider 的余额. 有多张 DeepSeek 卡片时, 只使用 APIs 页中最靠前的一张.

隐藏 Codex, Cursor, Grok 或 APIs 页面后, 对应插件项会显示 `N/A`. 四个数据页全部隐藏后, 应用会停止向插件提供数据.

升级 CodexTray 或修改 HTTP 端口后, 请重新执行 `Setup`, 让插件文件和端口配置同步到监控器.

## 数据与隐私

- Codex, Cursor 和 Grok 的额度使用本机已有登录信息向各自的官方服务查询.
- Codex 和 Grok 的 Token Cost 从本机会话日志统计; Cursor 使用官方账单记录. Codex 和 Grok 页面不读取 OpenCode 会话.
- DeepSeek, OpenRouter, Vercel, NanoGPT 与 NewAPI 请求直接发送到卡片中配置的 Base URL. API key, Management Key, access token 和 User ID 以明文保存在 `CodexTray.exe` 同级目录的 `settings.json` 中.
- Cursor 和 Grok 的登录凭据会自动续期并写回各自的本地登录文件, 不会复制到 `settings.json`.
- 插件连接仅供本机访问, 默认端口为 `17890`, 不向局域网开放. 登录凭据不会写入日志, 插件配置或插件收到的数据.
- 应用设置保存在 `CodexTray.exe` 同级目录的 `settings.json`.

## 常见问题

### 为什么额度显示 N/A

请确认当前 Windows 用户已登录 Codex, 并且网络可以访问 ChatGPT. 必要时重新登录 Codex, 然后点击 `Refresh Now`.

### 为什么 Token Cost 显示 N/A

请确认已完整解压发布包, 保留 `Resources/model-pricing.json`, 并且本机有对应的 Codex 或 Grok Build 会话记录. Grok 会话需要包含已完成轮次的用量记录. Cursor 的 Token Cost 需要能正常获取账单记录.

### 为什么 API 卡片显示 N/A

将鼠标悬停在卡片名称或状态圆点上查看错误信息. DeepSeek, OpenRouter, Vercel, NanoGPT 和 NewAPI 需要有效的 Base URL 与凭据, NewAPI 还需要 User ID, OpenRouter 必须使用 Management Key, Vercel 必须使用 AI Gateway API key.

### 为什么 Cursor 页面显示 N/A

请确认本机 Cursor IDE 已登录. 自动续期失败时, 请回到 Cursor IDE 重新登录. 额度与 Token Cost 可以独立更新, 部分数据不可用时, 其余部分仍可能正常显示.

### 为什么 Grok 页面显示 N/A

请先在 Grok Build 中登录. 自动续期失败时, 请运行 `grok login` 重新登录.

### 为什么 LiteMonitor 或 TrafficMonitor 没有更新

请确认 CodexTray 正在运行, 且 Settings 中 Codex, Cursor, Grok 或 APIs 至少一个页面可见. 在托盘菜单中点击 `Refresh Now`, 再检查监控器路径并重新执行 `Setup`. 升级后或修改过 HTTP 端口时, 必须重新安装插件.

### 为什么找不到 LiteMonitor 或 TrafficMonitor

自动检测会搜索本机磁盘中的 `LiteMonitor.exe` 或 `TrafficMonitor.exe`. 也可以使用 `Browse` 直接选择包含对应可执行文件的目录.

### 为什么托盘图标没有直接显示在任务栏

Windows 负责管理托盘图标的可见区域. 请在系统托盘展开区或 Windows 的任务栏设置中调整 CodexTray 的显示状态.
