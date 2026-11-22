# MediaDown

一个 B站 / 抖音 / 今日头条 / 腾讯视频 / YouTube 视频下载器（Windows / WPF / .NET 10），MVP 版本。
解决方案 `MediaDown.sln`；主程序与类库命名为 `MediaDownloader.*`。

## 快速开始

1. 运行 `publish-single\MediaDownloader.App.exe`。这是**单文件自包含**发布，
   **目标机器不用装 .NET**（运行时已打进 exe）。`tools` 目录是随包的下载引擎，
   必须和 exe 放在同一目录。
2. 粘贴视频链接 / BV 号 / av 号 → 点「解析」。
3. 选分 P 和清晰度 → 点「下载」。

发布产物结构（`publish-single\`）：

```
MediaDownloader.App.exe    ← 单文件主程序（自带 .NET 运行时）
tools\
  ffmpeg\ffmpeg.exe        音视频合并（官方 4.1.1 静态构建）
  ffmpeg\ffprobe.exe
  node\node.exe            yt-dlp 的 EJS 解密运行时（22.22.2）
  yt-dlp\yt-dlp.exe        YouTube 下载引擎
```

默认路径都以 **exe 所在目录**为基准（把整个 publish-single 文件夹拷到别处也一样生效）：

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

## 抖音

左侧「数据下载 → 抖音」。支持 `douyin.com/video/…`、`v.douyin.com/…` 短链、分享口令（整段带中文也行）和纯数字作品 ID。
一次解析一个视频，可选多档清晰度（1080P/720P/540P…，按码率细分）。

**为什么抖音需要一个内嵌浏览器**：抖音的详情接口带 Argus 反爬，请求必须携带 `a_bogus` 签名，
而签名由页面里的混淆 JS 算出。与其自己实现一套随时会失效的算法，不如让真实页面去发请求、
我们只拦它的响应体。所以解析时会在屏幕外起一个 WebView2 打开视频页，拿到 JSON 后剩下的
全部是纯 C# 解析（`Core/DouyinParser.cs`）加现有的多线程下载引擎。

代价是每次解析要多等 2~5 秒让页面跑起来；换来的是抖音改签名也不影响本工具。

- 登录：点「未登录」。同样走**扫码**，没有账号密码 / 手机号登录。登录页就是上面那个内嵌浏览器，
  扫完自动收起。
- 登录态存在 `%APPDATA%\MediaDownloader\douyin-profile`（WebView2 的 cookie 库），跨启动复用，
  不额外导出 cookie。「退出登录」清空该浏览器的 cookie。
- 依赖 **WebView2 运行时**（Win10/11 一般随 Edge 自带）。缺失时解析会直接报错提示。
- 图文作品（图集）暂不支持，会给出明确提示。

## 今日头条

左侧「数据下载 → 今日头条」，就在抖音下面。支持 `toutiao.com/video/…`、`toutiao.com/article/…`、
`m.toutiao.com/i<数字>/`、`m.toutiao.com/is/…` 短链、整段分享文案，以及纯数字条目 ID。
一条视频通常有 360P / 480P / 720P（有时到 1080P）几档，带文件大小和码率。

**和抖音相反，头条不需要内嵌浏览器就能解析**。实测下来这套链路纯 HTTP 全通：

1. `m.toutiao.com/i{条目ID}/info/` —— 元数据，没有签名校验，直接 GET；
2. 响应里的 `play_auth_token_v2` 是 base64 包了一层的 AWS SigV4 预签名查询串，剥开后直接拼到
   `vod.bytedanceapi.com` 上调用 VOD 的 `GetPlayInfo`，拿回清晰度列表和真实播放地址。

也就是说单条解析大约 **1 秒**（抖音是 2~5 秒）。解析逻辑全在 `Core/ToutiaoParser.cs`，纯字符串处理，
可以脱网测试。

- 登录：点「未登录」按钮，打开的是头条网页版，**扫码登录**，没有账号密码 / 手机号入口。
  登录态存在 `%APPDATA%\MediaDownloader\toutiao-profile`，和抖音的 profile 完全分开。
- 登录不是下载的必要条件：公开视频未登录也能拿全档清晰度。登录后的 cookie 会带进解析请求，
  用于兜住受限内容。「退出登录」清空该浏览器的 cookie。
- 同样依赖 **WebView2 运行时**。切到这一页时会启动一次内嵌浏览器读登录态，
  解析本身不走浏览器。
- 图文 / 微头条不支持，会提示「该链接不是视频内容」。

## 腾讯视频

左侧「数据下载 → 腾讯视频」，导航项上带一个「仅免费」标签。支持
`v.qq.com/x/cover/{cid}/{vid}.html`、`v.qq.com/x/cover/{cid}.html`、
`v.qq.com/x/page/{vid}.html` 和裸 vid。

**这个平台和前面三个的性质不一样，先说清楚**：

- **只做免费内容。** 会员剧集、4K/HDR 档、任何 DRM 加密的流一律不下载，也不做绕过。
  列表里照样列出来，但勾选框是灰的，状态列写明「需会员，本工具不下载」。
- **未登录最高 720P。** 腾讯的清晰度表里会列出 1080P，但未登录请求它只给 720P，
  所以解析阶段就比对 `vi.br` 把拿不到的档位过滤掉——列表上看到的档位都是真能下的。
- 不需要账号，不需要内嵌浏览器，不落任何 cookie。整条链路纯 HTTP。

### 为什么它比抖音简单

抖音要内嵌浏览器拦响应，是因为详情接口带 Argus 签名。腾讯这边实测下来两件事都不需要：

1. `float_vinfo2?cid=xxx` 拿专辑元数据和 `video_ids`（62 集的短剧一次给全）；
2. `getinfo?vids=xxx&platform=101001&charge=0&defn=fhd` 拿清晰度和直链——
   **不需要 ckey 签名，不需要登录**，裸 GET 就返回明文 JSON。

返回的还是**完整 mp4 直链**（`dltype=1`、`fn` 以 `.mp4` 结尾、`cl.fc=0`），不是 m3u8 分片，
所以不用 HLS 引擎，现有的 `DownloadEngine` 分段下载直接就能用。

一集 62 集的短剧解析约 **2.5 秒**（每集一次 getinfo，6 路并发），下载 480P 一集约 3.4 秒。

### 两个坑，都在代码里处理了

- **一份 getinfo 只给一个档位的文件名。** `fn` 里的 `.f622`/`.f632` 是内部档位号，
  和 `fl.fi[].id` 对不上，没法靠替换推出来。所以解析阶段只登记档位元数据，
  真正下载时按 `formatdefn` 再请求一次（`TencentClient.FetchStreamAsync`）。
- **vkey 有时效**（约数小时）。所以地址是下载前现取的，不是解析时缓存的那份。

另外腾讯有些接口的 `Content-Type` 里 charset 是非法值，直接 `ReadAsStringAsync` 会抛
"character set provided in ContentType is invalid"，所以统一按 UTF-8 解码字节数组。

## YouTube

左侧「数据下载 → YouTube」。粘贴 `youtube.com/watch?v=…`、`youtu.be/…` 短链，
解析出格式列表（分辨率 + 编码 + 大小），选一个下载。

**和前面四个平台彻底不同的做法**：下载不经过自己的 DownloadEngine，而是**随包
yt-dlp 一条命令干完**（解析签名 + 下载分段 + 音视频合并）。原因很简单——YouTube 的
反爬是 Google 每周都在改的活靶子：

- **n 参数解密**：流地址带一个要跑 JS 才能解开的参数，不解会被限速到几 KB/s；
- **EJS 挑战**：yt-dlp 需要 JS 运行时（node）来解 n 参数；
- **PoToken / SABR / 客户端切换**：这些自己逆向等于追着一个每周换锁的对手跑。

所以把脏活全交给 yt-dlp，本工具只做「调进程 + 解析 JSON + 进度展示」。yt-dlp 改版
时只需替换 `tools\yt-dlp\yt-dlp.exe` 这一个文件。

**随包三件套**（都在 `publish\tools\` 下）：

| 文件 | 作用 | 大小 |
|---|---|---|
| `yt-dlp\yt-dlp.exe` | 下载引擎（官方 PyInstaller 版，已内置 yt-dlp-ejs 脚本） | 23MB |
| `node\node.exe` | EJS 解密的 JS 运行时（22.22.2，yt-dlp 要求 ≥22） | 84MB |
| `ffmpeg\ffmpeg.exe` | 音视频合并（复用既有的） | 62MB |

**网络是硬前提，且本工具不碰代理**。yt-dlp 走系统代理（环境变量 / 系统设置），
本机网络能访问 YouTube 就能下，不能就连不上、报错提示——代理是用户自己的事，
不在工具职责内，避免越界。

**格式选择**：列表里「仅视频」的流（DASH 下常见）会标出来；选它下载时自动追加
`+bestaudio` 让 yt-dlp 配音轨并合并成 mp4。纯音频流、音画合一的老流直接下。

**Cookie（可选，登录态）**：YouTube 页点「Cookie 设置」展开粘贴框，把浏览器扩展
「Get cookies.txt LOCALLY」导出的 Netscape 格式 Cookie 整个粘贴进去保存（点旁边的
「图文指导」按钮有从装扩展、导出到粘贴的完整步骤截图）。Cookie 以
**明文**存到程序目录 `youtube-cookies.txt`——不加密，因为它不含账号密码、只是浏览器
可导出的登录态文本，且 yt-dlp 也只认明文文件。**不配 Cookie 也能下公开视频**，
配置后能解析会员 / 年龄限制等需要登录的内容。清空即删除文件回到匿名下载。

**已知限制**：
- 需要本机能访问 YouTube（代理自理）。
- 需要登录的受限视频（年龄限制 / 会员 / 私享）要配 Cookie 才能下，见上。
- 播放列表 / 频道批量下载不支持，一次一个视频（`--no-playlist` 已固定）。
- yt-dlp 更新快，遇到 403 先换最新的 yt-dlp.exe。

## 与唧唧的做法差异

| 项目 | 唧唧 | 本项目 |
|---|---|---|
| HTTPS 证书校验 | 全局无条件放行 | 不碰 `ServerCertificateValidationCallback` |
| 远程控制 | `control.txt` kill switch | 无 |
| 第三方统计 | CNZZ 埋点 | 无 |
| 密码 | 明文 POST 到自有服务器换哈希 | 只做扫码登录，密码不出本机 |
| Cookie 存储 | 明文落盘 | B站/抖音/头条登录态 DPAPI 加密；YouTube Cookie 明文（见上） |
| 互助代解析 | 失败时把请求 URL 发到第三方 TCP 服务器 | 无 |
| 自动更新 | 下包 → 写 .bat → 执行 | 无（手动更新） |
| 代码保护 | .NET Reactor 加壳 | 不加壳 |

## 架构

```
MediaDownloader.Core     接口客户端 / 下载引擎 / 合并 / 登录 / 凭证存储
  BilibiliClient      HttpClient 封装、buvid 初始化、WBI 签名、view / playurl 解析
  DouyinClient        短链 302 展开（详情靠内嵌浏览器拦响应，见下）
  DouyinParser        aweme_detail 响应解析（纯静态，可脱网测试）
  ToutiaoClient       纯 HTTP：info 接口 + VOD GetPlayInfo
  ToutiaoParser       info / PlayInfo 响应解析、播放 token 拆包（纯静态）
  TencentClient       纯 HTTP：float_vinfo2 专辑 + getinfo 逐集按档位取址
  TencentParser       专辑 / 播放信息解析、免费与 DRM 判定（纯静态）
  YoutubeClient       yt-dlp 进程封装：-J 解析格式表、下载 + 进度解析
  DownloadEngine      多线程分段（Range）、断点续传、限速、暂停
  MediaMerger         ffmpeg -c copy 合并 DASH 音视频
  LoginService        TV 端扫码登录
  CredentialStore     DPAPI 加密存储
