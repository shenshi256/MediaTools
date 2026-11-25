using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MediaDownloader.App.ViewModels;
using MediaDownloader.Core;

namespace MediaDownloader.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        Icon = UiIcons.App; // 应用图标：标题栏/任务栏/Alt-Tab 统一，不再是黑方块
        _vm = new MainViewModel
        {
            QrLoginHandler = ShowQrLogin,
            DuplicateHandler = ShowDuplicate
        };
        _vm.Douyin.DuplicateHandler = ShowDuplicate;
        _vm.Douyin.ConfirmLogoutHandler = ConfirmLogout;
        _vm.Douyin.ToastHandler = ShowToast;
        _vm.Toutiao.DuplicateHandler = ShowDuplicate;
        _vm.Toutiao.ConfirmLogoutHandler = ConfirmLogout;
        _vm.Toutiao.ToastHandler = ShowToast;
        _vm.Tencent.DuplicateHandler = ShowDuplicate;
        _vm.Youtube.DuplicateHandler = ShowDuplicate;
        _vm.Youtube.CookieHelpHandler = ShowCookieHelp;
        _vm.Compress.DuplicateHandler = ShowDuplicate;
        DataContext = _vm;
        Closed += (_, _) => _vm.Dispose();

        // 冒烟自检：设 JJD_SMOKE=1 时依次切到每个页面，把"当前可见的页面"写进日志再退出。
        // XAML 模板写错只在运行时才炸，编译期完全看不出来，所以需要这个开关来批量过一遍。
        // 设为 ttlogin 时额外真点一次头条的扫码登录，用来看登录流程能不能跑起来。
        var smoke = Environment.GetEnvironmentVariable("JJD_SMOKE");
        if (smoke == "1")
            Loaded += async (_, _) => await RunSmokeAsync();
        else if (smoke == "ttlogin")
            Loaded += async (_, _) => await RunToutiaoLoginSmokeAsync();
    }

    /// <summary>左侧导航点击。用 Tag 携带页面 key，避免为每个页面写一个事件。</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 期间 XAML 里的 IsChecked="True" 会先触发一次，此时还没有 DataContext
        if (sender is RadioButton { Tag: string key } && DataContext is MainViewModel vm)
            vm.CurrentPageKey = key;
    }

    // ---------- 冒烟自检 ----------

    private static readonly string[] SmokePages =
        { "Bilibili", "Youtube", "Douyin", "Toutiao", "Tencent", "Trim", "Merge", "Separate", "Compress", "Settings" };

    private async Task RunSmokeAsync()
    {
        var log = new StringBuilder();
        log.AppendLine($"[smoke] begin {DateTime.Now:HH:mm:ss}");

        foreach (var key in SmokePages)
        {
            _vm.CurrentPageKey = key;

            // 让布局和模板应用跑完，再数可见页面
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(220);

            var visible = PageHost.Children
                .OfType<FrameworkElement>()
                .Where(x => x.Visibility == Visibility.Visible)
                .Select(x => x.GetType().Name)
                .ToList();

            log.AppendLine($"[smoke] {key} visible={visible.Count}:{string.Join(",", visible)}");
        }

        log.AppendLine("[smoke] end");
        try { File.AppendAllText(AppPaths.LogFile, log.ToString()); } catch { /* 忽略 */ }

        Close();
    }

    /// <summary>
    /// 头条扫码登录的冒烟：切到头条页 → 点登录按钮 → 观察十几秒 → 把状态写日志退出。
    /// 不真的等人扫码，只验证「点得动、窗口弹得出、页面加载得到、cookie 会被种下」。
    /// </summary>
    private async Task RunToutiaoLoginSmokeAsync()
    {
        var log = new StringBuilder();
        log.AppendLine($"[ttlogin] begin {DateTime.Now:HH:mm:ss}");

        _vm.CurrentPageKey = "Toutiao";
        await Dispatcher.Yield(DispatcherPriority.Background);
        await Task.Delay(3500); // 等内嵌浏览器初始化完

        log.AppendLine($"[ttlogin] loginStatus={_vm.Toutiao.LoginStatus}");
        log.AppendLine($"[ttlogin] canLogin={_vm.Toutiao.LoginCommand.CanExecute(null)}");

        try
        {
            _vm.Toutiao.LoginCommand.Execute(null);
            log.AppendLine("[ttlogin] LoginCommand 已执行");
        }
        catch (Exception ex)
        {
            log.AppendLine("[ttlogin] LoginCommand 抛异常：" + ex.GetType().Name + " " + ex.Message);
        }

        // 观察页面加载 + 轮询，中途抓几次状态
        for (var i = 1; i <= 5; i++)
        {
            await Task.Delay(2000);
            log.AppendLine($"[ttlogin] t={i * 2}s status={_vm.Toutiao.StatusMessage}");
        }

        try { File.AppendAllText(AppPaths.LogFile, log.ToString()); } catch { /* 忽略 */ }
        Close();
    }

    private DuplicateChoice ShowDuplicate(IReadOnlyList<string> files)
    {
        var window = new DuplicateWindow(files) { Owner = this };
        return window.ShowDialog() == true ? window.Choice : DuplicateChoice.Cancel;
    }

    private void ShowCookieHelp()
    {
        var window = new CookieHelpWindow { Owner = this };
        window.ShowDialog();
    }

    private bool ConfirmLogout()
    {
        var result = MessageBox.Show(this, "确定要退出登录吗？", "退出登录",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.OK;
    }

    private void ShowToast(string message)
    {
        Toast.Show(message);
    }

    private Dictionary<string, string>? ShowQrLogin(QrSession session, LoginService service)
    {
        var window = new LoginWindow(session, service) { Owner = this };
        return window.ShowDialog() == true ? window.Cookies : null;
    }
}
