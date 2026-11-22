namespace MediaDownloader.Core;

public sealed class TencentException : Exception
{
    public TencentException(string message) : base(message) { }
}

/// <summary>
/// 腾讯视频一个视频页（cover）解析出来的东西：元数据 + 分集列表。
/// 短剧通常几十集，电影 / 单条视频只有一集。
/// </summary>
public sealed record TencentVideo
{
    /// <summary>专辑 ID，URL 里的 mzc00327xgk7sai 这一段。</summary>
    public string Cid { get; init; } = "";

    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string CoverUrl { get; init; } = "";

    /// <summary>已按集数排好序。</summary>
    public IReadOnlyList<TencentEpisode> Episodes { get; init; } = Array.Empty<TencentEpisode>();

    /// <summary>可下载（免费且非 DRM）的集数。</summary>
    public int FreeCount => Episodes.Count(e => e.CanDownload);

    /// <summary>因会员或 DRM 被挡下的集数。</summary>
    public int BlockedCount => Episodes.Count(e => !e.CanDownload);

    public string InfoText
    {
        get
        {
            var parts = new List<string> { $"共 {Episodes.Count} 集" };
            if (FreeCount > 0) parts.Add($"免费 {FreeCount}");
            if (BlockedCount > 0) parts.Add($"需会员/不支持 {BlockedCount}");
            var total = Episodes.Where(e => e.CanDownload).Aggregate(TimeSpan.Zero, (a, e) => a + e.Duration);
            if (total > TimeSpan.Zero) parts.Add($"可下时长 {total:hh\\:mm\\:ss}");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>一集。</summary>
public sealed class TencentEpisode
{
    public string Vid { get; init; } = "";

    /// <summary>接口给的标题，通常是「<c>剧名 第12集</c>」这种。</summary>
    public string Title { get; init; } = "";

    /// <summary>从标题里抠出来的集数，排序用；抠不到时按接口顺序。</summary>
    public int Number { get; set; }

    public TimeSpan Duration { get; init; }

    /// <summary>true 表示这一集免费可下。</summary>
    public bool CanDownload { get; init; }

    /// <summary>不能下载时的原因，直接显示给用户。</summary>
    public string BlockReason { get; init; } = "";

    /// <summary>
    /// 当前账号实际能拿到的最高分辨率。未登录时腾讯只给到 720P，
    /// 请求 1080P 会被悄悄降级，所以解析阶段就把真实上限记下来，UI 只列能拿到的档位。
    /// </summary>
    public int MaxAvailableHeight { get; init; }

    /// <summary>可选清晰度，按分辨率从高到低。</summary>
    public IReadOnlyList<TencentQuality> Qualities { get; init; } = Array.Empty<TencentQuality>();

    public string DurationText => Duration > TimeSpan.Zero
        ? $"{(int)Duration.TotalMinutes}:{Duration.Seconds:00}"
        : "-";

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Vid : Title;
}

/// <summary>一个清晰度档位。腾讯这边是完整的 mp4 直链，不是 m3u8 分片。</summary>
public sealed class TencentQuality
{
    /// <summary>接口给的档位名：hd / shd / fhd。</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 请求 getinfo 时要带的 defn 值，取自 fl.fi[].formatdefn。
    /// 每档的文件名（fn）不一样，必须按档位单独请求，不能一份响应套所有档。
    /// </summary>
    public string Defn { get; init; } = "";

    /// <summary>例如「标清;(480P)」里抠出来的 480。</summary>
    public int Height { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>
    /// 已拼好 vkey 的完整地址。解析专辑时为空——要到真正下载时
    /// 按 <see cref="Defn"/> 单独请求才会填上（见 TencentClient.FetchStreamAsync）。
    /// </summary>
    public string Url { get; init; } = "";

    public string[] BackupUrls { get; init; } = Array.Empty<string>();

    /// <summary>该档位是否带 DRM 标记，带的话不提供下载。</summary>
    public bool IsDrm { get; init; }

    public string Label => Height > 0 ? $"{Height}P" : Name.ToUpperInvariant();

    public string DisplayName
    {
        get
        {
            var s = Label;
            if (SizeBytes > 0) s += " · " + SizeText(SizeBytes);
            return s;
        }
    }

    public TencentQuality WithUrl(string url, string[] backups)
        => new()
        {
            Name = Name,
            Defn = Defn,
            Height = Height,
            SizeBytes = SizeBytes,
            Url = url,
            BackupUrls = backups,
            IsDrm = IsDrm
        };

    private static string SizeText(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B"
    };
}
