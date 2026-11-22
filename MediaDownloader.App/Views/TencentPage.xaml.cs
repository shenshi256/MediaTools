using System.Windows.Controls;

namespace MediaDownloader.App.Views;

/// <summary>
/// 腾讯视频下载页。纯 HTTP 解析，没有登录态要维护，
/// 所以这里不需要像抖音 / 头条那样在切换可见性时做任何初始化。
/// </summary>
public partial class TencentPage : UserControl
{
    public TencentPage()
    {
        InitializeComponent();
    }
}
