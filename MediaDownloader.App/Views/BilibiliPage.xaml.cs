using System.Windows;
using System.Windows.Controls;

namespace MediaDownloader.App.Views;

public partial class BilibiliPage : UserControl
{
    public BilibiliPage() => InitializeComponent();

    /// <summary>分 P 复选框列表放进 Popup 里，点按钮才展开，不占常驻布局空间。</summary>
    private void OpenPagePopup_Click(object sender, RoutedEventArgs e) => PagePopup.IsOpen = true;
}