MediaDownloader.App      WPF 界面（MVVM，未使用第三方 UI 框架）
  Services\DouyinBrowser    WebView2：拦详情接口 + 扫码登录
  Services\ToutiaoBrowser   WebView2：仅扫码登录，导出 cookie 给 HttpClient
tools\Probe           命令行冒烟测试
tools\MediaCli        媒体功能 + 各平台解析回归（含脱网 fixture 断言）
```

## 构建

目标框架是 **net10.0 / net10.0-windows**。注意 `MediaDown.sln` 只含 Core 和 App，
`tools` 下的三个项目要单独编。

```bash
dotnet build MediaDown.sln -c Release

# 命令行验证器（tools 不在 sln 里，单独编）
dotnet build tools/MediaCli/MediaCli.csproj -c Release
dotnet tools/MediaCli/bin/Release/net10.0/MediaCli.dll                # 脱网回归
dotnet tools/MediaCli/bin/Release/net10.0/MediaCli.dll --net-tencent "<链接>"  # 联网验证
```

### 发布（单文件自包含）

双击 `publish-single.bat`，或手动执行等价命令：

```bash
dotnet publish MediaDownloader.App/MediaDownloader.App.csproj -c Release \
  -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o publish-single
```

然后把随包工具拷进输出目录（脚本会自动做）：

```bash
xcopy /e /i /q tools\yt-dlp publish-single\tools\yt-dlp
xcopy /e /i /q tools\node   publish-single\tools\node
xcopy /e /i /q tools\ffmpeg publish-single\tools\ffmpeg
```

**为什么单文件也要带 tools 目录**：yt-dlp / node / ffmpeg 是独立原生程序，不是 .NET
程序集，塞不进单文件 exe，只能随包外置。所以最终交付是「一个自包含 exe + tools 目录」。

**关于早期记录的 `LsDisableSpecialCharacterLigature` 崩溃**：那是 .NET 10 刚发布时
WPF 单文件的 bug（`HwndTarget` 构造时原生绑定失败），当时的结论是走框架依赖发布。
**该 bug 已在 .NET 10.0.9 运行时修复**（2026-11 实测：单文件 exe 能正常起窗口、渲染、
响应）。所以现在恢复单文件发布，`IncludeNativeLibrariesForSelfExtract=true` 是关键——
它让 WPF 的原生库（`wpfgfx_cor3.dll`、`PresentationNative_cor3.dll` 等）内嵌进 exe，
启动时自解压，避免走框架依赖那套外置 dll。

## 已验证（冒烟测试 `tools/Probe`，真实 B站接口）

| 环节 | 结果 |
|---|---|
| view 解析（WBI 签名） | 通过 |
| playurl 清晰度列表（未登录） | 480P / 360P（DASH） |
| 分段下载（Range + Referer） | 通过，多段并发约 2.7 MB/s |
| 限速 / 暂停 / 续传 | 通过，生成 `.jjprog` 侧车文件 |
| ffmpeg 合并 | 通过，输出 HEVC 视频 + AAC 音频，时长 340s / 16.3 MB |

## 多分 P 与重复文件

- 分 P 是勾选列表，支持「全选 / 清空」，选中几个就下几个，任务**串行排队**执行（一个下完自动开始下一个）。
- 点下载前会先检查下载目录：若目标文件名已存在，默认弹窗询问。
  - **全部下载**：覆盖同名文件重新下载
  - **跳过**（默认，回车即选）：只下载其余分 P
  - **取消**：什么都不做
- 也可以在设置里把「文件已存在」固定为「直接跳过，不再询问」或「直接覆盖重新下载」。

## 视频压缩

左侧「视频工具 → 视频压缩」（音视分离下面）。把已有视频重编码到更小体积，界面按三步走：
加文件（可拖拽、可多选）→ 选参数 → 入队。

三种模式：

- **CRF 质量优先（推荐）**：默认 H.265 + CRF 24 + medium，画质三档（22 视觉无损 / 24 推荐 / 26 较小）。
  同档位 x264 会自动换算到 21（两个编码器 CRF 刻度不同）。
- **目标体积**：填"这个文件必须小于多少 MB"，按片长反推码率跑 2Pass 逼近，音频固定 AAC 96k。
- **自定义参数**：原样追加 ffmpeg 参数，双引号里的空格不切分，给高级用户。

其他固定行为：分辨率**保持原值**（降分辨率是唯一肉眼必查的降级，不提供）；
试压按钮取片长约 1/4 处的 30 秒先压出来看效果（避免压坏全片）；
队列串行执行；完成后显示「原大小 → 新大小（省 X%）」。

**对"平台下载源"要有预期**：B站/抖音/头条的 mp4 已经被平台压过一轮，
CRF 24 的 x265 再压可能不降反升（回归里实测过：9.8 MB 低码率源压出 11.6 MB）。
高码率片源才有可观收益——实测 44.6 MB 的高码率源压到 11.6 MB（省 74%）。
所以先试压再全片。

## 日志

默认**关闭**。设置页「日志 → 记录日志」打开后，运行日志按天写到 exe 目录的
`logs\yyyy-MM-dd.log`（解析 / 下载 / 登录等关键动作，格式 `HH:mm:ss.fff [级别] [来源] 内容`）。
改开关即时生效，不用重启；排查完关掉即可，删掉整个 `logs` 目录也不会影响任何数据。

崩溃兜底日志永远保留在 `%APPDATA%\MediaDownloader\app.log`，不受这个开关影响——
程序遇到未处理异常时，详细信息写在那里。

实现是 `Core/Log.cs`，约 60 行手写（全局锁 + 每日文件 + 全部吞异常），没有引入
log4net / NLog 之类的框架。

## 已知限制（MVP）

- 暂不支持收藏夹、番剧、直播批量解析（单个视频的多分 P 已支持）。
- 断点续传依赖同名的 `.jjprog` 侧车文件，删除后重新开始。
- 暂停后点「继续」会重新请求流地址再续传；若地址已过期会自动重新获取。
- 未做任务持久化（关闭程序后任务列表清空，已下载的文件保留）。
- 字幕 / 弹幕、自动更新、限速队列尚缺。
- 抖音 / 头条的图文作品（图集）不支持。
- 头条的播放地址带时效（`UrlExpire`，约 24 小时）。解析后放太久再点下载会失败，重新解析即可。
- **腾讯只下免费内容**：会员剧集、4K/HDR、DRM 加密流一律不下载；未登录最高 720P。
- 腾讯的剧集列表只覆盖「一部剧一个 cover」的情况。合集页、频道页、搜索页不解析。

## 边界

本项目只下载**当前账号本身有权访问的公开内容**，依靠用户自己的 Cookie 鉴权。不包含、也不会加入任何绕过付费 / 大会员 / DRM 鉴权的能力，也不做批量爬取或分发。

腾讯视频这条线把这条边界落成了代码：`TencentParser.ParsePlayInfo` 先看 `exem` 和 `vi.drm`，
任意一个非 0 就直接判为不可下载并且**不去拼地址**——不是下载完发现失败，是根本不请求。
