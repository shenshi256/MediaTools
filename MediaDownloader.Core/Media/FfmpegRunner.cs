using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MediaDownloader.Core;

public sealed class FfmpegProgress
{
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public string Stage { get; init; } = "";

    /// <summary>
    /// 多阶段任务（如音视分离做两遍）可由上层直接给总百分比；
    /// 为 null 时才用 Position/Duration 现算。
    /// </summary>
    public double? OverallPercent { get; init; }

    public double Percent => OverallPercent ?? (Duration.TotalSeconds > 0
        ? Math.Clamp(Position.TotalSeconds / Duration.TotalSeconds * 100, 0, 100)
        : 0);
}

/// <summary>ffprobe 读出的媒体信息，用于合并前的一致性校验。</summary>
public sealed class MediaProbe
{
    public string FilePath { get; init; } = "";
    public string FileName => Path.GetFileName(FilePath);
    public TimeSpan Duration { get; init; }
    public string VideoCodec { get; init; } = "";
    public string AudioCodec { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public long SizeBytes { get; init; }

    public bool HasVideo => VideoCodec.Length > 0;
    public bool HasAudio => AudioCodec.Length > 0;

    /// <summary>合并能否直接 -c copy 的判断依据。</summary>
    public string Signature => $"{VideoCodec}/{Width}x{Height}/{AudioCodec}";

    public string Describe()
    {
        var v = HasVideo ? $"{VideoCodec} {Width}x{Height}" : "无视频流";
        var a = HasAudio ? AudioCodec : "无音频流";
        return $"{v} / {a} / {Duration:mm\\:ss}";
    }
}

/// <summary>
/// ffmpeg 统一执行器：带结构化进度（-progress pipe:1）、可取消。
/// </summary>
public static class FfmpegRunner
{
    public static string? ResolveFfmpeg(string? configured = null) => MediaMerger.FindFfmpeg(configured);

    public static string? ResolveFfprobe(string ffmpegPath)
    {
        var dir = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var probe = Path.Combine(dir, "ffprobe.exe");
        return File.Exists(probe) ? probe : null;
    }

    public static async Task<FfmpegResult> RunAsync(string ffmpegPath, IEnumerable<string> args,
        TimeSpan duration, IProgress<FfmpegProgress>? progress, CancellationToken ct, string stage = "")
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");
        foreach (var a in args) psi.ArgumentList.Add(a);

        var log = new StringBuilder();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.AppendLine(e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
        });

        double lastPercent = -1;
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                    line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                {
                    // out_time_ms 的数值实际是微秒（ffmpeg 历史遗留）
                    var raw = line[(line.IndexOf('=') + 1)..];
                    if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                    {
                        var pos = TimeSpan.FromMicroseconds(us);
                        var pct = duration.TotalSeconds > 0 ? pos.TotalSeconds / duration.TotalSeconds * 100 : 0;
                        if (pct - lastPercent >= 0.5 || pct >= 99.9)
                        {
                            lastPercent = pct;
                            progress?.Report(new FfmpegProgress
                            {
                                Position = pos,
                                Duration = duration,
                                Stage = stage
                            });
                        }
                    }
                }
                else if (line == "progress=end")
                {
                    break;
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 进程被杀导致管道中断，属于正常取消
        }

        try { await proc.WaitForExitAsync().ConfigureAwait(false); } catch { /* 忽略 */ }

        ct.ThrowIfCancellationRequested();

        if (proc.ExitCode == 0 && duration > TimeSpan.Zero)
        {
            progress?.Report(new FfmpegProgress
            {
                Position = duration,
                Duration = duration,
                Stage = stage
            });
        }

        return new FfmpegResult { Success = proc.ExitCode == 0, Output = log.ToString() };
    }

    public static async Task<MediaProbe?> ProbeAsync(string ffprobePath, string filePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return null;

        var psi = new ProcessStartInfo(ffprobePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in new[]
                 {
                     "-v", "error",
                     "-show_entries", "format=duration,size:stream=codec_type,codec_name,width,height,r_frame_rate",
                     "-of", "json", filePath
                 })
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var json = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double durationSec = 0;
            long size = 0;
            if (root.TryGetProperty("format", out var fmt))
            {
                if (fmt.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var dv)) durationSec = dv;
                if (fmt.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var sv)) size = sv;
            }

            string vcodec = "", acodec = "";
            int w = 0, h = 0;
            double fps = 0;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var st in streams.EnumerateArray())
                {
                    var type = st.TryGetProperty("codec_type", out var t) ? t.GetString() : "";
                    var codec = st.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                    if (type == "video" && vcodec.Length == 0)
                    {
                        vcodec = codec;
                        w = st.TryGetProperty("width", out var ww) ? ww.GetInt32() : 0;
                        h = st.TryGetProperty("height", out var hh) ? hh.GetInt32() : 0;
                        if (st.TryGetProperty("r_frame_rate", out var fr))
                        {
                            var parts = (fr.GetString() ?? "").Split('/');
                            if (parts.Length == 2 &&
                                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
                                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
                                den > 0)
                                fps = Math.Round(n / den, 2);
                        }
                    }
                    else if (type == "audio" && acodec.Length == 0)
                    {
                        acodec = codec;
                    }
                }
            }

            return new MediaProbe
            {
                FilePath = filePath,
                Duration = TimeSpan.FromSeconds(durationSec),
                VideoCodec = vcodec,
                AudioCodec = acodec,
                Width = w,
                Height = h,
                FrameRate = fps,
                SizeBytes = size
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 抽一帧做缩略图，返回 JPEG 字节。
    /// <para>
    /// <c>-ss</c> 放在 <c>-i</c> 前快速定位，配合 ffmpeg 默认开启的 accurate_seek，
    /// 会在关键帧后继续解码到目标时间，因此预览帧是准确的（代价是多解一个 GOP）。
    /// </para>
    /// </summary>
    public static async Task<byte[]?> ExtractFrameAsync(string ffmpegPath, string input, TimeSpan position,
        int width = 192, CancellationToken ct = default)
    {
        if (!File.Exists(input)) return null;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     "-ss", ToFfmpegTime(position),
                     "-i", input,
                     "-frames:v", "1",
                     "-vf", $"scale={width}:-2",
                     "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "6",
                     "pipe:1"
                 })
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();

            // stderr 必须同时读走，否则写满管道缓冲会卡死
            var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            using var reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            });

            using var buf = new MemoryStream(64 * 1024);
            try
            {
                await proc.StandardOutput.BaseStream.CopyToAsync(buf, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return null;
            }

            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }

            _ = errTask;
            return buf.Length > 0 ? buf.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>格式化成 ffmpeg 接受的 HH:MM:SS.mmm。</summary>
    public static string ToFfmpegTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    /// <summary>解析 "83.5"、"01:23"、"1:02:03" 这类输入。</summary>
    public static bool TryParseTime(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().Replace('：', ':');

        var parts = text.Split(':');
        try
        {
            if (parts.Length == 1)
            {
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) return false;
                if (sec < 0) return false;
                value = TimeSpan.FromSeconds(sec);
                return true;
            }
            if (parts.Length == 2)
            {
                if (!int.TryParse(parts[0], out var m)) return false;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return false;
                value = TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
                return value >= TimeSpan.Zero;
            }
            if (parts.Length == 3)
            {
                if (!int.TryParse(parts[0], out var h)) return false;
                if (!int.TryParse(parts[1], out var m)) return false;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return false;
                value = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
                return value >= TimeSpan.Zero;
            }
        }
        catch { /* 解析失败返回 false */ }
        return false;
    }
}
