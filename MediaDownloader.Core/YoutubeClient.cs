using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MediaDownloader.Core;

/// <summary>
/// YouTube 下载，全靠随包的 yt-dlp.exe。这里不自己碰 innertube 接口、
/// 不碰 n 参数解密、不碰 PoToken——那些是 Google 每周都在改的反爬，
/// 交给 yt-dlp 去追。本类只做两件事：调 yt-dlp 拿格式列表、调 yt-dlp 下载。
///
/// 网络方面：**不做任何代理配置**。yt-dlp 会走系统代理（环境变量 / 系统设置）。
/// 用户没代理就连不上，报错提示即可——代理是用户自己的事，不在本工具职责内。
/// </summary>
public sealed class YoutubeClient : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _ytdlpPath;
    private string? _nodePath;

    public YoutubeClient()
    {
        // 构造时就定位好两个可执行文件，避免每次调用都找一遍
        _ytdlpPath = LocateYtDlp();
        _nodePath = LocateNode();
    }

    public void Dispose() => _lock.Dispose();

    public bool IsAvailable => _ytdlpPath is not null;

    public string AvailabilityHint => _ytdlpPath is null
        ? "未找到 yt-dlp.exe。请把它放到程序目录的 tools\\yt-dlp\\yt-dlp.exe"
        : "";

    // ---------- Cookie ----------

    /// <summary>
    /// YouTube 登录态 cookie 文件，明文 Netscape 格式，存在程序目录，文件名叫
    /// youtube-cookies.txt。**可选**：没有它也能下公开视频，只是下不了会员/年龄限制内容。
    /// 不加密的原因：cookie 就是一段可导出的浏览器文本，不涉及账号密码；
    /// yt-dlp 也只认明文文件，加密了还得解密写临时文件，反而多此一举。
    /// </summary>
    private static readonly string CookieFileName = "youtube-cookies.txt";

    private static string? _cookiePath;

    /// <summary>返回 cookie 文件绝对路径（存在才返回，否则 null）。</summary>
    public static string? CookiePath
    {
        get
        {
            if (_cookiePath is not null) return _cookiePath;
            var p = Path.Combine(AppContext.BaseDirectory, CookieFileName);
            _cookiePath = File.Exists(p) ? p : null;
            return _cookiePath;
        }
    }

    /// <summary>
    /// 保存用户粘贴的 Netscape 格式 cookie 到程序目录 youtube-cookies.txt。
    /// 传空或 null 表示清空（删除文件）。返回保存后的状态文案。
    /// </summary>
    public static string SaveCookie(string? content)
    {
        var path = Path.Combine(AppContext.BaseDirectory, CookieFileName);
        if (string.IsNullOrWhiteSpace(content))
        {
            if (File.Exists(path)) File.Delete(path);
            _cookiePath = null;
            return "未配置 Cookie（匿名下载，仅公开视频）";
        }

        var text = content.Trim();
        // 去掉可能被粘贴进来的多余首尾空白行
        File.WriteAllText(path, text + Environment.NewLine, new UTF8Encoding(false));
        _cookiePath = path;
        return "已保存 Cookie，会员/年龄限制视频可用登录态解析";
    }

    /// <summary>追加 --cookies 参数（当 cookie 文件存在时）。</summary>
    private static void AppendCookieArgs(List<string> args)
    {
        if (CookiePath is { } p)
        {
            args.Add("--cookies");
            args.Add(p);
        }
    }

    // ---------- 定位可执行文件 ----------

    /// <summary>yt-dlp.exe：优先随包位置，其次 PATH，最后往上层目录找 tools\yt-dlp（源码运行时）。</summary>
    private static string? LocateYtDlp()
    {
        var exe = "yt-dlp.exe";
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, exe),
                     Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp", exe),
                     Path.Combine(AppContext.BaseDirectory, "tool", exe)
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        // 源码运行时（如 dotnet run tools/MediaCli）：从 bin 目录往上找项目根的 tools\yt-dlp
        try
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && dir is not null; i++)
            {
                var candidate = Path.Combine(dir, "tools", "yt-dlp", exe);
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent is null) break;
                dir = parent.FullName;
            }
        }
        catch { /* 忽略 */ }

        // PATH 里找
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(p.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 忽略非法 PATH 项 */ }
        }
        return null;
    }

    /// <summary>
    /// node.exe：yt-dlp 的 EJS 解密（n 参数）需要 JS 运行时，没有它很多视频
    /// 只剩图片缩略图格式、下不动真流。优先随包，其次往上找 tools\node，最后 PATH。
    /// </summary>
    private static string? LocateNode()
    {
        var exe = "node.exe";
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "tools", "node", exe),
                     Path.Combine(AppContext.BaseDirectory, exe)
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        try
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && dir is not null; i++)
            {
                var candidate = Path.Combine(dir, "tools", "node", exe);
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent is null) break;
                dir = parent.FullName;
            }
        }
        catch { /* 忽略 */ }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(p.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 忽略 */ }
        }
        return null;
    }

    // ---------- 解析 ----------

    /// <summary>
    /// 解析一个链接，返回标题 + 时长 + 格式列表。
    /// 传 --no-download -J 让 yt-dlp 只吐 JSON 元数据，不下载。
    /// </summary>
    public async Task<YoutubeVideo> FetchAsync(string url, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            EnsureAvailable();

            var args = new List<string> { "--no-warnings", "--no-download", "-J" };
            AppendCookieArgs(args);
            args.Add(url);

            var (exitCode, stdout, stderr) = await RunAsync(args, ct);

            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                var reason = ExtractError(stderr);
                throw new YoutubeException(reason);
            }

            return ParseJson(stdout, url);
        }
        finally { _lock.Release(); }
    }

    /// <summary>把 yt-dlp -J 的输出解析成 <see cref="YoutubeVideo"/>。</summary>
    public static YoutubeVideo ParseJson(string json, string url)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new YoutubeException("yt-dlp 返回的不是合法 JSON：" + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;

            var id = GetString(root, "id");
            if (string.IsNullOrEmpty(id))
                id = ExtractVideoId(url);

            var title = GetString(root, "title");
            var channel = GetString(root, "channel");
            var thumbnail = GetString(root, "thumbnail");
            var duration = GetDouble(root, "duration") ?? 0;

            var formats = new List<YoutubeFormat>();
            if (root.TryGetProperty("formats", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    var ext = GetString(f, "ext");
                    // storyboard 图集、manifest 等不是真视频流，跳过
                    if (ext is "mhtml" or "json") continue;

                    var vcodec = GetString(f, "vcodec");
                    var acodec = GetString(f, "acodec");
                    var height = (int)(GetDouble(f, "height") ?? 0);

                    // 空视频 + 空音频（纯图片/占位）跳过
                    if (vcodec is "none" or "" && acodec is "none" or "" && height == 0)
                        continue;

                    formats.Add(new YoutubeFormat
                    {
                        FormatId = GetString(f, "format_id"),
                        Ext = ext,
                        Height = height,
                        Width = (int)(GetDouble(f, "width") ?? 0),
                        VCodec = vcodec,
                        ACodec = acodec,
                        AudioBitrate = GetDouble(f, "abr") ?? 0,
                        SampleRate = (int)(GetDouble(f, "asr") ?? 0),
                        FileSize = (long)(GetDouble(f, "filesize") ?? 0)
                    });
                }
            }

            return new YoutubeVideo
            {
                VideoId = id,
                Title = title,
                Channel = channel,
                CoverUrl = thumbnail,
                Duration = TimeSpan.FromSeconds(duration),
                Formats = SortFormats(formats)
            };
        }
    }

    /// <summary>排序：合并流优先（音画一体），其次视频流按分辨率降序，音频流按码率降序。</summary>
    private static List<YoutubeFormat> SortFormats(List<YoutubeFormat> formats)
    {
        int Rank(YoutubeFormat f) => f switch
        {
            { IsCombined: true } => 0,
            { IsVideoOnly: true } => 1,
            { IsAudioOnly: true } => 2,
            _ => 3
        };

        return formats
            .OrderBy(Rank)
            .ThenByDescending(f => f.Height)
            .ThenByDescending(f => f.AudioBitrate)
            .ToList();
    }

    // ---------- 下载 ----------

    /// <summary>
    /// 下载指定格式。用 -f {id}+bestaudio 时 yt-dlp 会自动挑音轨并合并，
    /// 但为了可预期，这里由调用方决定传单个 format_id 还是组合。
    /// </summary>
    public async Task DownloadAsync(string url, string formatSelector, string outputTemplate,
        IProgress<YoutubeProgress>? progress, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            EnsureAvailable();

            var args = new List<string>
            {
                "--no-warnings",
                "-f", formatSelector,
                "-o", outputTemplate,
                "--no-playlist",
                "--newline",           // 逐行输出进度，方便解析
                "--no-progress"        // 不要它自己的进度条，我们自己解析
            };

            AppendCookieArgs(args);

            // 合并后的容器：mp4 优先（本机播放器兼容性最好）
            args.Add("--merge-output-format");
            args.Add("mp4");

            // 音视频分离的流要 ffmpeg 合并，指向随包的 ffmpeg
            var ffmpegDir = Path.GetDirectoryName(MediaMerger.FindFfmpeg());
            if (!string.IsNullOrEmpty(ffmpegDir))
            {
                args.Add("--ffmpeg-location");
                args.Add(ffmpegDir);
            }

            args.Add(url);

            await RunAsync(args, ct, onLine: line => ParseProgress(line, progress));
        }
        finally { _lock.Release(); }
    }

    // ---------- 进度解析 ----------

    private void ParseProgress(string line, IProgress<YoutubeProgress>? progress)
    {
        if (progress is null) return;

        // yt-dlp --newline 的进度行形如：
        // [download]  45.2% of 89.34MiB at 2.31MiB/s ETA 00:12
        if (!line.Contains("[download]", StringComparison.Ordinal)) return;

        var pct = 0.0;
        long bytes = 0, total = 0;
        double speed = 0;

        var pctIdx = line.IndexOf('%');
        if (pctIdx > 0)
        {
            var pctText = line[..pctIdx].Trim();
            var sp = pctText.LastIndexOf(' ');
            if (sp >= 0) pctText = pctText[(sp + 1)..];
            if (double.TryParse(pctText, out var p)) pct = p;
        }

        // "of 89.34MiB" 里的总大小
        var ofIdx = line.IndexOf("of ", StringComparison.Ordinal);
        if (ofIdx >= 0)
        {
            var rest = line[(ofIdx + 3)..];
            var unitIdx = IndexOfUnit(rest);
            if (unitIdx >= 0)
            {
                var numText = rest[..unitIdx].Trim();
                var unit = rest[unitIdx..].Trim().ToLowerInvariant();
                if (double.TryParse(numText, out var n)) total = ToBytes(n, unit);
            }
        }

        // "at 2.31MiB/s" 里的速度
        var atIdx = line.IndexOf("at ", StringComparison.Ordinal);
        if (atIdx >= 0)
        {
            var rest = line[(atIdx + 3)..];
            var slashIdx = rest.IndexOf("/s", StringComparison.Ordinal);
            if (slashIdx > 0)
            {
                var speedText = rest[..slashIdx].Trim();
                var unitIdx = IndexOfUnit(speedText);
                if (unitIdx >= 0)
                {
                    var numText = speedText[..unitIdx].Trim();
                    var unit = speedText[unitIdx..].Trim().ToLowerInvariant();
                    if (double.TryParse(numText, out var n)) speed = ToBytes(n, unit);
                }
            }
        }

        if (total > 0) bytes = (long)(total * pct / 100.0);

        progress.Report(new YoutubeProgress
        {
            Percent = pct,
            ReceivedBytes = bytes,
            TotalBytes = total,
            BytesPerSecond = speed
        });
    }

    private static int IndexOfUnit(string s)
    {
        for (var i = 0; i < s.Length; i++)
            if (char.IsLetter(s[i])) return i;
        return -1;
    }

    private static long ToBytes(double n, string unit)
    {
        // 单位可能是 MiB / KiB / B 这种，也可能只是 Mi / Ki
        unit = unit.ToLowerInvariant();
        return unit switch
        {
            var u when u.StartsWith("g") => (long)(n * 1024 * 1024 * 1024),
            var u when u.StartsWith("m") => (long)(n * 1024 * 1024),
            var u when u.StartsWith("k") => (long)(n * 1024),
            _ => (long)n
        };
    }

    // ---------- 进程执行 ----------

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        List<string> args, CancellationToken ct, Action<string>? onLine = null)
    {
        EnsureAvailable();

        var psi = new ProcessStartInfo(_ytdlpPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // node 给 yt-dlp 当 EJS 解密运行时。官方 exe 已内置 yt-dlp-ejs 脚本，
        // 只需显式启用 node 运行时（默认只启用 deno）。
        if (_nodePath is not null)
        {
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add("node:" + _nodePath);
        }

        using var proc = new Process { StartInfo = psi };
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdoutSb.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderrSb.AppendLine(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            throw new YoutubeException("已取消");
        }

        return (proc.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
    }

    private void EnsureAvailable()
    {
        if (_ytdlpPath is null)
            throw new YoutubeException("未找到 yt-dlp.exe，请把它放到程序目录的 tools\\yt-dlp\\yt-dlp.exe");
    }

    /// <summary>从 stderr 里抠出最关键的那行错误。</summary>
    private static string ExtractError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "yt-dlp 失败（无错误信息）";

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // 找 ERROR 开头的行，取最后一条
        var err = lines.LastOrDefault(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase));
        if (err is not null)
            return err.Length > 200 ? err[..200] : err;

        // 没有 ERROR 就取最后一行
        return lines[^1].Length > 200 ? lines[^1][..200] : lines[^1];
    }

    /// <summary>从链接里抠视频 ID，作为 JSON 里 id 缺失时的兜底。</summary>
    public static string ExtractVideoId(string url)
    {
        var idx = url.IndexOf("v=", StringComparison.Ordinal);
        if (idx < 0) return "";
        var id = url[(idx + 2)..];
        var end = id.IndexOfAny(new[] { '&', '#', '?' });
        return end > 0 ? id[..end] : id;
    }

    // ---------- JSON 小工具 ----------

    private static string GetString(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return "";
        if (!e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static double? GetDouble(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(v.GetString(), out var d) => d,
            _ => null
        };
    }
}

/// <summary>yt-dlp 的下载进度。</summary>
public sealed class YoutubeProgress
{
    public double Percent { get; init; }
    public long ReceivedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
}
