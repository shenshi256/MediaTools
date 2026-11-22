using System.Windows.Controls;
using MediaDownloader.App.ViewModels;

namespace MediaDownloader.App.Views;

public partial class DouyinPage : UserControl
{
    public DouyinPage()
    {
        InitializeComponent();

        // 页面常驻内存、只切可见性，所以用 IsVisibleChanged 而不是 Loaded：
        // 每次真正切到这一页时读一次登录态（第一次会顺带把内嵌浏览器启动起来）。
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is not true) return;
            if (DataContext is not DouyinViewModel vm) return;
            await vm.RefreshLoginStateAsync();
        };
    }
}
