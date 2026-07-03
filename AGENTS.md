# CodexMonitor Agent Guide

## 目录

- [项目概览](#项目概览)
- [语言与文风](#语言与文风)
- [目录结构](#目录结构)
- [构建与输出](#构建与输出)
- [开发规则](#开发规则)
- [验证命令](#验证命令)
- [发布流程](#发布流程)
- [注意事项](#注意事项)

## 项目概览

`CodexMonitor` 是一个 C#/.NET Windows 托盘应用, 用于读取 Codex OAuth 凭据并请求 ChatGPT 官方额度接口, 然后通过本地 HTTP 服务向 LiteMonitor 插件提供 Codex 额度显示数据. 当 OAuth 凭据不存在时, 会回退读取 `~/.codex/sessions/**/*.jsonl` 中的 `token_count` 事件.

## 目录结构

- `CodexMonitor.Core`: 官方额度采集, 本地 session 兜底采集, 使用量缓存, HTTP 服务, 设置存储, LiteMonitor 定位, 插件安装, Windows 自启动管理.
- `CodexMonitor.App`: WinForms 托盘应用和设置窗口.
- `CodexMonitor.Tests`: 自包含 C# 测试运行器.
- `Plugins/LiteMonitor`: LiteMonitor 插件定义, 当前插件文件为 `CodexMonitor.json`.
- `Builds`: 发布产物目录, 只提交 `.gitkeep`, 其余内容由 `.gitignore` 忽略.
- `Directory.Build.props` 和 `Directory.Build.targets`: 全局 MSBuild 默认配置和默认编译项排除规则.

## 构建与输出

- `bin` 和 `obj` 使用各项目默认位置, 即 `{ProjectDir}/bin` 和 `{ProjectDir}/obj`, 由 `.gitignore` 的 `bin/` 和 `obj/` 规则过滤.
- 不把 `bin`, `obj` 重定向到 `Builds/` 下, 避免 Rider/ReSharper 找不到 `obj` 里的隐式生成文件而误报.
- `Builds/` 仅存放发布脚本通过 `-o` 显式输出的发布产物, 由脚本自行管理路径.
- `Configuration` 为空时默认按 `Debug` 处理.
- 不要把 `bin`, `obj`, `Builds` 下的任何生成文件提交到仓库.
- `Directory.Build.targets` 排除 `Builds/**` 下的 `.cs`, 防止发布产物里的 generated `.cs` 被 SDK 默认编译项重新纳入编译.

## 开发规则

- 修改 C# 代码时, namespace 必须与项目文件夹名对齐, 即 `CodexMonitor.Core`, `CodexMonitor.App`, `CodexMonitor.Tests`.
- LiteMonitor 插件文件名应保持为 `Plugins/LiteMonitor/CodexMonitor.json`.
- 如果修改内置插件 JSON, 同步检查 `CodexMonitor.Core/LiteMonitorPluginInstaller.cs` 中的 `PluginJson`.

## 验证命令

构建全部项目:

```powershell
dotnet build .\CodexMonitor.sln -m:1
```

运行测试:

```powershell
dotnet run --project .\CodexMonitor.Tests\CodexMonitor.Tests.csproj
```

发布托盘应用:

```powershell
.\Scripts\Publish-App.ps1
```

修改托盘应用后, 验证通过时需要发布并重启预览程序:

```powershell
.\Scripts\Restart-App.ps1
```

如果需要从资源管理器双击运行, 使用 `Scripts/Publish-App.cmd` 或 `Scripts/Restart-App.cmd`, 结束后窗口会停留显示结果.

打包 GitHub Release 上传文件:

```powershell
.\Scripts\Package-Release.ps1 -Version 0.1.0
```

输出文件名格式为 `Builds/Release/CodexMonitor-vX.Y.Z-win-x64.zip`.

## 发布流程

- 当用户要求打 tag 或发布新版本并提供版本号时, 按本节自动完成后续流程.
- 版本号支持 `X.Y.Z` 或 `vX.Y.Z`, tag 固定使用 `vX.Y.Z`, 上传文件固定使用 `Builds/Release/CodexMonitor-vX.Y.Z-win-x64.zip`.
- 发布前先检查 `git status --short --branch`, 确认本次待提交内容只包含发布相关修改.
- 如有发布脚本, 文档, 图标资源, 或应用打包行为变更, 先提交并推送这些源码修改. 不要提交 `Builds/Debug`, `Builds/Release`, 或 zip 产物.
- 执行 `.\Scripts\Package-Release.ps1 -Version X.Y.Z -NoPause` 生成 zip, 并确认 zip 文件存在.
- 在已推送的最终发布提交上执行 `git tag -a vX.Y.Z -m "vX.Y.Z"`, 再执行 `git push origin vX.Y.Z`.
- 使用 GitHub CLI 创建 release 并上传 zip:

```powershell
gh release create vX.Y.Z `
  "Builds\Release\CodexMonitor-vX.Y.Z-win-x64.zip" `
  --title "CodexMonitor vX.Y.Z" `
  --notes "Release vX.Y.Z." `
  --verify-tag
```

- 如果对应 tag 或 release 已存在, 先停止并说明当前状态, 不要覆盖 tag. 替换 release asset 需要用户明确同意后才使用 `gh release upload --clobber`.
- 如果 `git push` 被 non-fast-forward 拒绝, 停止流程并交给用户决定是否 rebase 或 merge.

## 注意事项

- 默认 HTTP 服务只监听 `127.0.0.1`, 不要无意改成局域网可访问地址.
- 项目会读取 `~/.codex/auth.json` 中的 Codex OAuth token, 仅用于请求 `https://chatgpt.com/backend-api/wham/usage`. 不要在日志, 本地 HTTP 响应, README 示例, 或插件 JSON 中暴露 access token.
- 修改插件输出字段时, 同步检查 README 的 HTTP API 示例和 LiteMonitor 插件提取路径.
- Windows 沙箱环境可能阻止 `dotnet` 写入或删除 `Builds` 下的生成文件. 遇到这种情况时, 使用受控提权重新执行验证或清理命令.
