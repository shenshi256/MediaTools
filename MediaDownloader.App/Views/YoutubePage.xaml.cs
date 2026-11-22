using System.Windows.Controls;

namespace MediaDownloader.App.Views;

/// <summary>YouTube 下载页。下载走 yt-dlp，无内嵌浏览器、无登录态要维护。</summary>
public partial class YoutubePage : UserControl
{
    public YoutubePage()
    {
        InitializeComponent();
    }
}
