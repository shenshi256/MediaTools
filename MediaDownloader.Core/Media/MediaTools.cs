using System.Text;

namespace MediaDownloader.Core;

/// <summary>截取结果：带上回读到的真实时长，用于向界面解释“实际切出来多长”。</summary>
public sealed class TrimResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = "";
    public TimeSpan RequestedStart { get; init; }
    public TimeSpan RequestedEnd { get; init; }

    /// <summary>ffprobe 回读的真实时长；未校验时为 0。</summary>
    public TimeSpan ActualDuration { get; init; }

    public bool Precise { get; init; }

    public TimeSpan RequestedDuration => RequestedEnd - RequestedStart;

    /// <summary>无损模式下起点对齐到关键帧造成的前移量（精确模式应≈0）。</summary>
    public TimeSpan StartShift => ActualDuration > TimeSpan.Zero
        ? ActualDuration - RequestedDuration
        : TimeSpan.Zero;

    /// <summary>起点是否被前移了超过 0.5 秒。</summary>
    public bool StartSnapped => StartShift > TimeSpan.FromSeconds(0.5);

    public string Summary
    {
        get
        {
            if (!Success) return "截取失败";
            if (ActualDuration <= TimeSpan.Zero) return $"截取完成（时长未校验，设定 {RequestedDuration:hh\\:mm\\:ss}）";
            if (Precise) return $"精确截取完成，时长 {ActualDuration:hh\\:mm\\:ss}";
            if (StartSnapped)
                return $"快速截取完成，实际时长 {ActualDuration:hh\\:mm\\:ss}：" +
                       $"起点已前移到最近关键帧，比设定多 {StartShift.TotalSeconds:F1} 秒（结束点仍准确）";
            return $"快速截取完成，时长 {ActualDuration:hh\\:mm\\:ss}";
        }
    }
}

/// <summary>
/// 视频截取：默认无损（-c copy，切在关键帧），可选精确模式（重编码，帧级准确）。
/// <para>
/// 无损模式的固有约束：copy 只能从关键帧开始写，而 ffmpeg 的 -t 是从“请求起点”算起，
/// 因此产物范围是 [起点之前最近的关键帧, 请求终点]。即<b>结束点准确，起点最多提前一个 GOP</b>。
/// 这里通过回读真实时长把这件事量化出来，而不是假装精确。
/// </para>
/// </summary>
public static class VideoTrimmer
{
    public static async Task<TrimResult> TrimAsync(string ffmpegPath, string? ffprobePath,
        string input, string output, TimeSpan start, TimeSpan end, bool precise,
        IProgress<FfmpegProgress>? progress, CancellationToken ct)
    {
        if (!File.Exists(input)) throw new FileNotFoundException("找不到输入文件", input);
        if (end <= start) throw new ArgumentException("结束时间必须大于开始时间");
        if (start < TimeSpan.Zero) start = TimeSpan.Zero;

        var duration = end - start;
        var dir = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(output)) File.Delete(output);

        List<string> args;
        if (precise)
        {
            // 精确模式：-ss 放在 -i 之后，逐帧解码丢弃，帧级准确但耗时接近实时
            args = new List<string>
            {
                "-i", input,
                "-ss", FfmpegRunner.ToFfmpegTime(start),
                "-t", FfmpegRunner.ToFfmpegTime(duration),
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                "-c:a", "aac", "-b:a", "192k",
                "-movflags", "+faststart",
                "-y", output
            };
        }
        else
        {
            // 无损模式：-ss 放在 -i 之前，快速定位到最近关键帧
            args = new List<string>
            {
                "-ss", FfmpegRunner.ToFfmpegTime(start),
                "-i", input,
                "-t", FfmpegRunner.ToFfmpegTime(duration),
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
                "-movflags", "+faststart",
                "-y", output
            };
        }

        var run = await FfmpegRunner.RunAsync(ffmpegPath, args, duration, progress, ct, "截取");

        // 回读真实时长：copy 模式下产物起点可能早于请求起点，这里把差异量化出来
        var actual = TimeSpan.Zero;
        if (run.Success && !string.IsNullOrEmpty(ffprobePath))
        {
            var probe = await FfmpegRunner.ProbeAsync(ffprobePath, output, ct);
            if (probe is not null) actual = probe.Duration;
        }

        return new TrimResult
        {
            Success = run.Success,
            Output = run.Output,
            RequestedStart = start,
            RequestedEnd = end,
            ActualDuration = actual,
            Precise = precise
        };
    }
}

