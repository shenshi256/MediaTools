using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MediaDownloader.App;

/// <summary>
/// 通用轻提示（Toast）：无边框、圆角、深色半透明底、白字，出现后自动淡出消失。
/// 不阻塞、不抢焦点。用法：Toast.Show("您当前还未登录");
/// </summary>
public static class Toast
{
    /// <summary>默认显示时长（毫秒）。</summary>
    private const int DefaultDurationMs = 2200;

    private const double FadeMs = 260;

    public static void Show(string message, int durationMs = DefaultDurationMs)
    {
        // 必须在 UI 线程上创建窗口
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess() == false)
        {
            dispatcher?.Invoke(() => ShowCore(message, durationMs));
            return;
        }
        ShowCore(message, durationMs);
    }

    private static void ShowCore(string message, int durationMs)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Focusable = false,
            ShowActivated = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            IsHitTestVisible = false,           // 不挡鼠标
            WindowStartupLocation = WindowStartupLocation.Manual
        };

        var text = new TextBlock
        {
            Text = message,
            Foreground = Brushes.White,
            FontSize = 13,
            Padding = new Thickness(18, 10, 18, 10),
            MaxWidth = 480,
            TextWrapping = TextWrapping.Wrap
        };

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x1F, 0x1F)), // 近黑，90% 不透明
            CornerRadius = new CornerRadius(6),
            Child = text
        };

        window.Content = border;

        // 定位到主窗口底部居中偏上一点，避免贴边
        var owner = Application.Current?.MainWindow;
        if (owner is not null)
        {
            window.Left = owner.Left + (owner.ActualWidth - 0) / 2;   // 占位，下面统一重算
            window.Top = owner.Top + owner.ActualHeight - 120;
        }
        else
        {
            // 没有主窗口就放屏幕中下
            var wa = SystemParameters.WorkArea;
            window.Left = wa.Left + wa.Width / 2;
            window.Top = wa.Top + wa.Height - 120;
        }

        window.Loaded += (_, _) =>
        {
            // 加载后才能拿到实际宽度，此时才做水平居中
            if (owner is not null)
                window.Left = owner.Left + (owner.ActualWidth - window.ActualWidth) / 2;
        };

        window.Show();

        // 淡出
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await FadeOutAsync(window);
        };
        timer.Start();
    }

    private static async Task FadeOutAsync(Window window)
    {
        var total = FadeMs;
        var step = 40;
        var steps = (int)(total / step);
        var perStep = 1.0 / steps;

        for (var i = 1; i <= steps; i++)
        {
            window.Opacity = 1.0 - perStep * i;
            await Task.Delay(step);
        }
        window.Close();
    }
}
