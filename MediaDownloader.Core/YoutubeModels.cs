namespace MediaDownloader.Core;

public sealed class YoutubeException : Exception
{
    public YoutubeException(string message) : base(message) { }
}

/// <summary>一个 YouTube 视频解析出来的信息。</summary>
public sealed record YoutubeVideo
{
    /// <summary>视频 ID，URL 里 ?v= 后面的那串。</summary>
    public string VideoId { get; init; } = "";

    public string Title { get; init; } = "";
    public string Channel { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public TimeSpan Duration { get; init; }

    /// <summary>可选格式，已按「分辨率 + 是否有音轨」排好序。</summary>
    public IReadOnlyList<YoutubeFormat> Formats { get; init; } = Array.Empty<YoutubeFormat>();

    public string InfoText
    {
        get
        {
            var parts = new List<string> { $"时长 {Duration:mm\\:ss}" };
            if (!string.IsNullOrWhiteSpace(Channel)) parts.Add(Channel);
            parts.Add($"{Formats.Count} 种格式");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>一个可下载的格式。</summary>
public sealed class YoutubeFormat
{
    /// <summary>yt-dlp 的 format_id，下载时用 -f 传给 yt-dlp。</summary>
    public string FormatId { get; init; } = "";

    /// <summary>扩展名：mp4 / webm / m4a。</summary>
    public string Ext { get; init; } = "";

    /// <summary>视频高（纯音频流为 0）。</summary>
    public int Height { get; init; }
    public int Width { get; init; }

    /// <summary>视频编码：avc1 / vp9 / av01。</summary>
    public string VCodec { get; init; } = "";

    /// <summary>音频编码；纯音频流才有意义。</summary>
    public string ACodec { get; init; } = "";

    /// <summary>音频码率（纯音频流）。</summary>
    public double AudioBitrate { get; init; }

    /// <summary>音频采样率。</summary>
    public int SampleRate { get; init; }

    /// <summary>文件大小，yt-dlp 没给时是 0（按需下载时很多是 0）。</summary>
    public long FileSize { get; init; }

    /// <summary>视频流（无音轨）——DASH 下常见。</summary>
    public bool IsVideoOnly => Height > 0 && string.IsNullOrEmpty(ACodec);

    /// <summary>纯音频流。</summary>
    public bool IsAudioOnly => Height == 0 && !string.IsNullOrEmpty(ACodec);

    /// <summary>音视频合一的流（老格式，或有音轨的单文件）。</summary>
    public bool IsCombined => Height > 0 && !string.IsNullOrEmpty(ACodec);

    public string Label
    {
        get
        {
            if (IsAudioOnly) return $"{AudioBitrate:F0}kbps 音频";
            if (Height > 0) return $"{Height}P";
            return "未知";
        }
    }

    public string DisplayName
    {
        get
        {
            var s = Label;
            var codec = !string.IsNullOrEmpty(VCodec) ? VCodec : ACodec;
            if (!string.IsNullOrEmpty(codec)) s += $" · {codec}";
            if (IsVideoOnly) s += "（仅视频）";
            if (IsAudioOnly && SampleRate > 0) s += $" · {SampleRate / 1000.0:F1}kHz";
            if (FileSize > 0) s += " · " + SizeText(FileSize);
            return s;
        }
    }

    private static string SizeText(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B"
    };
}