/// <summary>视频合并：concat demuxer。仅参数一致时可无损拼接。</summary>
public static class VideoMerger
{
    public sealed class MergeCheck
    {
        public bool CanCopy { get; init; }
        public string Message { get; init; } = "";
        public IReadOnlyList<MediaProbe> Probes { get; init; } = Array.Empty<MediaProbe>();
        public TimeSpan TotalDuration { get; init; }
    }

    public static async Task<MergeCheck> CheckAsync(string ffprobePath, IReadOnlyList<string> inputs,
        CancellationToken ct = default)
    {
        if (inputs.Count < 2)
            return new MergeCheck { CanCopy = false, Message = "至少需要两个文件才能合并" };

        var probes = new List<MediaProbe>();
        foreach (var f in inputs)
        {
            var p = await FfmpegRunner.ProbeAsync(ffprobePath, f, ct);
            if (p is null)
                return new MergeCheck { CanCopy = false, Message = $"无法读取：{Path.GetFileName(f)}", Probes = probes };
            probes.Add(p);
        }

        var total = TimeSpan.FromTicks(probes.Sum(p => p.Duration.Ticks));
        var sig = probes[0].Signature;
        var bad = probes.Skip(1).FirstOrDefault(p => p.Signature != sig);

        if (bad is not null)
        {
            return new MergeCheck
            {
                CanCopy = false,
                Probes = probes,
                TotalDuration = total,
                Message = $"参数不一致，无法无损拼接。\n{probes[0].FileName}：{probes[0].Describe()}\n{bad.FileName}：{bad.Describe()}"
            };
        }

        return new MergeCheck
        {
            CanCopy = true,
            Probes = probes,
            TotalDuration = total,
            Message = $"参数一致，可无损拼接（{probes.Count} 个文件，总时长 {total:hh\\:mm\\:ss}）"
        };
    }

    public static async Task<FfmpegResult> MergeAsync(string ffmpegPath, IReadOnlyList<string> inputs, string output,
        TimeSpan totalDuration, IProgress<FfmpegProgress>? progress, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(output)) File.Delete(output);

        // concat 列表文件：路径统一用正斜杠，单引号按 ffmpeg 语法转义
        var listPath = Path.Combine(Path.GetTempPath(), $"jjconcat_{Guid.NewGuid():N}.txt");
        var lines = inputs.Select(p => $"file '{p.Replace('\\', '/').Replace("'", @"'\''")}'");
        await File.WriteAllLinesAsync(listPath, lines, new UTF8Encoding(false), ct);

        try
        {
            var args = new List<string>
            {
                "-f", "concat", "-safe", "0",
                "-i", listPath,
                "-c", "copy",
                "-movflags", "+faststart",
                "-y", output
            };
            return await FfmpegRunner.RunAsync(ffmpegPath, args, totalDuration, progress, ct, "合并");
        }
        finally
        {
            try { File.Delete(listPath); } catch { /* 忽略 */ }
        }
    }
}

public enum AudioMode
{
    /// <summary>提取音频为 m4a（无损复制音轨）。</summary>
    ExtractM4a,
    /// <summary>提取音频并转成 mp3。</summary>
    ExtractMp3,
    /// <summary>去掉音轨，只保留视频。</summary>
    StripAudio
}

