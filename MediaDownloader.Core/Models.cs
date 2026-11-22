namespace MediaDownloader.Core;

public sealed class VideoMeta
{
    public string Bvid { get; set; } = "";
    public long Aid { get; set; }
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    public string UpName { get; set; } = "";
    public long Duration { get; set; }
    public List<VideoPage> Pages { get; set; } = new();
}

public sealed class VideoPage
{
    public long Cid { get; set; }
    public int Page { get; set; }
    public string Part { get; set; } = "";
    public long Duration { get; set; }
}

/// <summary>一个可选的清晰度档位。</summary>
public sealed class QualityOption
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool NeedVip { get; set; }

    /// <summary>DASH 视频流地址；单流（老格式 durl）时为空。</summary>
    public string VideoUrl { get; set; } = "";
    public string[] VideoBackupUrls { get; set; } = Array.Empty<string>();
    /// <summary>DASH 音频流地址；单流时为 null。</summary>
    public string? AudioUrl { get; set; }
    public string[] AudioBackupUrls { get; set; } = Array.Empty<string>();
    /// <summary>单流地址（flv/mp4 直链），与 DASH 二选一。</summary>
    public string? SingleUrl { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
    public string Codecs { get; set; } = "";

    public bool IsDash => AudioUrl is not null;

    public string DisplayName
    {
        get
        {
            var res = Width > 0 && Height > 0 ? $" · {Width}x{Height}" : "";
            var vip = NeedVip ? " · 大会员" : "";
            return $"{Name}（{Id}）{res}{vip}";
        }
    }
}

public enum DownloadState
{
    Pending,
    Running,
    Paused,
    Merging,
    Completed,
    Failed,
    Canceled
}

public sealed class DownloadProgress
{
    public long ReceivedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public DownloadState State { get; init; }
    public string? Message { get; init; }

    public double Percent => TotalBytes > 0 ? ReceivedBytes * 100.0 / TotalBytes : 0;

    public string SpeedText => BytesPerSecond switch
    {
        >= 1024 * 1024 => $"{BytesPerSecond / 1024 / 1024:F1} MB/s",
        >= 1024 => $"{BytesPerSecond / 1024:F0} KB/s",
        > 0 => $"{BytesPerSecond:F0} B/s",
        _ => "-"
    };
}
