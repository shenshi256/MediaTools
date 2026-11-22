# MediaDown

一个 B站 / 抖音 / 今日头条 / 腾讯视频 / YouTube 视频下载器（Windows / WPF / .NET 10），MVP 版本。

## 快速开始

1. 运行 `.\MediaTools.exe`。这是**单文件自包含**发布，
   **目标机器不用装 .NET**（运行时已打进 exe）。`tools` 目录是随包的下载引擎，
   必须和 exe 放在同一目录。
2. 粘贴视频链接 / BV 号 / av 号 → 点「解析」。
3. 选分 P 和清晰度 → 点「下载」。

发布产物结构（`.\`）：

```
MediaTools.exe    ← 单文件主程序（自带 .NET 运行时）
tools\
  ffmpeg\ffmpeg.exe        音视频合并（官方 4.1.1 静态构建）
  ffmpeg\ffprobe.exe
  node\node.exe            yt-dlp 的 EJS 解密运行时（22.22.2）
  yt-dlp\yt-dlp.exe        YouTube 下载引擎
```

默认路径都以 **exe 所在目录**为基准（把整个 . 文件夹拷到别处也一样生效）：

- 下载目录：`<exe目录>\down`（首次启动自动创建）
- ffmpeg：`<exe目录>\tools\ffmpeg\ffmpeg.exe`，已随包
- yt-dlp：`<exe目录>\tools\yt-dlp\yt-dlp.exe`（YouTube 下载用）
- node：`<exe目录>\tools\node\node.exe`（yt-dlp 的 EJS 解密运行时）
- ffmpeg 自动查找顺序：设置指定的路径 → `<exe目录>\ffmpeg.exe` → `tools\ffmpeg\ffmpeg.exe` → `tool\ffmpeg.exe` → `ffmpeg\ffmpeg.exe` → PATH

## 登录

未登录只能下载 360P/480P。登录方式：

- **推荐：扫码登录**。点顶部登录按钮，用 B站手机客户端扫码。走的是 TV 端扫码接口，**密码不经过本程序，也不经过任何第三方服务器**。
- **备选：手动粘贴 SESSDATA**。在浏览器登录 B站后，从 Cookie 里复制 `SESSDATA`，粘到设置的输入框里点「加密保存」。

凭证用 Windows DPAPI 加密后存在 `%APPDATA%\MediaDownloader\credential.bin`，绑定当前 Windows 账户，**不做明文落盘**。

**随包三件套**（都在 `tools\` 下）：

| 文件 | 作用 | 大小 |
|---|---|---|
| `yt-dlp\yt-dlp.exe` | 下载引擎（官方 PyInstaller 版，已内置 yt-dlp-ejs 脚本） | 23MB |
| `node\node.exe` | EJS 解密的 JS 运行时（22.22.2，yt-dlp 要求 ≥22） | 84MB |
| `ffmpeg\ffmpeg.exe` | 音视频合并（复用既有的） | 62MB |


## 边界

本项目只下载**当前账号本身有权访问的公开内容**，依靠用户自己的 Cookie 鉴权。不包含、也不会加入任何绕过付费 / 大会员 / DRM 鉴权的能力，也不做批量爬取或分发。

腾讯视频这条线把这条边界落成了代码：`TencentParser.ParsePlayInfo` 先看 `exem` 和 `vi.drm`，
任意一个非 0 就直接判为不可下载并且**不去拼地址**——不是下载完发现失败，是根本不请求。
