using MediaDownloader.Core;

namespace MediaDownloader.App.ViewModels;

/// <summary>
/// 腾讯视频分集列表的一项。
/// 会员 / DRM 的集照样列出来，但勾选框禁用并写明原因——
/// 让用户看得见"为什么这一集不能下"，比直接藏起来清楚。
/// </summary>
public sealed class TencentEpisodeItem : ObservableObject
{
    private bool _isSelected;

    public TencentEpisodeItem(TencentEpisode episode, bool isSelected = false)
    {
        Episode = episode;
        _isSelected = isSelected && episode.CanDownload;
    }

    public TencentEpisode Episode { get; }

    public bool CanSelect => Episode.CanDownload;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            // 不可下载的集即使被程序置为 true 也要挡回去
            if (!Episode.CanDownload)
            {
                if (_isSelected) { _isSelected = false; OnPropertyChanged(); }
                return;
            }
            Set(ref _isSelected, value);
        }
    }

    public string NumberText => Episode.Number > 0 ? Episode.Number.ToString() : "—";

    public string Title => Episode.DisplayTitle;

    public string DurationText => Episode.DurationText;

    /// <summary>可下载时显示档位数量，否则显示拦截原因。</summary>
    public string StatusText => Episode.CanDownload
        ? $"{(Episode.Qualities.Count > 0 ? Episode.Qualities.Count + " 档" : "可下")}"
        : Episode.BlockReason;

    public bool IsBlocked => !Episode.CanDownload;
}
