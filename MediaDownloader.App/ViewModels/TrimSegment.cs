namespace MediaDownloader.App.ViewModels;

/// <summary>时间轴上的一段（由切点推导出来的区间，段数 = 切点数 + 1）。</summary>
public sealed class TrimSegment : ObservableObject
{
    public TrimSegment(TimeSpan start, TimeSpan end)
    {
        Start = start;
        End = end;
    }

    /// <summary>从 1 开始的段号，仅用于显示。</summary>
    public int Index { get; set; }

    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public TimeSpan Length => End - Start;

    /// <summary>不足一小时省略小时位，方便紧凑显示。</summary>
    public static string Fmt(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"mm\:ss\.f");

    public string Display => $"{Index}.　{Fmt(Start)} → {Fmt(End)}　（{Length.TotalSeconds:F1} 秒）";

    // 供列表分列显示用（段号 / 起点 / 终点 / 时长各自一列，比一整行字符串好对齐）
    public string IndexText => Index.ToString();
    public string StartText => Fmt(Start);
    public string EndText => Fmt(End);
    public string LengthText => $"{Length.TotalSeconds:F1} 秒";
}
