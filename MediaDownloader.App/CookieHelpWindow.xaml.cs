using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace MediaDownloader.App;

/// <summary>YouTube Cookie 获取的图文指导窗口。</summary>
public partial class CookieHelpWindow : Window
{
    public CookieHelpWindow()
    {
        InitializeComponent();
        Icon = UiIcons.Blank; // 标题栏不显示图标
    }

    private void OnOpenLink(object sender, RequestNavigateEventArgs e)
    {
        // 用系统默认浏览器打开，不内嵌 WebView2
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* 忽略：打不开浏览器就不开，链接文字还在 */ }
        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
