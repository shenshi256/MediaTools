using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MediaDownloader.App.Converters;

/// <summary>
/// 把 <c>MainViewModel.CurrentPageKey</c> 和目标页面的 key 做比较：
/// 命中返回 Visible，否则 Collapsed。所有页面控件常驻内存，
/// 只切可见性——这样在视频截取页打好的切点，切走再切回来不会丢。
/// </summary>
public sealed class PageKeyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value as string;
        var want = parameter as string;
        return string.Equals(current, want, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
