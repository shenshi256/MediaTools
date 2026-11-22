namespace MediaDownloader.Core;

public sealed class DouyinException : Exception
{
    public DouyinException(string message) : base(message) { }
}

/// <summary>一条抖音视频解析出来的信息。</summary>
public sealed class DouyinVideo
{
    public string AwemeId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public string MusicTitle { get; init; } = "";
    public TimeSpan Duration { get; init; }

    /// <summary>视频原始宽高（抖音多为竖屏，宽 &lt; 高）。</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>图文作品（images 非空）。目前只支持视频，图集会给出明确提示。</summary>
    public bool IsImagePost { get; init; }

    /// <summary>可选码率档位，已按清晰度从高到低排好。</summary>
    public IReadOnlyList<DouyinStream> Streams { get; init; } = Array.Empty<DouyinStream>();

    public string InfoText
    {
        get
        {
            var parts = new List<string> { $"时长 {Duration:mm\\:ss\\.f}" };
            if (Width > 0 && Height > 0) parts.Add($"{Width}x{Height}");
            if (!string.IsNullOrWhiteSpace(Author)) parts.Add("@" + Author);
            if (!string.IsNullOrWhiteSpace(MusicTitle)) parts.Add("♪ " + MusicTitle);
            parts.Add($"共 {Streams.Count} 档");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>抖音的一个码率档位（对应接口 video.bit_rate 里的一项）。</summary>
public sealed class DouyinStream
{
    public string GearName { get; init; } = "";
    public int QualityType { get; init; }
    public long BitRate { get; init; }
    public string Url { get; init; } = "";
    public string[] BackupUrls { get; init; } = Array.Empty<string>();
    public int Width { get; init; }
    public int Height { get; init; }
    public long DataSize { get; init; }

    public string Label => GearLabel(GearName) ?? ShortSideLabel();

    /// <summary>
    /// 抖音的档位名（normal_1080_0 / low_720_0 / 540_1_1…）里带分辨率，直接认它最准。
    /// 认不出来再看实际像素。
    /// </summary>
    private static string? GearLabel(string gear)
    {
        if (string.IsNullOrEmpty(gear)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(gear, @"(?:^|_)(1080|720|540|480|360)(?:_|$)");
        return m.Success ? m.Groups[1].Value + "P" : null;
    }

    /// <summary>竖屏视频的高度是长边，所以"1080P"要取短边，不能取 Height。</summary>
    public int ShortSide => Width > 0 && Height > 0 ? Math.Min(Width, Height) : Math.Max(Width, Height);

    private string ShortSideLabel() => ShortSide > 0 ? $"{ShortSide}P" : "默认";

    public string DisplayName
    {
        get
        {
            var s = Label;
            if (BitRate > 0) s += $" · {BitRate / 1000.0:F0} kbps";
            if (DataSize > 0) s += " · " + SizeText(DataSize);
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
