using System.Globalization;
using System.Text;

namespace MediaDownloader.Core;

public enum CompressMode { Crf, TargetSize, Custom }
public enum CompressEncoder { H265, H264 }
public enum CompressAudio { Copy, Aac96, Aac128 }

/// <summary>
/// 一条压缩任务的用户侧参数（对应设置页那三张画质卡 / 模式切换）。
/// 可序列化进 settings 记住上次选择。
/// </summary>
public sealed class CompressOptions
{
    public CompressMode Mode { get; set; } = CompressMode.Crf;
    public CompressEncoder Encoder { get; set; } = CompressEncoder.H265;

    /// <summary>CRF 档位值，按 x265 口径存：22 = 视觉无损，24 = 推荐，26 = 较小。</summary>
    public double Crf { get; set; } = 24;

    public string Preset { get; set; } = "medium";
    public CompressAudio Audio { get; set; } = CompressAudio.Aac96;
    public long TargetSizeMB { get; set; } = 50;

    /// <summary>自定义模式下原样追加的 ffmpeg 参数（支持带引号的值）。</summary>
    public string CustomArgs { get; set; } = "";

    /// <summary>v1 固定 true：不做缩放。降分辨率是唯一肉眼必查的降级，先不提供。</summary>
    public bool KeepResolution { get; set; } = true;
}

/// <summary>
/// 视频压缩的 ffmpeg 命令构造。全部是纯函数（吃参数、吐参数列表），
/// 方便在 MediaCli 里做离线断言。
///
/// 路线说明：走 ffmpeg 单命令（libx265 / libx264），不走小丸那套
/// "拆流 → x264 10bit → neroAac → 封装" —— libx265 内部本身就是 10bit 编码，
/// 还带硬件解码支持，同样的收益没有它的兼容性代价。
/// </summary>
public static class CompressService
{
    /// <summary>x265 与 x264 的 CRF 不在同一刻度，同档观感 x264 要低约 3。</summary>
    public static double CrfFor(CompressEncoder encoder, double x265Crf)
        => encoder == CompressEncoder.H265 ? x265Crf : Math.Max(14, x265Crf - 3);

    private static string VideoCodec(CompressEncoder e)
        => e == CompressEncoder.H265 ? "libx265" : "libx264";

    private static IEnumerable<string> AudioArgs(CompressAudio a)
    {
        return a switch
        {
            CompressAudio.Copy => new[] { "-c:a", "copy" },
            CompressAudio.Aac96 => new[] { "-c:a", "aac", "-b:a", "96k" },
            CompressAudio.Aac128 => new[] { "-c:a", "aac", "-b:a", "128k" },
            _ => new[] { "-c:a", "copy" }
        };
    }