/// <summary>音视频分离：提音频 / 去音轨。</summary>
public static class AudioSeparator
{
    public static string SuggestOutput(string input, AudioMode mode)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
        var name = Path.GetFileNameWithoutExtension(input);
        return mode switch
        {
            AudioMode.ExtractM4a => Path.Combine(dir, name + ".m4a"),
            AudioMode.ExtractMp3 => Path.Combine(dir, name + ".mp3"),
            _ => Path.Combine(dir, name + "_无音轨.mp4")
        };
    }

    public static async Task<FfmpegResult> RunAsync(string ffmpegPath, string input, string output,
        AudioMode mode, TimeSpan duration, IProgress<FfmpegProgress>? progress, CancellationToken ct)
    {
        if (!File.Exists(input)) throw new FileNotFoundException("找不到输入文件", input);

        var dir = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(output)) File.Delete(output);

        var args = mode switch
        {
            AudioMode.ExtractM4a => new List<string> { "-i", input, "-vn", "-c:a", "copy", "-y", output },
            AudioMode.ExtractMp3 => new List<string>
            {
                "-i", input, "-vn", "-c:a", "libmp3lame", "-q:a", "2", "-y", output
            },
            _ => new List<string> { "-i", input, "-an", "-c:v", "copy", "-y", output }
        };

        var stage = mode switch
        {
            AudioMode.ExtractM4a => "提取音频",
            AudioMode.ExtractMp3 => "转码 MP3",
            _ => "去除音轨"
        };

        return await FfmpegRunner.RunAsync(ffmpegPath, args, duration, progress, ct, stage);
    }

    /// <summary>音视分离的两个产物建议路径。</summary>
    public static (string Video, string Audio) SuggestSplitOutputs(string input, AudioMode audioMode)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
        var name = Path.GetFileNameWithoutExtension(input);
        var audioExt = audioMode == AudioMode.ExtractM4a ? ".m4a" : ".mp3";
        return (Path.Combine(dir, name + "_视频.mp4"), Path.Combine(dir, name + "_音频" + audioExt));
    }

    public sealed class SplitResult
    {
        public bool Success { get; init; }
        public bool AudioDone { get; init; }
        public bool VideoDone { get; init; }
        public string AudioOutput { get; init; } = "";
        public string VideoOutput { get; init; } = "";
        public string Message { get; init; } = "";
        public string Output { get; init; } = "";
    }

    /// <summary>
    /// 音视分离：一次产出两个文件——音频（mp3 转码，或 m4a 直接复制音轨）
    /// 与无声视频（视频流 -c copy，不重编码）。
    /// </summary>
    public static async Task<SplitResult> SplitAsync(string ffmpegPath, string input,
        string videoOutput, string audioOutput, AudioMode audioMode, TimeSpan duration,
        bool hasAudio, IProgress<FfmpegProgress>? progress, CancellationToken ct)
    {
        if (!File.Exists(input)) throw new FileNotFoundException("找不到输入文件", input);

        var audioDone = false;
        var log = "";

        if (hasAudio)
        {
            // 音频多为转码，给多一点进度区间
            var relay = new WeightedProgress(progress, 0, 55, "1/2 提取音频");
            var audio = await RunAsync(ffmpegPath, input, audioOutput, audioMode, duration, relay, ct);
            log = audio.Output;

            if (!audio.Success)
            {
                return new SplitResult
                {
                    Success = false,
                    AudioOutput = audioOutput,
                    VideoOutput = videoOutput,
                    Message = "提取音频失败",
                    Output = audio.Output
                };
            }

            audioDone = true;
        }

        var from = hasAudio ? 55 : 0;
        var relayVideo = new WeightedProgress(progress, from, 100, "2/2 分离视频");
        var video = await RunAsync(ffmpegPath, input, videoOutput, AudioMode.StripAudio, duration, relayVideo, ct);
        log += video.Output;

        if (!video.Success)
        {
            return new SplitResult
            {
                Success = false,
                AudioDone = audioDone,
                AudioOutput = audioDone ? audioOutput : "",
                VideoOutput = videoOutput,
                Message = audioDone ? "音频已生成，但分离视频失败" : "分离视频失败",
                Output = video.Output
            };
        }

        progress?.Report(new FfmpegProgress
        {
            Stage = "完成",
            Duration = duration,
            Position = duration,
            OverallPercent = 100
        });

        return new SplitResult
        {
            Success = true,
            AudioDone = audioDone,
            VideoDone = true,
            AudioOutput = audioDone ? audioOutput : "",
            VideoOutput = videoOutput,
            Message = audioDone
                ? "已生成 2 个文件：音频 + 无声视频"
                : "源文件没有音轨，只生成了无声视频",
            Output = log
        };
    }

    /// <summary>把子任务的 0-100% 映射到整体区间的百分比。</summary>
    private sealed class WeightedProgress : IProgress<FfmpegProgress>
    {
        private readonly IProgress<FfmpegProgress>? _inner;
        private readonly double _from;
        private readonly double _to;
        private readonly string _stage;

        public WeightedProgress(IProgress<FfmpegProgress>? inner, double from, double to, string stage)
        {
            _inner = inner;
            _from = from;
            _to = to;
            _stage = stage;
        }

        public void Report(FfmpegProgress value)
        {
            _inner?.Report(new FfmpegProgress
            {
                Position = value.Position,
                Duration = value.Duration,
                Stage = _stage,
                OverallPercent = _from + (_to - _from) * value.Percent / 100.0
            });
        }
    }
}
