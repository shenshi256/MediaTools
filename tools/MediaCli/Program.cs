using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaDownloader.Core;

namespace MediaCli;

/// <summary>
/// 媒体功能命令行验证器：对真实 mp4 依次跑截取 / 合并 / 音视分离，
/// 每一步都用 ffprobe 回读产物核对时长与流信息。
/// 用法：MediaCli [ffmpeg.exe 路径] [输入 mp4]
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;
    private static int _skip;

    private static string _ffmpeg = "";
    private static string _ffprobe = "";
    private static string _input = "";
    private static string _out = "";

    private static async Task<int> Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 选项和它的值要一起剔掉，否则 --net-toutiao <url> 里的 url 会被当成输入文件
        var positional = PositionalArgs(argv, "--net-toutiao", "--net-toutiao-download",
            "--net-tencent", "--net-tencent-download", "--net-youtube");

        var ffmpegArg = positional.Length > 0 ? positional[0] : @"E:\ffmpeg\ffmpeg.exe";
        _input = positional.Length > 1 ? positional[1] : Path.Combine(Path.GetTempPath(), "jjprobe", "e2e", "out.mp4");
        _out = Path.Combine(Path.GetTempPath(), "jjmedia");

        Banner();

        // ---------- [D] 抖音解析 ----------
        // 纯 JSON 解析，不需要 ffmpeg，所以放在环境检查之前：环境不满足也照样能验
        Section("[D] 抖音解析（真实响应样本，不依赖 ffmpeg）");
        DouyinSection();

        // ---------- [E] 今日头条解析 ----------
        Section("[E] 今日头条解析（真实响应样本，不依赖 ffmpeg）");
        ToutiaoSection();

        // 加 --net-toutiao <链接> 时额外跑一次真实联网，验证纯 HTTP 链路；
        // 加 --net-toutiao-download <链接> 则再顺带用 DownloadEngine 真正落一次盘
        var liveToutiao = ArgValue(argv, "--net-toutiao");
        var liveDownload = ArgValue(argv, "--net-toutiao-download");
        var liveUrl = liveDownload ?? liveToutiao;
        if (!string.IsNullOrWhiteSpace(liveUrl))
        {
            Section($"[E2] 今日头条真实联网  {liveUrl}");
            await ToutiaoLiveAsync(liveUrl, download: liveDownload is not null, ffmpegPath: ffmpegArg);
        }

        // ---------- [F] 腾讯视频 ----------
        Section("[F] 腾讯视频解析（真实响应样本，不依赖 ffmpeg）");
        TencentSection();

        var liveTencent = ArgValue(argv, "--net-tencent") ?? ArgValue(argv, "--net-tencent-download");
        if (!string.IsNullOrWhiteSpace(liveTencent))
        {
            Section($"[F2] 腾讯视频真实联网  {liveTencent}");
            await TencentLiveAsync(liveTencent, download: ArgValue(argv, "--net-tencent-download") is not null);
        }

        // ---------- [G] YouTube 解析 ----------
        Section("[G] YouTube 解析（真实响应样本，不依赖 ffmpeg）");
        YoutubeSection();

        var liveYoutube = ArgValue(argv, "--net-youtube");
        if (!string.IsNullOrWhiteSpace(liveYoutube))
        {
            Section($"[G2] YouTube 真实联网  {liveYoutube}");
            await YoutubeLiveAsync(liveYoutube);
        }

        // ---------- [0] 环境 ----------
        Section("[0] 环境检查");
        _ffmpeg = Path.GetFullPath(ffmpegArg);
        Check("ffmpeg 存在", File.Exists(_ffmpeg), _ffmpeg);
        _ffprobe = FfmpegRunner.ResolveFfprobe(_ffmpeg) ?? "";
        Check("ffprobe 存在", File.Exists(_ffprobe), _ffprobe);
        Check("输入文件存在", File.Exists(_input), _input);

        if (_fail > 0)
        {
            Console.WriteLine("\n环境不满足，终止。");
            return 1;
        }

        if (Directory.Exists(_out)) Directory.Delete(_out, true);
        Directory.CreateDirectory(_out);

        // ---------- [1] 探测 ----------
        Section("[1] 探测源文件");
        var src = await FfmpegRunner.ProbeAsync(_ffprobe, _input);
        if (src is null)
        {
            Check("ProbeAsync 返回结果", false, "返回 null");
            return 1;
        }
        Check("ProbeAsync 返回结果", true);
        Console.WriteLine($"      {src.FileName}");
        Console.WriteLine($"      时长 {src.Duration:hh\\:mm\\:ss\\.fff}  大小 {src.SizeBytes / 1024.0 / 1024.0:F1} MB");
        Console.WriteLine($"      视频 {src.VideoCodec} {src.Width}x{src.Height} @ {src.FrameRate}fps");
        Console.WriteLine($"      音频 {src.AudioCodec}");
        Console.WriteLine($"      签名 {src.Signature}");
        Check("有视频流", src.HasVideo);
        Check("有音频流", src.HasAudio);
        Check("时长大于 60 秒", src.Duration > TimeSpan.FromSeconds(60), src.Duration.ToString());

        // ---------- [2] 时间工具 ----------
        Section("[2] 时间解析与格式化");
        TimeToolTests();

        // ---------- [3] 无损截取 ----------
        Section("[3] 无损截取（-c copy，关键帧对齐）");
        var clipLossless = Path.Combine(_out, "lossless.mp4");
        var pLossless = new List<double>();
        var sw = Stopwatch.StartNew();
        var r1 = await VideoTrimmer.TrimAsync(_ffmpeg, _ffprobe, _input, clipLossless,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(40), precise: false,
            new Progress<FfmpegProgress>(p => { lock (pLossless) pLossless.Add(p.Percent); }), default);
        sw.Stop();
        Check("返回成功", r1.Success, Tail(r1.Output));
        Check("产物存在且非空", File.Exists(clipLossless) && new FileInfo(clipLossless).Length > 0,
            Bytes(clipLossless));
        var pr1 = await FfmpegRunner.ProbeAsync(_ffprobe, clipLossless);
        Check("产物可探测", pr1 is not null);
        if (pr1 is not null)
        {
            Check("视频编码与源一致", pr1.VideoCodec == src.VideoCodec, $"{pr1.VideoCodec} vs {src.VideoCodec}");
            Check("音频编码与源一致", pr1.AudioCodec == src.AudioCodec, $"{pr1.AudioCodec} vs {src.AudioCodec}");
            Check("分辨率与源一致", pr1.Width == src.Width && pr1.Height == src.Height,
                $"{pr1.Width}x{pr1.Height}");
        }
        // 契约：结束点准确 → 实际时长 ∈ [请求时长, 请求时长 + 一个 GOP]
        var reqLen = r1.RequestedDuration;
        Console.WriteLine($"      设定 {reqLen.TotalSeconds:F1}s → 实际 {r1.ActualDuration.TotalSeconds:F1}s" +
                          $"（起点前移 {r1.StartShift.TotalSeconds:F1}s）");
        Check("回读时长与 ffprobe 一致",
            pr1 is not null && Math.Abs(r1.ActualDuration.TotalSeconds - pr1.Duration.TotalSeconds) < 0.05,
            $"{r1.ActualDuration.TotalSeconds:F2} vs {pr1?.Duration.TotalSeconds:F2}");
        Check("实际时长不短于请求（结束点不丢帧）",
            r1.ActualDuration >= reqLen - TimeSpan.FromSeconds(0.3),
            $"{r1.ActualDuration.TotalSeconds:F2}s vs {reqLen.TotalSeconds:F2}s");
        Check("起点前移不超过一个 GOP（≤ 8s）",
            r1.StartShift <= TimeSpan.FromSeconds(8), r1.StartShift.TotalSeconds.ToString("F2") + "s");
        Check("StartSnapped 与 StartShift 自洽",
            r1.StartSnapped == (r1.StartShift > TimeSpan.FromSeconds(0.5)),
            $"snapped={r1.StartSnapped} shift={r1.StartShift.TotalSeconds:F2}s");
        Check("Summary 标注为快速模式", r1.Summary.Contains("快速"), r1.Summary);
        if (r1.StartSnapped)
            Check("起点前移时 Summary 提示关键帧", r1.Summary.Contains("关键帧"), r1.Summary);
        else
            _skip++;
        Check("耗时 < 5s（无损应极快）", sw.Elapsed.TotalSeconds < 5, sw.Elapsed.TotalSeconds.ToString("F2") + "s");
        ProgressTests(pLossless, "无损截取");

        // ---------- [4] 精确截取 ----------
        Section("[4] 精确截取（重编码 libx264，帧级准确）");
        var clipPrecise = Path.Combine(_out, "precise.mp4");
        var pPrecise = new List<double>();
        sw.Restart();
        var r2 = await VideoTrimmer.TrimAsync(_ffmpeg, _ffprobe, _input, clipPrecise,
            TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(22), precise: true,
            new Progress<FfmpegProgress>(p => { lock (pPrecise) pPrecise.Add(p.Percent); }), default);
        sw.Stop();
        Check("返回成功", r2.Success, Tail(r2.Output));
        Check("产物存在且非空", File.Exists(clipPrecise) && new FileInfo(clipPrecise).Length > 0,
            Bytes(clipPrecise));
        var pr2 = await FfmpegRunner.ProbeAsync(_ffprobe, clipPrecise);
        Check("产物可探测", pr2 is not null);
        if (pr2 is not null)
        {
            Check("时长≈10s（±0.6s）", Math.Abs(pr2.Duration.TotalSeconds - 10) <= 0.6,
                pr2.Duration.TotalSeconds.ToString("F2") + "s");
            Check("转成 h264", pr2.VideoCodec == "h264", pr2.VideoCodec);
            Check("音频转成 aac", pr2.AudioCodec == "aac", pr2.AudioCodec);
            Check("分辨率不变", pr2.Width == src.Width && pr2.Height == src.Height,
                $"{pr2.Width}x{pr2.Height}");
        }
        Check("精确模式起点不前移", !r2.StartSnapped, r2.StartShift.TotalSeconds.ToString("F2") + "s");
        Check("Summary 标注为精确模式", r2.Summary.Contains("精确"), r2.Summary);
        ProgressTests(pPrecise, "精确截取");

        // ---------- [5] 合并（同参数） ----------
        Section("[5] 合并：同参数三片段（精确切片，各 20s）");
        var parts = new List<string>();
        var partDurations = new List<TimeSpan>();
        for (var i = 0; i < 3; i++)
        {
            var f = Path.Combine(_out, $"part{i}.mp4");
            var rr = await VideoTrimmer.TrimAsync(_ffmpeg, _ffprobe, _input, f,
                TimeSpan.FromSeconds(i * 20), TimeSpan.FromSeconds(i * 20 + 20), precise: true, null, default);
            var pp = await FfmpegRunner.ProbeAsync(_ffprobe, f);
            Check($"切片 {i + 1} 生成", rr.Success && pp is not null, Tail(rr.Output));
            if (pp is not null) partDurations.Add(pp.Duration);
            parts.Add(f);
        }
        var expect = TimeSpan.FromTicks(partDurations.Sum(t => t.Ticks));

        var check = await VideoMerger.CheckAsync(_ffprobe, parts);
        Console.WriteLine($"      CheckAsync：{check.Message.Replace("\n", "\n      ")}");
        Check("CheckAsync 判定可无损拼接", check.CanCopy, check.Message);
        Check("CheckAsync 总时长 = 各切片之和",
            Math.Abs(check.TotalDuration.TotalSeconds - expect.TotalSeconds) <= 0.3,
            $"{check.TotalDuration.TotalSeconds:F2}s vs {expect.TotalSeconds:F2}s");
        Check("三段合计≈60s", Math.Abs(expect.TotalSeconds - 60) <= 1.5, expect.TotalSeconds.ToString("F2") + "s");

        var merged = Path.Combine(_out, "merged.mp4");
        var pMerge = new List<double>();
        var r3 = await VideoMerger.MergeAsync(_ffmpeg, parts, merged, check.TotalDuration,
            new Progress<FfmpegProgress>(p => { lock (pMerge) pMerge.Add(p.Percent); }), default);
        Check("MergeAsync 成功", r3.Success, Tail(r3.Output));
        var pr3 = await FfmpegRunner.ProbeAsync(_ffprobe, merged);
        Check("合并产物可探测", pr3 is not null);
        if (pr3 is not null)
        {
            Check("合并时长≈各片段之和（±2s）",
                Math.Abs(pr3.Duration.TotalSeconds - expect.TotalSeconds) <= 2,
                $"{pr3.Duration.TotalSeconds:F2}s vs {expect.TotalSeconds:F2}s");
            Check("合并后编码不变", pr3.Signature == pr2!.Signature, $"{pr3.Signature} vs {pr2.Signature}");
        }
        Check("临时列表文件已清理", Directory.GetFiles(Path.GetTempPath(), "jjconcat_*.txt").Length == 0);

        // ---------- [6] 合并（参数不一致应拒绝） ----------
        Section("[6] 合并：参数不一致检测");
        var odd = Path.Combine(_out, "odd_480p.mp4");
        var rOdd = await FfmpegRunner.RunAsync(_ffmpeg, new[]
        {
            "-i", _input, "-t", "4",
            "-vf", "scale=480:-2", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28",
            "-c:a", "aac", "-b:a", "96k", "-y", odd
        }, TimeSpan.FromSeconds(4), null, default, "造样本");
        Check("480p 样本生成", rOdd.Success && File.Exists(odd), Tail(rOdd.Output));

        var mixed = await VideoMerger.CheckAsync(_ffprobe, new[] { parts[0], odd });
        Console.WriteLine($"      CheckAsync：{mixed.Message.Replace("\n", "\n      ")}");
        Check("CheckAsync 正确拒绝", !mixed.CanCopy, mixed.Message);
        Check("拒绝信息里点出差异文件", mixed.Message.Contains("odd_480p.mp4"), mixed.Message);

        var noFile = await VideoMerger.CheckAsync(_ffprobe, new[] { parts[0] });
        Check("单文件时提示至少两个", !noFile.CanCopy && noFile.Message.Contains("两个"), noFile.Message);

        // ---------- [7] 音视分离 ----------
        Section("[7] 音视分离");
        var m4a = AudioSeparator.SuggestOutput(_input, AudioMode.ExtractM4a);
        Check("SuggestOutput(m4a) 后缀正确", m4a.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase), m4a);
        Check("SuggestOutput(mp3) 后缀正确",
            AudioSeparator.SuggestOutput(_input, AudioMode.ExtractMp3).EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));
        Check("SuggestOutput(去音轨) 带 _无音轨",
            AudioSeparator.SuggestOutput(_input, AudioMode.StripAudio).Contains("_无音轨"));

        // 7.1 提取 m4a
        var outM4a = Path.Combine(_out, "audio.m4a");
        var pM4a = new List<double>();
        var r4 = await AudioSeparator.RunAsync(_ffmpeg, _input, outM4a, AudioMode.ExtractM4a, src.Duration,
            new Progress<FfmpegProgress>(p => { lock (pM4a) pM4a.Add(p.Percent); }), default);
        Check("提取 m4a 成功", r4.Success, Tail(r4.Output));
        var pr4 = await FfmpegRunner.ProbeAsync(_ffprobe, outM4a);
        Check("m4a 可探测", pr4 is not null);
        if (pr4 is not null)
        {
            Check("m4a 无视频流", !pr4.HasVideo, pr4.VideoCodec);
            Check("m4a 有音频流", pr4.HasAudio, pr4.AudioCodec);
            Check("m4a 时长≈源（±1s）", Math.Abs(pr4.Duration.TotalSeconds - src.Duration.TotalSeconds) <= 1,
                $"{pr4.Duration.TotalSeconds:F2}s vs {src.Duration.TotalSeconds:F2}s");
            Check("m4a 体积明显小于源", pr4.SizeBytes < src.SizeBytes);
        }
        ProgressTests(pM4a, "提取 m4a");

        // 7.2 提取 mp3
        var outMp3 = Path.Combine(_out, "audio.mp3");
        var r5 = await AudioSeparator.RunAsync(_ffmpeg, _input, outMp3, AudioMode.ExtractMp3, src.Duration,
            null, default);
        Check("提取 mp3 成功", r5.Success, Tail(r5.Output));
        var pr5 = await FfmpegRunner.ProbeAsync(_ffprobe, outMp3);
        Check("mp3 可探测", pr5 is not null);
        if (pr5 is not null)
        {
            Check("mp3 无视频流", !pr5.HasVideo);
            Check("mp3 编码为 mp3", pr5.AudioCodec == "mp3", pr5.AudioCodec);
            Check("mp3 时长≈源（±1.5s）", Math.Abs(pr5.Duration.TotalSeconds - src.Duration.TotalSeconds) <= 1.5,
                $"{pr5.Duration.TotalSeconds:F2}s");
        }

        // 7.3 去音轨
        var outMute = Path.Combine(_out, "mute.mp4");
        var r6 = await AudioSeparator.RunAsync(_ffmpeg, _input, outMute, AudioMode.StripAudio, src.Duration,
            null, default);
        Check("去音轨成功", r6.Success, Tail(r6.Output));
        var pr6 = await FfmpegRunner.ProbeAsync(_ffprobe, outMute);
        Check("去音轨产物可探测", pr6 is not null);
        if (pr6 is not null)
        {
            Check("无音频流", !pr6.HasAudio, pr6.AudioCodec);
            Check("视频流保留", pr6.HasVideo && pr6.VideoCodec == src.VideoCodec, pr6.VideoCodec);
            Check("时长≈源（±0.5s）", Math.Abs(pr6.Duration.TotalSeconds - src.Duration.TotalSeconds) <= 0.5,
                $"{pr6.Duration.TotalSeconds:F2}s");
        }

        // ---------- [8] 覆盖与异常 ----------
        Section("[8] 覆盖写与异常分支");
        var before = new FileInfo(outMute).Length;
        var r7 = await AudioSeparator.RunAsync(_ffmpeg, _input, outMute, AudioMode.StripAudio, src.Duration,
            null, default);
        Check("已存在产物可覆盖（不报错）", r7.Success, Tail(r7.Output));
        Check("覆盖后文件仍有效", new FileInfo(outMute).Length == before,
            $"{new FileInfo(outMute).Length} vs {before}");

        var threw = false;
        try
        {
            await VideoTrimmer.TrimAsync(_ffmpeg, _ffprobe, Path.Combine(_out, "不存在.mp4"), Path.Combine(_out, "x.mp4"),
                TimeSpan.Zero, TimeSpan.FromSeconds(5), false, null, default);
        }
        catch (FileNotFoundException) { threw = true; }
        Check("输入不存在抛 FileNotFoundException", threw);

        threw = false;
        try
        {
            await VideoTrimmer.TrimAsync(_ffmpeg, _ffprobe, _input, Path.Combine(_out, "x.mp4"),
                TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), false, null, default);
        }
        catch (ArgumentException) { threw = true; }
        Check("结束早于开始抛 ArgumentException", threw);

        // ---------- [9] 取消 ----------
        Section("[9] 取消：杀进程树");
        var baseline = Process.GetProcessesByName("ffmpeg").Length;
        var cts = new CancellationTokenSource();
        var cancelled = false;
        try
        {
            var big = Path.Combine(_out, "cancel.mp4");
            var task = VideoTrimmer.TrimAsync(_ffmpeg, _ffprobe, _input, big,
                TimeSpan.Zero, TimeSpan.FromSeconds(300), precise: true, null, cts.Token);
            await Task.Delay(700);
            cts.Cancel();
            await task;
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { Console.WriteLine($"      未预期异常：{ex.GetType().Name}: {ex.Message}"); }
        Check("取消抛 OperationCanceledException", cancelled);
        await Task.Delay(800);
        var after = Process.GetProcessesByName("ffmpeg").Length;
        Check("无残留 ffmpeg 进程", after <= baseline, $"before={baseline} after={after}");
        cts.Dispose();

        // ---------- [10] 抽帧缩略图 ----------
        Section("[10] 抽帧缩略图（时间轴预览用）");
        sw.Restart();
        var frame = await FfmpegRunner.ExtractFrameAsync(_ffmpeg, _input, TimeSpan.FromSeconds(30), 192);
        sw.Stop();
        Check("抽帧返回数据", frame is { Length: > 500 }, frame is null ? "null" : $"{frame.Length} 字节");
        Check("是有效 JPEG（FF D8 开头）",
            frame is { Length: > 2 } && frame[0] == 0xFF && frame[1] == 0xD8);
        Check("抽帧耗时 < 3s", sw.Elapsed.TotalSeconds < 3, sw.Elapsed.TotalSeconds.ToString("F2") + "s");

        var frameStart = await FfmpegRunner.ExtractFrameAsync(_ffmpeg, _input, TimeSpan.Zero, 192);
        // 注意：源视频开头是黑场淡入，黑帧压出来只有 ~330 字节，因此按 JPEG 魔数判断而不是按体积
        Check("起点 0 秒可抽帧且是 JPEG",
            frameStart is { Length: > 100 } && frameStart[0] == 0xFF && frameStart[1] == 0xD8,
            frameStart is null ? "null" : $"{frameStart.Length} 字节");

        var frameOut = await FfmpegRunner.ExtractFrameAsync(_ffmpeg, _input,
            src.Duration + TimeSpan.FromSeconds(30), 192);
        Check("超出时长返回 null", frameOut is null);

        using (var ctsFrame = new CancellationTokenSource())
        {
            ctsFrame.Cancel();
            var cancelledFrame = await FfmpegRunner.ExtractFrameAsync(_ffmpeg, _input,
                TimeSpan.FromSeconds(60), 192, ctsFrame.Token);
            Check("已取消的 token 返回 null", cancelledFrame is null);
        }

        var frameSizes = new List<int>();
        var allJpeg = true;
        for (var t = 0; t < 5; t++)
        {
            var f = await FfmpegRunner.ExtractFrameAsync(_ffmpeg, _input, TimeSpan.FromSeconds(t * 3), 192);
            frameSizes.Add(f?.Length ?? 0);
            if (f is not { Length: > 100 } || f[0] != 0xFF || f[1] != 0xD8) allJpeg = false;
        }
        Check("连续抽 5 帧都是有效 JPEG", allJpeg, string.Join(",", frameSizes));
        Check("其中至少一帧有实际画面（> 1KB）", frameSizes.Any(n => n > 1024), string.Join(",", frameSizes));

        // ---------- [11] 音视分离：一次两个文件 ----------
        Section("[11] 音视分离（音频 + 无声视频，两个文件）");
        var splitAudio = Path.Combine(_out, "split_audio.mp3");
        var splitVideo = Path.Combine(_out, "split_video.mp4");
        var pSplit = new List<double>();

        var sp = await AudioSeparator.SplitAsync(_ffmpeg, _input, splitVideo, splitAudio,
            AudioMode.ExtractMp3, src.Duration, hasAudio: true,
            new Progress<FfmpegProgress>(p => { lock (pSplit) pSplit.Add(p.Percent); }), default);

        Check("SplitAsync 成功", sp.Success, sp.Message);
        Check("音频与视频两个文件都生成",
            sp.AudioDone && sp.VideoDone && File.Exists(splitAudio) && File.Exists(splitVideo),
            $"audio={sp.AudioDone} video={sp.VideoDone}");
        Check("Message 说明产出两个文件", sp.Message.Contains("2 个文件"), sp.Message);

        var pa = await FfmpegRunner.ProbeAsync(_ffprobe, splitAudio);
        Check("音频文件可探测", pa is not null);
        if (pa is not null)
        {
            Check("音频文件无视频流", !pa.HasVideo, pa.VideoCodec);
            Check("音频文件是 mp3", pa.AudioCodec == "mp3", pa.AudioCodec);
            Check("音频时长≈源（±1.5s）", Math.Abs(pa.Duration.TotalSeconds - src.Duration.TotalSeconds) <= 1.5,
                $"{pa.Duration.TotalSeconds:F2}s");
        }

        var pv = await FfmpegRunner.ProbeAsync(_ffprobe, splitVideo);
        Check("视频文件可探测", pv is not null);
        if (pv is not null)
        {
            Check("视频文件无音频流", !pv.HasAudio, pv.AudioCodec);
            Check("视频流编码不变（copy）", pv.VideoCodec == src.VideoCodec, pv.VideoCodec);
            Check("视频分辨率不变", pv.Width == src.Width && pv.Height == src.Height, $"{pv.Width}x{pv.Height}");
            Check("视频时长≈源（±0.5s）", Math.Abs(pv.Duration.TotalSeconds - src.Duration.TotalSeconds) <= 0.5,
                $"{pv.Duration.TotalSeconds:F2}s");
        }

        Check("视频文件不携带音频（体积应小于源）",
            pv is not null && pv.SizeBytes < src.SizeBytes, $"{pv?.SizeBytes} vs {src.SizeBytes}");
        ProgressTests(pSplit, "音视分离");
        Check("进度跨两个阶段（出现 40%~60% 的中间值）",
            pSplit.Any(x => x > 40 && x < 60),
            string.Join(" ", pSplit.Select(x => x.ToString("F0"))));

        // m4a 模式（音轨直接复制）
        var splitM4a = Path.Combine(_out, "split_audio.m4a");
        var splitVideo2 = Path.Combine(_out, "split_video2.mp4");
        var sp2 = await AudioSeparator.SplitAsync(_ffmpeg, _input, splitVideo2, splitM4a,
            AudioMode.ExtractM4a, src.Duration, hasAudio: true, null, default);
        Check("m4a 模式成功", sp2.Success, sp2.Message);
        var pa2 = await FfmpegRunner.ProbeAsync(_ffprobe, splitM4a);
        Check("m4a 是 aac 且无视频流", pa2 is { HasAudio: true, HasVideo: false } && pa2.AudioCodec == "aac",
            pa2?.AudioCodec);

        // 无音轨的源：只应产出视频
        var splitVideo3 = Path.Combine(_out, "split_video3.mp4");
        var splitAudio3 = Path.Combine(_out, "split_audio3.mp3");
        var sp3 = await AudioSeparator.SplitAsync(_ffmpeg, outMute, splitVideo3, splitAudio3,
            AudioMode.ExtractMp3, src.Duration, hasAudio: false, null, default);
        Check("无音轨源：成功且只生成视频",
            sp3.Success && !sp3.AudioDone && File.Exists(splitVideo3) && !File.Exists(splitAudio3), sp3.Message);

        // ---------- [12] 视频压缩 ----------
        Section("[12] 视频压缩（libx265 CRF / 目标体积 2Pass / 试压片段）");
        await CompressSection(src);

        // ---------- 汇总 ----------
        Console.WriteLine();
        Console.WriteLine(new string('=', 66));
        Console.WriteLine($"  通过 {_pass}    失败 {_fail}    跳过 {_skip}");
        Console.WriteLine($"  产物目录：{_out}");
        Console.WriteLine(new string('=', 66));

        return _fail == 0 ? 0 : 2;
    }

    /// <summary>
    /// 压缩功能回归：先用纯函数断言命令构造，再用真实 ffmpeg 压一遍本素材，
    /// 验证「产物更小、ffprobe 能读、参数与请求一致」。
    /// </summary>
    private static async Task CompressSection(MediaProbe src)
    {
        // ----- 命令构造（纯函数，离线断言） -----
        var opts = new CompressOptions
        {
            Mode = CompressMode.Crf,
            Encoder = CompressEncoder.H265,
            Crf = 24,
            Preset = "medium",
            Audio = CompressAudio.Aac96
        };

        var args = CompressService.BuildCrfArgs(opts, @"D:\in.mp4", @"D:\out.mp4");
        var joined = string.Join(" ", args);
        Check("CRF 参数含 libx265", joined.Contains("-c:v libx265"), joined);
        Check("CRF 参数含 crf 24", joined.Contains("-crf 24"));
        Check("CRF 参数含 preset", joined.Contains("-preset medium"));
        Check("音频按选择重压 96k", joined.Contains("-c:a aac -b:a 96k"));
        Check("faststart 已加", joined.Contains("-movflags +faststart"));
        Check("输入输出都在", joined.Contains(@"D:\in.mp4") && joined.Contains(@"D:\out.mp4"));

        var h264 = new CompressOptions { Mode = CompressMode.Crf, Encoder = CompressEncoder.H264, Crf = 24, Audio = CompressAudio.Copy };
        var joined264 = string.Join(" ", CompressService.BuildCrfArgs(h264, "i", "o"));
        Check("x264 同档 CRF 自动降 3（24→21）", joined264.Contains("-crf 21"), joined264);
        Check("x264 + copy 音频", joined264.Contains("-c:a copy"));

        // 试压片段：取 1/4 处 30 秒
        var test = string.Join(" ", CompressService.BuildTestArgs(opts, "i", "o", 61.5));
        Check("试压含 -t 30", test.Contains("-t 30"));
        Check("试压起点在 1/4 处（约 15.4s）", test.Contains("00:00:15.375"), test);

        // 目标体积：码率预算
        var bitrate = CompressService.EstimateVideoBitrate(50L * 1024 * 1024, 60);
        Check("50MB/60s 估出约 6.8Mbps", bitrate is > 6_500_000 and < 7_100_000, bitrate.ToString("N0"));

        var (p1, p2, _, _) = CompressService.BuildTwoPassArgs(
            new CompressOptions { Mode = CompressMode.TargetSize, TargetSizeMB = 50 }, "i", "o", 60,
            Path.Combine(_out, "x265.stats"));
        Check("2Pass 第一遍 -an 且不打快进", string.Join(" ", p1).Contains("-an") && !string.Join(" ", p1).Contains("faststart"));
        Check("2Pass 第二遍带音频与 faststart", string.Join(" ", p2).Contains("-b:a 96k") && string.Join(" ", p2).Contains("faststart"));
        Check("2Pass x265 用 stats 文件", string.Join(" ", p2).Contains("pass=2:stats="));

        Check("自定义参数按引号切分",
            CompressService.SplitCustomArgs("""-crf 23 -x265-params "aq-mode=3:qcomp=0.7" """ )
                .SequenceEqual(new[] { "-crf", "23", "-x265-params", "aq-mode=3:qcomp=0.7" }),
            string.Join("|", CompressService.SplitCustomArgs("""-crf 23 -x265-params "aq-mode=3:qcomp=0.7" """)));

        Check("输出路径加 _c 后缀",
            CompressService.BuildOutputPath(Path.Combine(_out, "哪吒.mp4")).EndsWith("哪吒_c.mp4", StringComparison.Ordinal),
            CompressService.BuildOutputPath(Path.Combine(_out, "哪吒.mp4")));

        Check("H265 压缩比预估在 0.3~0.5",
            CompressService.EstimateRatio(new CompressOptions { Crf = 24 }) is > 0.3 and < 0.5);

        // ----- 真实压缩 -----
        var progress = new Progress<FfmpegProgress>(p => { });
        // 先造一个"高码率片源"（模拟单反导出/大会员高码率），x265 压它才有可观的压缩比；
        // 对测试素材这种本来就压干的低码率源，CRF 24 产物反而会变大（油水已榨干），
        // 这本身就是一个值得记录的观察。
        var highSrc = Path.Combine(_out, "compress_highsrc.mp4");
        var mkHigh = await FfmpegRunner.RunAsync(_ffmpeg,
            new[] { "-y", "-i", _input, "-c:v", "libx264", "-crf", "12", "-preset", "fast", "-c:a", "copy", highSrc },
            src.Duration, progress, default, "造高码率源");
        Check("高码率源已生成", mkHigh.Success && File.Exists(highSrc), Tail(mkHigh.Output));
        var highLen = File.Exists(highSrc) ? new FileInfo(highSrc).Length : 0;
        Console.WriteLine($"      高码率源 {highLen / 1024.0 / 1024.0:F1} MB（源素材 {_input} 是低码率，仅作参考）");

        var crfOut = Path.Combine(_out, "compress_crf.mp4");
        var sw = Stopwatch.StartNew();
        var result = await FfmpegRunner.RunAsync(_ffmpeg,
            CompressService.BuildCrfArgs(opts, highSrc, crfOut), src.Duration, progress, default, "压缩");
        sw.Stop();
        Check("libx265 CRF 压缩成功", result.Success, Tail(result.Output));

        var cOut = await FfmpegRunner.ProbeAsync(_ffprobe, crfOut);
        if (cOut is not null)
        {
            Check("产物可被 ffprobe 读取", true);
            Check("产物编码是 hevc", cOut.VideoCodec == "hevc", cOut.VideoCodec);
            Check("产物音频是 aac 96k 级别", cOut.HasAudio && cOut.AudioCodec == "aac", cOut.AudioCodec);
            Check("产物时长与源一致", Math.Abs(cOut.Duration.TotalSeconds - src.Duration.TotalSeconds) < 0.5,
                $"{cOut.Duration.TotalSeconds:F1}s vs {src.Duration.TotalSeconds:F1}s");
            Check("产物分辨率与源一致", cOut.Width == src.Width && cOut.Height == src.Height,
                $"{cOut.Width}x{cOut.Height}");

            var dstLen = File.Exists(crfOut) ? new FileInfo(crfOut).Length : 0;
            var saved = highLen > 0 ? (1.0 - (double)dstLen / highLen) * 100 : 0;
            Console.WriteLine($"      {highLen / 1024.0 / 1024.0:F1} MB → {dstLen / 1024.0 / 1024.0:F1} MB（省 {saved:F0}%）  编码耗时 {sw.ElapsedMilliseconds} ms");
            Check("高码率源压后显著变小（省 40% 以上）", dstLen > 0 && saved > 40, $"{dstLen} vs {highLen}");
        }
        else
        {
            Check("产物可被 ffprobe 读取", false, "probe 返回 null");
        }

        // 试压片段真实跑一遍
        var testOut = Path.Combine(_out, "compress_test30.mp4");
        var tr = await FfmpegRunner.RunAsync(_ffmpeg,
            CompressService.BuildTestArgs(opts, _input, testOut, src.Duration.TotalSeconds),
            TimeSpan.FromSeconds(30), progress, default, "试压");
        Check("试压 30 秒成功", tr.Success, Tail(tr.Output));
        var tProbe = await FfmpegRunner.ProbeAsync(_ffprobe, testOut);
        Check("试压产物约 30s 且编码 hevc",
            tProbe is { VideoCodec: "hevc" } && Math.Abs(tProbe.Duration.TotalSeconds - 30) < 0.6,
            tProbe?.Duration.TotalSeconds.ToString("F1") ?? "null");
    }

    private static void TimeToolTests()
    {
        (string Input, double ExpectSec, bool Ok)[] cases =
        {
            ("83.5", 83.5, true),
            ("01:23", 83, true),
            ("1:02:03", 3723, true),
            ("0:00:00", 0, true),
            ("1：02：03", 3723, true),
            ("", 0, false),
            ("abc", 0, false),
            ("1:2:3:4", 0, false),
            ("-5", 0, false)
        };

        foreach (var c in cases)
        {
            var got = FfmpegRunner.TryParseTime(c.Input, out var v);
            var ok = got == c.Ok && (!c.Ok || Math.Abs(v.TotalSeconds - c.ExpectSec) < 0.001);
            Check($"TryParseTime(\"{c.Input}\")", ok, got ? v.TotalSeconds.ToString("F3") + "s" : "false");
        }

        Check("ToFfmpegTime(0) = 00:00:00.000",
            FfmpegRunner.ToFfmpegTime(TimeSpan.Zero) == "00:00:00.000",
            FfmpegRunner.ToFfmpegTime(TimeSpan.Zero));
        Check("ToFfmpegTime(3723.5s) = 01:02:03.500",
            FfmpegRunner.ToFfmpegTime(TimeSpan.FromSeconds(3723.5)) == "01:02:03.500",
            FfmpegRunner.ToFfmpegTime(TimeSpan.FromSeconds(3723.5)));
        Check("ToFfmpegTime(负数) 归零",
            FfmpegRunner.ToFfmpegTime(TimeSpan.FromSeconds(-9)) == "00:00:00.000");
    }

    private static void ProgressTests(List<double> samples, string label)
    {
        double[] snap;
        lock (samples) snap = samples.ToArray();

        Check($"{label}：有进度回调", snap.Length > 0, $"{snap.Length} 次");
        Check($"{label}：进度单调不减", IsMonotonic(snap), string.Join(" ", snap.Select(x => x.ToString("F0"))));
        Check($"{label}：末次进度 ≥ 99", snap.Length > 0 && snap[^1] >= 99,
            snap.Length > 0 ? snap[^1].ToString("F1") : "无");
        Check($"{label}：回调次数不过密（≤ 400）", snap.Length <= 400, $"{snap.Length} 次");
    }

    private static bool IsMonotonic(double[] a)
    {
        for (var i = 1; i < a.Length; i++)
            if (a[i] + 0.001 < a[i - 1]) return false;
        return true;
    }

    private static void Banner()
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 66));
        Console.WriteLine("  媒体功能验证  VideoTrimmer / VideoMerger / AudioSeparator");
        Console.WriteLine($"  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine(new string('=', 66));
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
    }

    /// <summary>
    /// 抖音解析回归。样本是 2026-10-30 从真实页面拦下来的 aweme_detail 响应，
    /// 存在 testdata/douyin_detail.json，随生成输出一起复制。
    /// </summary>
    private static void DouyinSection()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "testdata", "douyin_detail.json");
        Check("样本文件存在", File.Exists(fixture), fixture);
        if (!File.Exists(fixture)) return;

        var json = File.ReadAllText(fixture);

        DouyinVideo v;
        try
        {
            v = DouyinParser.Parse(json);
            Check("解析不抛异常", true);
        }
        catch (Exception ex)
        {
            Check("解析不抛异常", false, ex.Message);
            return;
        }

        Check("作品 ID", v.AwemeId == "7477518093946752310", v.AwemeId);
        Check("标题非空且含关键字", v.Title.Contains("哪吒"), v.Title);
        Check("作者", v.Author == "我们视频", v.Author);
        Check("时长 ≈ 10.169s", Math.Abs(v.Duration.TotalSeconds - 10.169) < 0.01, v.Duration.ToString());
        Check("宽高 1080x1920", v.Width == 1080 && v.Height == 1920, $"{v.Width}x{v.Height}");
        Check("非图文作品", !v.IsImagePost);
        Check("封面是 https", v.CoverUrl.StartsWith("https://"), v.CoverUrl);
        Check("档位数量 = 12", v.Streams.Count == 12, v.Streams.Count.ToString());

        Check("最高档 1080P", v.Streams.Count > 0 && v.Streams[0].Label == "1080P",
            v.Streams.Count > 0 ? $"{v.Streams[0].Label} {v.Streams[0].Width}x{v.Streams[0].Height}" : "(空)");

        Check("竖屏取短边而不是长边", v.Streams.Count > 0 && v.Streams[0].ShortSide == 1080,
            v.Streams.Count > 0 ? v.Streams[0].ShortSide.ToString() : "(空)");

        Check("档位按短边降序",
            v.Streams.Select(x => x.ShortSide).SequenceEqual(v.Streams.Select(x => x.ShortSide).OrderByDescending(x => x)),
            string.Join(",", v.Streams.Select(x => x.ShortSide)));

        Check("所有地址都是 https 且非空",
            v.Streams.All(x => x.Url.StartsWith("https://")),
            v.Streams.FirstOrDefault(x => !x.Url.StartsWith("https://"))?.Url);

        Check("档位地址无重复",
            v.Streams.Select(x => x.Url).Distinct().Count() == v.Streams.Count);
        Check("InfoText 可读", v.InfoText.Contains("时长") && v.InfoText.Contains("档"), v.InfoText);
        Check("DisplayName 含码率", v.Streams[0].DisplayName.Contains("kbps"), v.Streams[0].DisplayName);

        // ----- 输入识别 -----
        const string id = "7477518093946752310";
        Check("整段链接", DouyinParser.TryExtractAwemeId($"https://www.douyin.com/video/{id}") == id);
        Check("share 链接",
            DouyinParser.TryExtractAwemeId($"https://www.iesdouyin.com/share/video/{id}/?region=CN") == id);
        Check("note 链接",
            DouyinParser.TryExtractAwemeId("https://www.douyin.com/note/7420371100103707913") == "7420371100103707913");
        Check("纯数字 ID", DouyinParser.TryExtractAwemeId(id) == id);
        Check("分享口令里抠 ID",
            DouyinParser.TryExtractAwemeId($"3.51 复制打开抖音，看看【我们视频】… https://v.douyin.com/{id}/") == id
            || DouyinParser.TryExtractShortUrl("https://v.douyin.com/i5u48YTn/") is not null);
        Check("短链识别", DouyinParser.TryExtractShortUrl("看这个 https://v.douyin.com/i5u48YTn/ 不错") == "https://v.douyin.com/i5u48YTn");
        Check("无意义输入返回 null", DouyinParser.TryExtractAwemeId("随便一段没有链接的话") is null);

        // ----- 异常路径 -----
        Check("空内容抛 DouyinException", Throws<DouyinException>(() => DouyinParser.Parse("")));
        Check("detail 为 null 抛 DouyinException",
            Throws<DouyinException>(() => DouyinParser.Parse("""{"status_code":0,"aweme_detail":null}""")));
        Check("status_code 非 0 抛 DouyinException",
            Throws<DouyinException>(() => DouyinParser.Parse("""{"status_code":2154,"aweme_detail":null}""")));
        Check("非法 JSON 抛 DouyinException",
            Throws<DouyinException>(() => DouyinParser.Parse("not json at all")));

        // ----- bit_rate 缺失时回落到 play_addr -----
        const string minimal = """
        {"status_code":0,"aweme_detail":{"aweme_id":"1","desc":"兜底","duration":1000,
          "video":{"duration":1000,"width":720,"height":1280,
            "play_addr":{"url_list":["https://x/a.mp4"],"width":720,"height":1280,"data_size":1024}}}}
        """;
        try
        {
            var m = DouyinParser.Parse(minimal);
            Check("无 bit_rate 时回落到 play_addr", m.Streams.Count == 1 && m.Streams[0].Url == "https://x/a.mp4",
                m.Streams.Count.ToString());
            Check("兜底档位带尺寸", m.Streams[0].Height == 1280, m.Streams[0].DisplayName);
        }
        catch (Exception ex)
        {
            Check("无 bit_rate 时回落到 play_addr", false, ex.Message);
        }
    }

    /// <summary>
    /// 头条解析回归。两份样本都是 2026-10-30 从真实接口抓的：
    ///   testdata/toutiao_info.json      —— m.toutiao.com/i{id}/info/ 的响应
    ///   testdata/toutiao_playinfo.json  —— vod.bytedanceapi.com GetPlayInfo 的响应
    /// 样本里的播放地址会过期，但解析器只做字符串处理，不校验签名，所以不影响回归。
    /// </summary>
    private static void ToutiaoSection()
    {
        var infoPath = Path.Combine(AppContext.BaseDirectory, "testdata", "toutiao_info.json");
        var playPath = Path.Combine(AppContext.BaseDirectory, "testdata", "toutiao_playinfo.json");
        Check("info 样本存在", File.Exists(infoPath), infoPath);
        Check("playinfo 样本存在", File.Exists(playPath), playPath);
        if (!File.Exists(infoPath) || !File.Exists(playPath)) return;

        ToutiaoVideo info;
        try
        {
            info = ToutiaoParser.ParseInfo(File.ReadAllText(infoPath));
            Check("解析 info 不抛异常", true);
        }
        catch (Exception ex)
        {
            Check("解析 info 不抛异常", false, ex.Message);
            return;
        }

        // ----- 元数据 -----
        Check("条目 ID", info.GroupId == "7542802202696515599", info.GroupId);
        Check("视频资源 ID", info.VideoId == "v0d910g10002d2m26ufog65p5d10lpv0", info.VideoId);
        Check("标题含关键字", info.Title.Contains("头条治序"), info.Title);
        Check("作者", info.Author == "今日头条", info.Author);
        Check("时长 = 61s", Math.Abs(info.Duration.TotalSeconds - 61) < 0.01, info.Duration.ToString());
        Check("发布时间 2025-08-26",
            info.PublishTime is { } pt && pt.LocalDateTime.ToString("yyyy-MM-dd") == "2025-08-26",
            info.PublishTime?.ToString() ?? "(空)");
        Check("封面是 https", info.CoverUrl.StartsWith("https://"), info.CoverUrl);
        Check("播放凭证非空", info.PlayToken.Length > 100, info.PlayToken.Length.ToString());

        // ----- token 拆解 -----
        string query;
        try
        {
            query = ToutiaoParser.DecodePlayToken(info.PlayToken);
            Check("拆 token 不抛异常", true);
        }
        catch (Exception ex)
        {
            Check("拆 token 不抛异常", false, ex.Message);
            return;
        }

        Check("token 里含 GetPlayInfo", query.Contains("Action=GetPlayInfo"), query[..Math.Min(60, query.Length)]);
        Check("token 里有签名", query.Contains("X-Amz-Signature="));
        Check("token 里 \\u0026 已还原成 &", !query.Contains("\\u0026", StringComparison.Ordinal));
        Check("token 里带 video_id", query.Contains("video_id=v0d910g10002d2m26ufog65p5d10lpv0"));

        var playUrl = ToutiaoParser.BuildPlayInfoUrl(info.PlayToken);
        Check("播放接口 URL 前缀正确", playUrl.StartsWith("https://vod.bytedanceapi.com/?Action=GetPlayInfo"), playUrl[..Math.Min(70, playUrl.Length)]);

        // ----- 清晰度档位 -----
        IReadOnlyList<ToutiaoStream> streams;
        try
        {
            streams = ToutiaoParser.ParsePlayInfo(File.ReadAllText(playPath));
            Check("解析 playinfo 不抛异常", true);
        }
        catch (Exception ex)
        {
            Check("解析 playinfo 不抛异常", false, ex.Message);
            return;
        }

        Check("档位数量 = 3", streams.Count == 3, streams.Count.ToString());
        Check("最高档 720P", streams.Count > 0 && streams[0].Label == "720P",
            streams.Count > 0 ? $"{streams[0].Label} {streams[0].Width}x{streams[0].Height}" : "(空)");
        Check("竖屏取短边而不是长边", streams.Count > 0 && streams[0].ShortSide == 720,
            streams.Count > 0 ? streams[0].ShortSide.ToString() : "(空)");
        Check("档位按短边降序",
            streams.Select(x => x.ShortSide).SequenceEqual(streams.Select(x => x.ShortSide).OrderByDescending(x => x)),
            string.Join(",", streams.Select(x => x.ShortSide)));
        Check("所有地址都是 https",
            streams.All(x => x.Url.StartsWith("https://")),
            streams.FirstOrDefault(x => !x.Url.StartsWith("https://"))?.Url);
        Check("档位地址无重复",
            streams.Select(x => x.Url).Distinct().Count() == streams.Count);
        Check("档位带文件大小", streams.All(x => x.DataSize > 0),
            string.Join(",", streams.Select(x => x.DataSize)));
        Check("最高档的 DisplayName 可读",
            streams[0].DisplayName.Contains("kbps") && streams[0].DisplayName.Contains("MB"),
            streams[0].DisplayName);
        Check("备份地址存在", streams[0].BackupUrls.Length == 1 && streams[0].BackupUrls[0].StartsWith("https://"));

        // ----- 两段合并 -----
        var merged = ToutiaoParser.Combine(info, streams);
        Check("合并后带档位", merged.Streams.Count == 3 && merged.Title == info.Title);
        Check("InfoText 含作者与档数",
            merged.InfoText.Contains("@今日头条") && merged.InfoText.Contains("共 3 档"), merged.InfoText);
        Check("InfoText 含发布日期", merged.InfoText.Contains("2025-08-26"), merged.InfoText);

        // ----- 输入识别 -----
        const string gid = "7542802202696515599";
        Check("video 链接", ToutiaoParser.TryExtractGroupId($"https://www.toutiao.com/video/{gid}/") == gid);
        Check("article 链接", ToutiaoParser.TryExtractGroupId($"https://www.toutiao.com/article/{gid}/") == gid);
        Check("移动版 i 链接", ToutiaoParser.TryExtractGroupId($"https://m.toutiao.com/i{gid}/") == gid);
        Check("纯数字 ID", ToutiaoParser.TryExtractGroupId(gid) == gid);
        Check("分享文案里抠 ID",
            ToutiaoParser.TryExtractGroupId($"【标题】 https://www.toutiao.com/video/{gid}/ 复制打开") == gid);
        Check("短链识别",
            ToutiaoParser.TryExtractShortUrl("看看这个 https://m.toutiao.com/is/iRNabcdE/ 不错") == "https://m.toutiao.com/is/iRNabcdE");
        Check("无意义输入返回 null", ToutiaoParser.TryExtractGroupId("随便一段没有链接的话") is null);

        // ----- token 兜底：顶层字段缺失时从 content 里捞 -----
        const string noTopToken = """
        {"success":true,"data":{"gid":"1","video_id":"v123","title":"兜底",
          "content":"<p><div class=\"tt-video-box\" tt-videoid='v123' data-token='QUNUSU9OPUdldFBsYXlJbmZv'></div></p>"}}
        """;
        try
        {
            var m = ToutiaoParser.ParseInfo(noTopToken);
            Check("顶层无 token 时从 content 捞", m.PlayToken == "QUNUSU9OPUdldFBsYXlJbmZv", m.PlayToken);
            Check("video_id 也从 content 捞", m.VideoId == "v123", m.VideoId);
        }
        catch (Exception ex)
        {
            Check("顶层无 token 时从 content 捞", false, ex.Message);
        }

        // ----- 异常路径 -----
        Check("空内容抛 ToutiaoException", Throws<ToutiaoException>(() => ToutiaoParser.ParseInfo("")));
        Check("非法 JSON 抛 ToutiaoException",
            Throws<ToutiaoException>(() => ToutiaoParser.ParseInfo("not json at all")));
        Check("success=false 抛 ToutiaoException",
            Throws<ToutiaoException>(() => ToutiaoParser.ParseInfo("""{"success":false,"data":{}}""")));
        Check("缺 data 抛 ToutiaoException",
            Throws<ToutiaoException>(() => ToutiaoParser.ParseInfo("""{"success":true}""")));
        Check("图文帖（无 video_id）抛 ToutiaoException",
            Throws<ToutiaoException>(() => ToutiaoParser.ParseInfo("""{"success":true,"data":{"gid":"1","title":"图文"}}""")));
        Check("缺 token 抛 ToutiaoException",
            Throws<ToutiaoException>(() =>
                ToutiaoParser.ParseInfo("""{"success":true,"data":{"gid":"1","video_id":"v1","title":"x"}}""")));
        Check("空 token 抛 ToutiaoException", Throws<ToutiaoException>(() => ToutiaoParser.DecodePlayToken("")));
        Check("非 base64 token 抛 ToutiaoException",
            Throws<ToutiaoException>(() => ToutiaoParser.DecodePlayToken("!!!not base64!!!")));
        Check("base64 里没 GetPlayInfo 抛 ToutiaoException",
            Throws<ToutiaoException>(() => ToutiaoParser.DecodePlayToken(Convert.ToBase64String("{\"x\":1}"u8.ToArray()))));
        Check("playinfo 非法 JSON 抛 ToutiaoException",
            Throws<ToutiaoException>(() => ToutiaoParser.ParsePlayInfo("xxx")));
        Check("playinfo 缺 Result 抛 ToutiaoException",
            Throws<ToutiaoException>(() => ToutiaoParser.ParsePlayInfo("""{"ResponseMetadata":{}}""")));
        Check("playinfo 档位为空抛 ToutiaoException",
            Throws<ToutiaoException>(() =>
                ToutiaoParser.ParsePlayInfo("""{"Result":{"Data":{"Status":10,"PlayInfoList":[]}}}""")));
        Check("playinfo Status 异常抛 ToutiaoException",
            Throws<ToutiaoException>(() =>
                ToutiaoParser.ParsePlayInfo("""{"Result":{"Data":{"Status":4,"PlayInfoList":[{"MainPlayUrl":"https://x/a.mp4"}]}}}""")));
    }

    /// <summary>
    /// 真实联网验证：走完整的两段请求（info -> GetPlayInfo），再对最高档地址发一个
    /// Range 请求，确认返回 206 + video/mp4 而不是 HTML 错误页。
    /// 只在命令行显式传 --net-toutiao 时执行，默认回归保持离线。
    /// </summary>
    /// <summary>
    /// 腾讯视频解析回归。三份样本都是 2026-11-04 从真实接口抓的：
    ///   testdata/tencent_info.json      —— float_vinfo2 专辑响应（62 集免费短剧）
    ///   testdata/tencent_playinfo.json  —— 免费集的 getinfo 响应
    ///   testdata/tencent_vip.json       —— 会员内容的 getinfo 响应，用来钉死 VIP 拦截
    /// 后两份的 vkey 会过期，但解析器只做字符串处理，不校验签名，所以不影响回归。
    /// </summary>
    private static void TencentSection()
    {
        // ---------- 链接识别 ----------
        var (c1, v1) = TencentParser.ExtractIds("https://v.qq.com/x/cover/mzc00327xgk7sai/b3298637u51.html");
        Check("cover+vid 完整链接", c1 == "mzc00327xgk7sai" && v1 == "b3298637u51", $"{c1}/{v1}");

        var (c2, v2) = TencentParser.ExtractIds("https://v.qq.com/x/cover/mzc00327xgk7sai.html");
        Check("只有 cover 的链接", c2 == "mzc00327xgk7sai" && v2 == "", $"{c2}/{v2}");

        var (c3, v3) = TencentParser.ExtractIds("https://v.qq.com/x/page/b3298637u51.html");
        Check("x/page 单集链接", c3 == "" && v3 == "b3298637u51", $"{c3}/{v3}");

        var (c4, v4) = TencentParser.ExtractIds("b3298637u51");
        Check("裸 vid", c4 == "" && v4 == "b3298637u51", $"{c4}/{v4}");

        var (c5, v5) = TencentParser.ExtractIds("一段没有任何链接的文字");
        Check("无效文本返回空", c5 == "" && v5 == "");

        // ---------- 专辑 ----------
        var infoPath = Path.Combine(AppContext.BaseDirectory, "testdata", "tencent_info.json");
        var playPath = Path.Combine(AppContext.BaseDirectory, "testdata", "tencent_playinfo.json");
        var vipPath = Path.Combine(AppContext.BaseDirectory, "testdata", "tencent_vip.json");
        Check("专辑样本存在", File.Exists(infoPath), infoPath);
        Check("免费集样本存在", File.Exists(playPath), playPath);
        Check("会员样本存在", File.Exists(vipPath), vipPath);
        if (!File.Exists(infoPath) || !File.Exists(playPath) || !File.Exists(vipPath)) return;

        TencentVideo album;
        try
        {
            album = TencentParser.ParseAlbum(File.ReadAllText(infoPath), "mzc00327xgk7sai");
            Check("解析专辑不抛异常", true);
        }
        catch (Exception ex)
        {
            Check("解析专辑不抛异常", false, ex.Message);
            return;
        }

        Check("专辑 ID", album.Cid == "mzc00327xgk7sai", album.Cid);
        Check("标题非空", album.Title.Length > 0, album.Title);
        Check("集数 = 62", album.Episodes.Count == 62, album.Episodes.Count.ToString());
        Check("每集都有 vid", album.Episodes.All(e => e.Vid.Length >= 8));

        // ---------- 免费集 ----------
        TencentEpisode free;
        try
        {
            free = TencentParser.ParsePlayInfo(File.ReadAllText(playPath), "b3298637u51");
            Check("解析免费集不抛异常", true);
        }
        catch (Exception ex)
        {
            Check("解析免费集不抛异常", false, ex.Message);
            return;
        }

        Check("免费集可下载", free.CanDownload, free.BlockReason);
        Check("免费集标题含集数", free.Title.Contains("第") && free.Title.Contains("集"), free.Title);
        Check("免费集有时长", free.Duration.TotalSeconds > 10, free.Duration.ToString());

        // 这份样本是未登录、defn=fhd 抓的：fl 里列了 1080P，但接口只给到 720P，
        // 解析器要把 1080P 过滤掉，只留下真正能拿到的档位
        Check("真实上限识别为 720P", free.MaxAvailableHeight == 720, free.MaxAvailableHeight.ToString());
        Check("过滤掉拿不到的 1080P，剩 2 档", free.Qualities.Count == 2, free.Qualities.Count.ToString());

        if (free.Qualities.Count == 2)
        {
            Check("档位按分辨率降序",
                free.Qualities.Select(q => q.Height).SequenceEqual(new[] { 720, 480 }),
                string.Join(",", free.Qualities.Select(q => q.Height)));
            Check("defn 依次是 shd/hd",
                free.Qualities.Select(q => q.Defn).SequenceEqual(new[] { "shd", "hd" }),
                string.Join(",", free.Qualities.Select(q => q.Defn)));
            Check("解析阶段不带地址", free.Qualities.All(q => q.Url.Length == 0));
            Check("720P 大小约 4.2MB",
                free.Qualities[0].SizeBytes > 4_000_000 && free.Qualities[0].SizeBytes < 5_000_000,
                free.Qualities[0].SizeBytes.ToString());
        }

        // ---------- 单档位取址 ----------
        // 这份样本是 defn=shd 抓的，应当解出 720P 的地址和 3 条备用
        try
        {
            var (url, backups, actualHeight) = TencentParser.ParseStreamUrl(File.ReadAllText(playPath));
            Check("从样本解出直链", url.StartsWith("http"), url[..Math.Min(50, url.Length)]);
            Check("直链指向 mp4 文件", url.Contains(".mp4?vkey="));
            Check("解出备用地址", backups.Length >= 1, backups.Length.ToString());
            Check("实际档位识别为 720P", actualHeight == 720, actualHeight.ToString());
        }
        catch (Exception ex)
        {
            Check("从样本解出直链", false, ex.Message);
        }

        // ---------- 会员内容必须被挡住 ----------
        TencentEpisode vip;
        try
        {
            vip = TencentParser.ParsePlayInfo(File.ReadAllText(vipPath), "k4102b5k1da");
            Check("解析会员样本不抛异常", true);
        }
        catch (Exception ex)
        {
            Check("解析会员样本不抛异常", false, ex.Message);
            return;
        }

        Check("会员内容判定为不可下载", !vip.CanDownload, vip.CanDownload.ToString());
        Check("拦下原因是需会员", vip.BlockReason.Contains("会员"), vip.BlockReason);
        Check("会员内容不返回任何直链", vip.Qualities.Count == 0, vip.Qualities.Count.ToString());

        // ---------- 前缀/尾部杂质 ----------
        var dirty = "QZOutputJson={\"em\":0,\"exem\":0}" + "\r\n";
        var clean = TencentParser.ExtractJsonObject(dirty);
        Check("剥掉 QZOutputJson 前缀", clean.StartsWith("{") && clean.EndsWith("}"), clean);
    }

    /// <summary>腾讯视频联网验证：真解析一次，再用 DownloadEngine 确认直链可下载。</summary>
    private static async Task TencentLiveAsync(string url, bool download = false)
    {
        using var client = new TencentClient();

        TencentVideo v;
        try
        {
            var sw = Stopwatch.StartNew();
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                if (p.Done == p.Total || p.Done % 10 == 0)
                    Console.WriteLine($"      解析进度 {p.Done}/{p.Total}");
            });
            v = await client.FetchAsync(url, progress);
            sw.Stop();
            Check("联网解析成功", true);
            Console.WriteLine($"      耗时 {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"      {v.Title}");
            Console.WriteLine($"      {v.InfoText}");
        }
        catch (Exception ex)
        {
            Check("联网解析成功", false, ex.Message);
            return;
        }

        Check("拿到专辑 ID", v.Cid.Length >= 8, v.Cid);
        Check("集数大于 0", v.Episodes.Count > 0, v.Episodes.Count.ToString());
        Check("有免费集", v.FreeCount > 0, v.FreeCount.ToString());

        // 打印前 5 集
        foreach (var e in v.Episodes.Take(5))
            Console.WriteLine($"      #{e.Number,-3} {e.DisplayTitle}  {e.DurationText}  " +
                              (e.CanDownload ? $"{e.Qualities.Count} 档" : "× " + e.BlockReason));

        // 集号必须递增（接口给的顺序是乱的，排序得生效）
        var numbers = v.Episodes.Select(e => e.Number).Where(n => n > 0).ToList();
        if (numbers.Count == v.Episodes.Count && numbers.Count > 1)
            Check("集号递增", numbers.SequenceEqual(numbers.OrderBy(n => n)), string.Join(",", numbers.Take(6)));

        var pick = v.Episodes.FirstOrDefault(e => e.CanDownload && e.Qualities.Count > 0);
        if (pick is null)
        {
            Check("有可下载的集", false, "全部被拦");
            return;
        }

        var q = pick.Qualities[0];
        Console.WriteLine($"      试下：{pick.DisplayTitle} / {q.DisplayName}");

        TencentQuality stream;
        try
        {
            stream = await client.FetchStreamAsync(pick.Vid, q);
            Check("按档位取到直链", stream.Url.Length > 0, stream.Url[..Math.Min(60, stream.Url.Length)]);
            if (stream.Height > 0 && stream.Height != q.Height)
                Console.WriteLine($"      注意：请求 {q.Height}P，实际给的是 {stream.Height}P（未登录会被降档）");
        }
        catch (Exception ex)
        {
            Check("按档位取到直链", false, ex.Message);
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("User-Agent", TencentClient.UserAgent);
            using var req = new HttpRequestMessage(HttpMethod.Get, stream.Url);
            req.Headers.Referrer = new Uri(TencentClient.Referer);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 262143);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";

            Check("直链可下载", resp.IsSuccessStatusCode, $"HTTP {(int)resp.StatusCode}");
            Check("返回 206 分段", (int)resp.StatusCode == 206, ((int)resp.StatusCode).ToString());
            Check("内容类型是视频", type.Contains("video"), type);
            Check("在 buffer 范围内取到字节", bytes.Length > 0, bytes.Length.ToString());

            var brand = bytes.Length >= 12 ? Encoding.ASCII.GetString(bytes, 4, 4) : "";
            Check("头部是 ftyp box", brand == "ftyp", brand);
        }
        catch (Exception ex)
        {
            Check("直链可下载", false, ex.Message);
        }

        if (download) await TencentDownloadAsync(pick.Vid, pick.Qualities[^1]);
    }

    /// <summary>
    /// 用真正的 DownloadEngine 把一集落一次盘。
    /// 走的是分段 Range + 后备地址那套，和界面上下载完全同一条路径。
    /// 挑最小档，几十秒能跑完。
    /// </summary>
    private static async Task TencentDownloadAsync(string vid, TencentQuality quality)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "jjprobe");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "tencent_" + vid + ".mp4");
        if (File.Exists(outPath)) File.Delete(outPath);

        try
        {
            using var client = new TencentClient();
            var stream = await client.FetchStreamAsync(vid, quality);
            Console.WriteLine($"      下载 {quality.DisplayName}……");

            var sw = Stopwatch.StartNew();
            var limiter = new SpeedLimiter();
            using var engine = new DownloadEngine(limiter);

            var got = await engine.DownloadAsync(new DownloadRequest
            {
                Url = stream.Url,
                BackupUrls = stream.BackupUrls,
                DestinationPath = outPath,
                Referer = TencentClient.Referer,
                Segments = 8
            }, null, default);
            sw.Stop();

            Check("DownloadEngine 下载完成", File.Exists(outPath));
            if (!File.Exists(outPath)) return;

            var len = new FileInfo(outPath).Length;
            Check("落盘字节数与返回值一致", len == got, $"{len} vs {got}");
            Check("文件大于 1MB", len > 1_000_000, $"{len / 1024.0 / 1024:F1} MB");

            using var fs = File.OpenRead(outPath);
            var head = new byte[12];
            _ = fs.Read(head, 0, 12);
            Check("产物头部是 ftyp", Encoding.ASCII.GetString(head, 4, 4) == "ftyp",
                Encoding.ASCII.GetString(head, 4, 4));

            Console.WriteLine($"      {len / 1024.0 / 1024:F1} MB / {sw.ElapsedMilliseconds} ms → {outPath}");
        }
        catch (Exception ex)
        {
            Check("DownloadEngine 下载完成", false, ex.Message);
        }
    }

    /// <summary>
    /// YouTube 解析回归。样本是 2026-11-19 从真实 yt-dlp -J 输出抓的：
    ///   testdata/youtube_rick.json —— Rick Astley 4K 视频的完整格式表
    /// 解析逻辑只做字符串处理（过滤 storyboard、排序、映射），不联网，可脱网测。
    /// </summary>
    private static void YoutubeSection()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "youtube_rick.json");
        Check("YouTube 样本存在", File.Exists(path), path);
        if (!File.Exists(path)) return;

        YoutubeVideo v;
        try
        {
            v = YoutubeClient.ParseJson(File.ReadAllText(path), "https://www.youtube.com/watch?v=dQw4w9WgXcQ");
            Check("解析 YouTube JSON 不抛异常", true);
        }
        catch (Exception ex)
        {
            Check("解析 YouTube JSON 不抛异常", false, ex.Message);
            return;
        }

        Check("视频 ID", v.VideoId == "dQw4w9WgXcQ", v.VideoId);
        Check("标题非空", v.Title.Length > 0, v.Title[..Math.Min(40, v.Title.Length)]);
        Check("时长约 213s", Math.Abs(v.Duration.TotalSeconds - 213) < 5, v.Duration.ToString());

        // storyboard（mhtml）必须被过滤掉
        Check("没有 storyboard 格式", v.Formats.All(f => f.Ext != "mhtml"),
            v.Formats.Count(f => f.Ext == "mhtml").ToString());
        Check("没有 none 编码的占位格式",
            v.Formats.All(f => f.VCodec is not "none" or "" || f.ACodec is not "none" or ""),
            v.Formats.Count(f => f.VCodec is "none" && f.ACodec is "none").ToString());

        // 有真实视频流和音频流
        Check("有视频流", v.Formats.Any(f => f.Height > 0), v.Formats.Count(f => f.Height > 0).ToString());
        Check("有音频流", v.Formats.Any(f => f.IsAudioOnly), v.Formats.Count(f => f.IsAudioOnly).ToString());
        Check("最高到 4K", v.Formats.Any(f => f.Height >= 2160), v.Formats.Max(f => f.Height).ToString());

        // 排序：视频流按分辨率降序
        var videoHeights = v.Formats.Where(f => f.Height > 0).Select(f => f.Height).ToList();
        Check("视频流按分辨率降序", videoHeights.SequenceEqual(videoHeights.OrderByDescending(h => h)),
            string.Join(",", videoHeights.Take(8)));

        // 音频流带码率
        var audio = v.Formats.First(f => f.IsAudioOnly);
        Check("音频流有码率", audio.AudioBitrate > 0, audio.AudioBitrate.ToString("F0"));

        // 显示前几个
        foreach (var f in v.Formats.Take(6))
            Console.WriteLine($"      · {f.DisplayName}");

        // Cookie 落盘 / 读取往返（不联网，验证文件读写与路径一致性）
        var cookiePath = Path.Combine(AppContext.BaseDirectory, "youtube-cookies.txt");
        var hadCookieBefore = File.Exists(cookiePath);
        var savedBefore = hadCookieBefore ? File.ReadAllText(cookiePath) : null;

        try
        {
            var sample = "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tfake\n";
            var status = YoutubeClient.SaveCookie(sample);
            Check("保存 Cookie 返回状态", status.Contains("已保存"), status);
            Check("Cookie 文件已落盘", File.Exists(cookiePath), cookiePath);
            Check("CookiePath 返回该文件", YoutubeClient.CookiePath == cookiePath, YoutubeClient.CookiePath ?? "<null>");

            var cleared = YoutubeClient.SaveCookie(null);
            Check("清空 Cookie 返回状态", cleared.Contains("未配置"), cleared);
            Check("清空后文件删除", !File.Exists(cookiePath), cookiePath);
            Check("清空后 CookiePath 为 null", YoutubeClient.CookiePath is null, YoutubeClient.CookiePath ?? "<null>");
        }
        finally
        {
            // 还原测试前状态，不污染用户的 cookie
            if (savedBefore is not null) File.WriteAllText(cookiePath, savedBefore);
            else if (File.Exists(cookiePath)) File.Delete(cookiePath);
        }
    }

    /// <summary>YouTube 联网验证：真调 yt-dlp 解析一次（不下载，避免消耗带宽和触发风控）。</summary>
    private static async Task YoutubeLiveAsync(string url)
    {
        using var client = new YoutubeClient();
        if (!client.IsAvailable)
        {
            Check("yt-dlp 可用", false, client.AvailabilityHint);
            return;
        }
        Check("yt-dlp 可用", true);

        YoutubeVideo v;
        try
        {
            var sw = Stopwatch.StartNew();
            v = await client.FetchAsync(url);
            sw.Stop();
            Check("联网解析成功", true);
            Console.WriteLine($"      耗时 {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"      {v.Title}");
            Console.WriteLine($"      {v.InfoText}");
        }
        catch (Exception ex)
        {
            Check("联网解析成功", false, ex.Message);
            return;
        }

        Check("有真实视频流", v.Formats.Any(f => f.Height > 0),
            $"共 {v.Formats.Count} 种，最高 {v.Formats.Max(f => f.Height)}P");
        Check("没有 storyboard 混入", v.Formats.All(f => f.Ext != "mhtml"));

        foreach (var f in v.Formats.Take(5))
            Console.WriteLine($"      · {f.DisplayName}");
    }

    private static async Task ToutiaoLiveAsync(string url, bool download = false, string ffmpegPath = "")
    {
        using var client = new ToutiaoClient();

        ToutiaoVideo v;
        try
        {
            var sw = Stopwatch.StartNew();
            v = await client.FetchAsync(url);
            sw.Stop();
            Check("联网解析成功", true);
            Console.WriteLine($"      耗时 {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"      {v.Title}");
            Console.WriteLine($"      {v.InfoText}");
            foreach (var s in v.Streams)
                Console.WriteLine($"      · {s.DisplayName}   {s.Width}x{s.Height} {s.Codec}");
        }
        catch (Exception ex)
        {
            Check("联网解析成功", false, ex.Message);
            return;
        }

        Check("联网后拿到条目 ID", v.GroupId.Length >= 15, v.GroupId);
        Check("联网后拿到视频 ID", v.VideoId.StartsWith("v"), v.VideoId);
        Check("联网后档位非空", v.Streams.Count > 0, v.Streams.Count.ToString());

        if (v.Streams.Count == 0) return;

        // 真正发一次请求，确认地址可用、内容类型正确
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Add("User-Agent", ToutiaoClient.UserAgent);
            using var req = new HttpRequestMessage(HttpMethod.Get, v.Streams[0].Url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 262143);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";

            Check("最高档地址可下载", resp.IsSuccessStatusCode, $"HTTP {(int)resp.StatusCode}");
            Check("返回 206 分段", (int)resp.StatusCode == 206, ((int)resp.StatusCode).ToString());
            Check("内容类型是 video/mp4", type.Contains("video"), type);
            Check("实际拿到 256KB", bytes.Length == 262144, bytes.Length.ToString());

            // ISO BMFF 的第 5-8 字节是 ftyp，用来确认拿到的真是 mp4 而不是错误页
            var brand = bytes.Length >= 12 ? Encoding.ASCII.GetString(bytes, 4, 4) : "";
            Check("头部是 ftyp box", brand == "ftyp", brand);
        }
        catch (Exception ex)
        {
            Check("最高档地址可下载", false, ex.Message);
        }

        if (download) await ToutiaoDownloadAsync(v, ffmpegPath);
    }

    /// <summary>
    /// 用真正的 DownloadEngine 把最小档落一次盘，再用 ffprobe 回读，
    /// 确认分段下载 + 后备地址 + 合并这套逻辑在头条的 CDN 上也能跑通。
    /// </summary>
    private static async Task ToutiaoDownloadAsync(ToutiaoVideo v, string ffmpegPath)
    {
        // 挑最小档，省时间
        var small = v.Streams[^1];
        var dir = Path.Combine(Path.GetTempPath(), "jjmedia-toutiao");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, $"toutiao_{v.GroupId}.mp4");
        if (File.Exists(outPath)) File.Delete(outPath);

        Console.WriteLine($"      下载 {small.Label} → {outPath}");

        var sw = Stopwatch.StartNew();
        try
        {
            var limiter = new SpeedLimiter();
            using var engine = new DownloadEngine(limiter);

            await engine.DownloadAsync(new DownloadRequest
            {
                Url = small.Url,
                BackupUrls = small.BackupUrls,
                DestinationPath = outPath,
                Referer = ToutiaoClient.Referer,
                Segments = 4
            }, progress: null, CancellationToken.None);

            sw.Stop();
            Check("DownloadEngine 下载成功", true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Check("DownloadEngine 下载成功", false, ex.Message);
            return;
        }

        var len = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
        Check("产物已落盘", len > 0, len.ToString());
        Check("大小与接口给的 Size 一致", small.DataSize > 0 && len == small.DataSize,
            $"实际 {len} / 接口 {small.DataSize}");
        Console.WriteLine($"      耗时 {sw.ElapsedMilliseconds} ms  大小 {len / 1024.0 / 1024.0:F2} MB");

        // ffprobe 能读到正确时长，说明文件是完整的 mp4（不是拼坏的）
        var probe = FfmpegRunner.ResolveFfprobe(ffmpegPath);
        if (probe is null || !File.Exists(probe))
        {
            Check("ffprobe 可用（可选用）", true, "跳过");
            return;
        }

        var info = await FfmpegRunner.ProbeAsync(probe, outPath);
        if (info is null)
        {
            Check("ffprobe 能读产物", false, "返回 null");
            return;
        }

        Check("ffprobe 能读产物", true);
        Check("有视频流", info.HasVideo, info.VideoCodec);
        Check("时长与解析结果一致",
            Math.Abs(info.Duration.TotalSeconds - v.Duration.TotalSeconds) < 1.0,
            $"产物 {info.Duration.TotalSeconds:F1}s / 解析 {v.Duration.TotalSeconds:F1}s");
        Check("分辨率与档位一致",
            Math.Abs(info.Width - small.Width) <= 2 && Math.Abs(info.Height - small.Height) <= 2,
            $"{info.Width}x{info.Height} vs {small.Width}x{small.Height}");
        Console.WriteLine($"      {info.VideoCodec} {info.Width}x{info.Height} + {info.AudioCodec}  {info.Duration.TotalSeconds:F2}s");
    }

    /// <summary>取 --name value 形式的参数值。</summary>
    private static string? ArgValue(string[] argv, string name)
    {
        for (var i = 0; i < argv.Length - 1; i++)
            if (string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase))
                return argv[i + 1];
        return null;
    }

    /// <summary>去掉所有 --选项 及其值，剩下的才是位置参数。</summary>
    private static string[] PositionalArgs(string[] argv, params string[] options)
    {
        var list = new List<string>();
        for (var i = 0; i < argv.Length; i++)
        {
            if (options.Any(o => string.Equals(o, argv[i], StringComparison.OrdinalIgnoreCase)))
            {
                i++; // 连同它的值一起跳
                continue;
            }
            if (argv[i].StartsWith("--", StringComparison.Ordinal)) continue;
            list.Add(argv[i]);
        }
        return list.ToArray();
    }

    private static bool Throws<T>(Action act) where T : Exception
    {
        try { act(); return false; }
        catch (T) { return true; }
        catch { return false; }
    }

    private static void Check(string name, bool ok, string? detail = null)    {
        if (ok) _pass++;
        else _fail++;

        var mark = ok ? "  OK  " : " FAIL ";
        var color = ok ? ConsoleColor.Green : ConsoleColor.Red;
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write($"  [{mark}]");
        Console.ForegroundColor = prev;
        Console.Write($" {name}");

        if (!ok && !string.IsNullOrWhiteSpace(detail))
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write($"  ← {Flatten(detail)}");
            Console.ForegroundColor = prev;
        }
        Console.WriteLine();
    }

    private static string Flatten(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    private static string Tail(string log)
    {
        if (string.IsNullOrWhiteSpace(log)) return "(无输出)";
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines[^1].Trim();
    }

    private static string Bytes(string path)
    {
        if (!File.Exists(path)) return "文件不存在";
        var len = new FileInfo(path).Length;
        return len > 1024 * 1024 ? $"{len / 1024.0 / 1024.0:F1} MB" : $"{len / 1024.0:F1} KB";
    }
}
