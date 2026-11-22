namespace MediaDownloader.Core;

public sealed class ToutiaoException : Exception
{
    public ToutiaoException(string message) : base(message) { }
}

/// <summary>
/// 一条今日头条视频解析出来的信息。
/// 做成 record 是为了让"info 拿到元数据、PlayInfo 拿到清晰度"这两次请求的结果
/// 能用 with 合成一个对象，不用把属性改成可变。
/// </summary>
public sealed record ToutiaoVideo
{
    /// <summary>条目 ID，也就是 URL 里那串数字（接口里的 gid）。</summary>
    public string GroupId { get; init; } = "";

    /// <summary>视频资源 ID（v0d910g10002d2m26ufog65p5d10lpv0 这种），交给播放地址接口用。</summary>
    public string VideoId { get; init; } = "";

    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public DateTimeOffset? PublishTime { get; init; }

    /// <summary>
    /// GetPlayInfo 的预签名串（接口字段 play_auth_token_v2）。
    /// 内容里偶尔只有 content 内嵌的 data-token，解析时两者都认。
    /// </summary>
    public string PlayToken { get; init; } = "";

    /// <summary>可选清晰度档位，已按清晰度从高到低排好。</summary>
    public IReadOnlyList<ToutiaoStream> Streams { get; init; } = Array.Empty<ToutiaoStream>();

    public string InfoText
    {
        get
        {
            var parts = new List<string> { $"时长 {Duration:mm\\:ss}" };
            if (!string.IsNullOrWhiteSpace(Author)) parts.Add("@" + Author);
            if (PublishTime is { } t) parts.Add(t.LocalDateTime.ToString("yyyy-MM-dd"));
            parts.Add($"共 {Streams.Count} 档");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>头条的一个清晰度档位（对应 GetPlayInfo 的 PlayInfoList 里的一项）。</summary>
public sealed class ToutiaoStream
{
    /// <summary>接口给的档位名：360p / 480p / 720p。</summary>
    public string Definition { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public long BitRate { get; init; }
    public long DataSize { get; init; }
    public string Codec { get; init; } = "";
    public string Url { get; init; } = "";
    public string[] BackupUrls { get; init; } = Array.Empty<string>();

    public string Label
    {
        get
        {
            if (!string.IsNullOrEmpty(Definition))
                return Definition.TrimEnd('p', 'P') + "P";
            return ShortSide > 0 ? $"{ShortSide}P" : "默认";
        }
    }

    /// <summary>竖屏视频的高度是长边，所以"720P"要取短边，不能取 Height。</summary>
    public int ShortSide => Width > 0 && Height > 0 ? Math.Min(Width, Height) : Math.Max(Width, Height);

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
