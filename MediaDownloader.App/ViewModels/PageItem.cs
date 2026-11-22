using MediaDownloader.Core;

namespace MediaDownloader.App.ViewModels;

/// <summary>分 P 列表项，带勾选状态，用于多选下载。</summary>
public sealed class PageItem : ObservableObject
{
    private bool _isSelected;

    public PageItem(VideoPage page, bool isSelected = false)
    {
        Page = page;
        _isSelected = isSelected;
    }

    public VideoPage Page { get; }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public string Display => $"P{Page.Page:D2} {Page.Part}";

    public string DurationText
    {
        get
        {
            if (Page.Duration <= 0) return "";
            var t = TimeSpan.FromSeconds(Page.Duration);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        }
    }
}