    /// <summary>输出文件名规则：与源同目录、同名加 _c，保持 .mp4 容器。</summary>
    public static string BuildOutputPath(string inputPath, string? fallbackDir = null)
    {
        var dir = Path.GetDirectoryName(inputPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            dir = string.IsNullOrEmpty(fallbackDir) ? AppPaths.DefaultDownloadDir : fallbackDir;
        var name = Path.GetFileNameWithoutExtension(inputPath) + "_c.mp4";
        return Path.Combine(dir, name);
    }

    /// <summary>
    /// CRF 模式（单遍）。这也是试压片段用的同一套参数，保证"试出来的就是全片的"。
    /// </summary>
    public static string[] BuildCrfArgs(CompressOptions o, string input, string output)
    {
        var list = new List<string> { "-y", "-i", input };
        AppendVideoArgs(list, o, CrfFor(o.Encoder, o.Crf).ToString("0.#", CultureInfo.InvariantCulture));
        list.AddRange(AudioArgs(o.Audio));
        list.AddRange(new[] { "-movflags", "+faststart", output });
        return list.ToArray();
    }

    /// <summary>试压片段：取片长约 1/4 处的 30 秒（那一段通常已经包含运动镜头）。</summary>
    public static string[] BuildTestArgs(CompressOptions o, string input, string output, double durationSec)
    {
        var start = TimeSpan.FromSeconds(Math.Max(0, durationSec * 0.25));
        var list = new List<string>
        {
            "-y", "-ss", FfmpegRunner.ToFfmpegTime(start), "-i", input, "-t", "30"
        };
        AppendVideoArgs(list, o, CrfFor(o.Encoder, o.Crf).ToString("0.#", CultureInfo.InvariantCulture));
        list.AddRange(AudioArgs(o.Audio));
        list.AddRange(new[] { "-movflags", "+faststart", output });
        return list.ToArray();
    }

    private static void AppendVideoArgs(List<string> list, CompressOptions o, string crf)
    {
        list.AddRange(new[]
        {
            "-c:v", VideoCodec(o.Encoder),
            "-crf", crf,
            "-preset", o.Preset
        });
    }

    /// <summary>
    /// 目标体积模式：按片长反推视频码率，跑两遍逼近目标。
    /// 音频固定重压 AAC 96k（copy 的源音轨码率不可控，没法预算）。
    /// </summary>
    public static (string[] Pass1, string[] Pass2, long VideoBitrate, string StatsFile) BuildTwoPassArgs(
        CompressOptions o, string input, string output, double durationSec, string statsFile)
    {
        var bitrate = EstimateVideoBitrate(o.TargetSizeMB * 1024L * 1024L, durationSec);

        var codec = VideoCodec(o.Encoder);

        var p1 = new List<string>
        {
            "-y", "-i", input,
            "-c:v", codec, "-b:v", bitrate.ToString(CultureInfo.InvariantCulture),
            "-preset", o.Preset, "-an", "-f", "mp4"
        };
        if (o.Encoder == CompressEncoder.H265)
        {
            p1.Add("-x265-params");
            p1.Add($"pass=1:stats={statsFile}");
        }
        else
        {
            p1.AddRange(new[] { "-pass", "1", "-passlogfile", statsFile });
        }
        p1.Add("NUL");

        var p2 = new List<string>
        {
            "-y", "-i", input,
            "-c:v", codec, "-b:v", bitrate.ToString(CultureInfo.InvariantCulture),
            "-preset", o.Preset
        };
        if (o.Encoder == CompressEncoder.H265)
        {
            p2.Add("-x265-params");
            p2.Add($"pass=2:stats={statsFile}");
        }
        else
        {
            p2.AddRange(new[] { "-pass", "2", "-passlogfile", statsFile });
        }
        p2.AddRange(AudioArgs(CompressAudio.Aac96));
        p2.AddRange(new[] { "-movflags", "+faststart", output });

        return (p1.ToArray(), p2.ToArray(), bitrate, statsFile);
    }

    /// <summary>
    /// 目标字节 → 视频码率。扣掉音频（96k）和容器开销（约 1.5%），再留一点安全余量，
    /// 宁可略小于目标也别超。
    /// </summary>
    public static long EstimateVideoBitrate(long targetBytes, double durationSec)
    {
        if (durationSec <= 0) durationSec = 1;
        var totalBits = targetBytes * 8.0 * 0.985;          // 容器/索引开销
        var audioBits = 96_000.0 * durationSec;             // 音频预算
        var videoBits = Math.Max(50_000, (totalBits - audioBits) / durationSec);
        return (long)(videoBits / 1000) * 1000;             // 取整到 kbps
    }

    /// <summary>把用户手写的参数串按空格切开，双引号里的空格不切。</summary>
    public static string[] SplitCustomArgs(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"':
                    inQuote = !inQuote;
                    break;
                case ' ' when !inQuote:
                    if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result.ToArray();
    }

    /// <summary>
    /// CRF 模式下的粗略体积预估，只用于界面提示（真实结果由编码器决定）。
    /// 网络来源（B站/抖音/头条）的 H.264 二压经验系数。
    /// </summary>
    public static double EstimateRatio(CompressOptions o) => o.Mode switch
    {
        CompressMode.Crf => (o.Encoder, o.Crf) switch
        {
            (CompressEncoder.H265, <= 22) => 0.50,
            (CompressEncoder.H265, <= 24) => 0.37,
            (CompressEncoder.H265, <= 26) => 0.28,
            (CompressEncoder.H264, <= 22) => 0.80,
            (CompressEncoder.H264, <= 24) => 0.68,
            _ => 0.55
        },
        _ => 1.0
    };
}
