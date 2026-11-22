using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MediaDownloader.App.ViewModels;

namespace MediaDownloader.App.Controls;

/// <summary>
/// 时间轴条：整条分成若干段（段数 = 切点数 + 1）。
/// 左键点空白插入切点、点已有切点删除它、右键撤销最后一个切点；
/// 选中的一段会加重描边，方便和右侧列表对应。
/// </summary>
public sealed class TimelineStrip : FrameworkElement
{
    /// <summary>切点命中半径（DIP）。</summary>
    private const double CutHitRadius = 6;

    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF3)));
    private static readonly Pen TrackPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xD8, 0xDB, 0xE0)), 1));

    private static readonly Brush SegmentFill = Freeze(new SolidColorBrush(Color.FromArgb(0x1C, 0x2F, 0x6B, 0xED)));
    private static readonly Pen SegmentPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x9D, 0xBC, 0xF7)), 1));

    // 选中段：更实的填充 + 更重的描边
    private static readonly Brush SelectedFill = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0x2F, 0x6B, 0xED)));
    private static readonly Pen SelectedPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)), 2));

    private static readonly Brush CutBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush CutHotBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)));
    private static readonly Pen HoverPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x88, 0x8C, 0x92)), 1));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // ---------------- 依赖属性 ----------------

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(TimeSpan), typeof(TimelineStrip),
        new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable), typeof(TimelineStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSegmentsChanged));

    public static readonly DependencyProperty SelectedSegmentProperty = DependencyProperty.Register(
        nameof(SelectedSegment), typeof(TrimSegment), typeof(TimelineStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverTimeProperty = DependencyProperty.Register(
        nameof(HoverTime), typeof(TimeSpan?), typeof(TimelineStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public TimeSpan Duration
    {
        get => (TimeSpan)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public IEnumerable? Segments
    {
        get => (IEnumerable?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public TrimSegment? SelectedSegment
    {
        get => (TrimSegment?)GetValue(SelectedSegmentProperty);
        set => SetValue(SelectedSegmentProperty, value);
    }

    public TimeSpan? HoverTime
    {
        get => (TimeSpan?)GetValue(HoverTimeProperty);
        set => SetValue(HoverTimeProperty, value);
    }

    // ---------------- 事件 ----------------

    /// <summary>左键点在空白处：请求在该时间插入切点。</summary>
    public event EventHandler<TimeSpan>? CutInsertRequested;

    /// <summary>左键点在已有切点上：请求删除该切点。</summary>
    public event EventHandler<TimeSpan>? CutRemoveRequested;

    /// <summary>右键：撤销最后一个切点。</summary>
    public event EventHandler? UndoRequested;

    /// <summary>鼠标移动（null 表示离开）。</summary>
    public event EventHandler<TimeSpan?>? HoverChanged;

    public TimelineStrip()
    {
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
    }

    // ---------------- 坐标换算 ----------------

    public double TimeToX(TimeSpan t)
    {
        var dur = Duration.TotalSeconds;
        if (dur <= 0 || ActualWidth <= 0) return 0;
        return Math.Clamp(t.TotalSeconds / dur, 0, 1) * ActualWidth;
    }

    public TimeSpan XToTime(double x)
    {
        var dur = Duration.TotalSeconds;
        if (dur <= 0 || ActualWidth <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Clamp(x / ActualWidth, 0, 1) * dur);
    }

    /// <summary>内部边界（即用户插入的切点）。首尾不算切点。</summary>
    private List<TimeSpan> GetCutTimes()
    {
        var cuts = new List<TimeSpan>();
        if (Segments is null) return cuts;

        var list = Segments.OfType<TrimSegment>().ToList();
        for (var i = 1; i < list.Count; i++) cuts.Add(list[i].Start);
        return cuts;
    }

    private TimeSpan? HitCut(double x)
    {
        TimeSpan? best = null;
        var bestDiff = CutHitRadius;

        foreach (var cut in GetCutTimes())
        {
            var diff = Math.Abs(TimeToX(cut) - x);
            if (diff <= bestDiff)
            {
                bestDiff = diff;
                best = cut;
            }
        }

        return best;
    }

    // ---------------- 绘制 ----------------

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 40 : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 透明矩形让整块区域可命中，否则 FrameworkElement 收不到鼠标事件
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var track = new Rect(0, 6, w, Math.Max(8, h - 12));
        dc.DrawRoundedRectangle(TrackBrush, TrackPen, track, 3, 3);

        if (Duration <= TimeSpan.Zero) return;

        var innerY = track.Y + 1.5;
        var innerH = Math.Max(2, track.Height - 3);

        // 段
        if (Segments is not null)
        {
            foreach (var seg in Segments.OfType<TrimSegment>())
            {
                var x1 = TimeToX(seg.Start);
                var x2 = TimeToX(seg.End);
                if (x2 - x1 < 1.5) continue;

                var selected = ReferenceEquals(seg, SelectedSegment);
                dc.DrawRectangle(selected ? SelectedFill : SegmentFill,
                    selected ? SelectedPen : SegmentPen,
                    new Rect(x1, innerY, x2 - x1, innerH));
            }
        }

        // 悬停游标
        var hoverCut = HoverTime is null ? null : HitCut(TimeToX(HoverTime.Value));

        if (HoverTime is { } ht)
        {
            var x = TimeToX(ht);
            dc.DrawLine(HoverPen, new Point(x, track.Y), new Point(x, track.Bottom));
        }

        // 切点标记
        foreach (var cut in GetCutTimes())
        {
            var x = TimeToX(cut);
            var hot = hoverCut.HasValue && Math.Abs(TimeToX(hoverCut.Value) - x) < 0.5;
            var brush = hot ? CutHotBrush : CutBrush;
            var width = hot ? 3.5 : 2.5;

            dc.DrawRectangle(brush, null, new Rect(x - width / 2, track.Y - 3, width, track.Height + 6));
            dc.DrawEllipse(brush, null, new Point(x, track.Y - 5), hot ? 4.5 : 3.5, hot ? 4.5 : 3.5);
        }
    }

    // ---------------- 鼠标 ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Duration <= TimeSpan.Zero) return;

        var pos = e.GetPosition(this);
        var cut = HitCut(pos.X);
        Cursor = cut.HasValue ? Cursors.Hand : Cursors.Cross;

        HoverTime = cut ?? XToTime(pos.X);
        HoverChanged?.Invoke(this, HoverTime);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoverTime = null;
        Cursor = Cursors.Cross;
        HoverChanged?.Invoke(this, null);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Duration <= TimeSpan.Zero) return;

        var pos = e.GetPosition(this);
        var cut = HitCut(pos.X);

        if (cut.HasValue) CutRemoveRequested?.Invoke(this, cut.Value);
        else CutInsertRequested?.Invoke(this, XToTime(pos.X));

        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        UndoRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    // ---------------- 集合变化 ----------------

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctl = (TimelineStrip)d;
        if (e.OldValue is INotifyCollectionChanged oldC)
            oldC.CollectionChanged -= ctl.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newC)
            newC.CollectionChanged += ctl.OnCollectionChanged;
        ctl.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
}
