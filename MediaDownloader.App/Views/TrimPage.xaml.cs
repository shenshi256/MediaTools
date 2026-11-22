using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MediaDownloader.App.Services;
using MediaDownloader.App.ViewModels;

namespace MediaDownloader.App.Views;

public partial class TrimPage : UserControl
{
    // 与气泡内容尺寸保持一致（图 180 宽 + 内边距 + 时间文字）
    private const double PopupWidth = 190;
    private const double PopupHeight = 132;

    private readonly DispatcherTimer _hoverTimer;
    private ThumbnailProvider? _thumbs;
    private string _thumbsFfmpeg = "";
    private TimeSpan? _hoverTime;

    public TrimPage()
    {
        InitializeComponent();

        // 鼠标停稳 80ms 再去抽帧，避免快速滑动时反复起 ffmpeg
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _hoverTimer.Tick += async (_, _) =>
        {
            _hoverTimer.Stop();
            await UpdateThumbnailAsync();
        };

        Timeline.CutInsertRequested += (_, t) => Vm?.InsertCut(t);
        Timeline.CutRemoveRequested += (_, t) => Vm?.RemoveCutNear(t);
        Timeline.UndoRequested += (_, _) => Vm?.UndoLastCut();
        Timeline.HoverChanged += (_, t) => OnHoverChanged(t);

        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) =>
        {
            _hoverTimer.Stop();
            ThumbPopup.IsOpen = false;
        };
    }

    private VideoToolsViewModel? Vm => DataContext as VideoToolsViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is VideoToolsViewModel old) old.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is VideoToolsViewModel vm) vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 换源文件时缩略图缓存必须作废
        if (e.PropertyName == nameof(VideoToolsViewModel.TrimInput)) _thumbs?.Reset();
    }

    private ThumbnailProvider Thumbs(string ffmpeg)
    {
        if (_thumbs is not null && _thumbsFfmpeg == ffmpeg) return _thumbs;
        _thumbs = new ThumbnailProvider(ffmpeg);
        _thumbsFfmpeg = ffmpeg;
        return _thumbs;
    }

    private void OnHoverChanged(TimeSpan? t)
    {
        _hoverTime = t;

        if (t is null || Vm is null || Vm.TrimSourceDuration <= TimeSpan.Zero)
        {
            ThumbPopup.IsOpen = false;
            _hoverTimer.Stop();
            return;
        }

        PositionPopup(t.Value);

        var cached = Thumbs(Vm.FfmpegPath).TryGetCached(Vm.TrimInput, t.Value);
        if (cached is not null)
        {
            ThumbImage.Source = cached;
            ThumbPopup.IsOpen = true;
            _hoverTimer.Stop();
            return;
        }

        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void PositionPopup(TimeSpan t)
    {
        var x = Timeline.TimeToX(t);
        var width = Timeline.ActualWidth;
        var half = PopupWidth / 2;

        // 靠边时把气泡拉回可视范围，避免超出窗口
        var center = Math.Clamp(x, half, Math.Max(half, width - half));

        ThumbPopup.HorizontalOffset = center - half;
        ThumbPopup.VerticalOffset = -PopupHeight;
        ThumbTime.Text = TrimSegment.Fmt(t);
    }

    private async Task UpdateThumbnailAsync()
    {
        if (_hoverTime is null || Vm is null || !File.Exists(Vm.TrimInput)) return;

        var requested = _hoverTime.Value;
        var img = await Thumbs(Vm.FfmpegPath).GetAsync(Vm.TrimInput, requested);

        // 等待期间鼠标已经移到别处（或离开），这次结果作废
        if (img is null || _hoverTime != requested) return;

        ThumbImage.Source = img;
        ThumbPopup.IsOpen = true;
    }
}
