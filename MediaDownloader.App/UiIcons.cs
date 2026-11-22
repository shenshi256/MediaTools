using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaDownloader.App;

/// <summary>
/// 窗口标题栏图标统一处理。
/// - <see cref="App"/>：真正的应用图标，从 exe 自身的图标（ApplicationIcon 指定的 app.ico）提取，
///   用于主窗口，标题栏/任务栏/Alt-Tab 三处统一。
/// - <see cref="Blank"/>：1x1 全透明占位图标，用于弹窗等不需要图标的窗口。
/// WPF 没有 WinForms 那种 ShowIcon=false，直接 Icon=null 会回退成 exe 默认图标，
/// 所以不想显示图标时就塞 Blank；想显示就用 App。
/// </summary>
public static class UiIcons
{
    /// <summary>应用图标。Freeze 过，任何线程都能用。</summary>
    public static readonly ImageSource App = LoadAppIcon();

    /// <summary>透明占位图标。Freeze 过，任何线程都能用。</summary>
    public static readonly ImageSource Blank = CreateBlank();

    private static ImageSource LoadAppIcon()
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (icon is null) return Blank;
            var src = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            // 提取失败就退回透明占位，不让程序崩
            return Blank;
        }
    }

    private static ImageSource CreateBlank()
    {
        // 1x1 Bgra32，像素默认全 0，即完全透明
        var bmp = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
        bmp.Freeze();
        return bmp;
    }
}
