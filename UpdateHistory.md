# 更新历史

> 每次功能更新追加到文件顶部「未发布」区，发布时归入对应版本号。版本号只改 `MediaDownloader.App/MediaDownloader.App.csproj` 里的 `<Version>` 一处，其余（程序集版本、文件版本、日志里的 `v` 号）自动跟随。

## v1.0.0（2026-11）

初始发布版本。

- **YouTube 下载**：随包 yt-dlp 一条命令完成（解析签名 + 下载 + 合并），支持格式选择、音视频分离流自动 `+bestaudio` 合并。
- **YouTube Cookie（可选登录态）**：明文存 `youtube-cookies.txt`，界面「Cookie 设置」粘贴保存，带「图文指导」弹窗（从装扩展到导出复制）。不配 Cookie 也能下公开视频，配置后能过「不是机器人」验证、下会员/年龄限制内容。
- **腾讯视频下载**：纯 HTTP 免登录下载免费内容，会员/DRM 内容在解析期拦截，未登录最高 720P。
- **应用图标**：新增 `Assets/app.ico`，修复任务栏黑色方块；窗口与 exe 图标统一。
- **单文件发布**：新增 `publish-single.bat`，产出自包含 exe（自带 .NET 10 运行时，目标机免装）+ tools 目录（yt-dlp/node/ffmpeg）。
- **版本号**：`csproj` 加 `<Version>1.0.0</Version>`，日志里的启动版本号自动跟随。

## 待发布（未归入版本号）

（暂无）
