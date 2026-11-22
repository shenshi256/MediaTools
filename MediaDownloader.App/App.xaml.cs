using System.IO;
using System.Windows;
using MediaDownloader.Core;

namespace MediaDownloader.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 界面线程和后台线程都兜住：出了异常先落盘，用户才有东西可发给我们。
        // Log.Error 走可选的 logs\ 文件日志（受设置开关控制）；
        // AppendCrashLog 永远写 %APPDATA% 的 app.log，是崩溃兜底，不受开关影响。
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("app", args.Exception);
            AppendCrashLog(args.Exception);
            MessageBox.Show("程序遇到未处理的错误：\n\n" + args.Exception.Message +
                            "\n\n详细信息已写入：\n" + AppPaths.LogFile,
                "MediaDownloader", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Error("app", ex);
                AppendCrashLog(ex);
            }
        };

        try { AppPaths.Ensure(); } catch { /* 目录建不出来也不该拦住启动 */ }

        // 文件日志默认关闭；设置里开了才写 exe 目录下的 logs\。
        // 注意顺序：MainViewModel 还没构造，这里先按保存的设置接上，
        // 之后设置页改开关时 MainViewModel 会同步 Log.Enabled。
        try { Log.Enabled = Settings.Load().EnableFileLog; } catch { /* 忽略 */ }
        Log.Info("app", $"启动  v{typeof(App).Assembly.GetName().Version}  {AppPaths.ExeDir}");

        base.OnStartup(e);
    }

    /// <summary>崩溃兜底：无论文件日志开关与否都写 %APPDATA% 的 app.log。</summary>
    private static void AppendCrashLog(Exception ex)
    {
        try
        {
            AppPaths.Ensure();
            File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch
        {
            // 连日志都写不了就只能放弃
        }
    }
}
