using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DyProbe;

/// <summary>
/// 临时探针：用真实 WebView2 打开抖音视频页，拦截页面自己发出的接口响应并落盘。
/// 只为摸清真实字段结构，不参与正式功能。
/// 用法：DyProbe.exe &lt;url&gt; &lt;输出目录&gt; [桌面UA|移动UA]
/// </summary>
public static class Program
{
    private const string DesktopUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    private const string MobileUa =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

    [STAThread]
    public static void Main(string[] args)
    {
        var url = args.Length > 0 ? args[0] : "";
        var outDir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "probe-out");
        var ua = args.Length > 2 && args[2] == "mobile" ? MobileUa : DesktopUa;

        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine("用法：DyProbe.exe <url> <输出目录> [desktop|mobile]");
            return;
        }

        Directory.CreateDirectory(outDir);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var win = new Window
        {
            Width = 1100,
            Height = 820,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Title = "DyProbe"
        };

        var view = new WebView2();
        win.Content = view;
        win.Loaded += async (_, _) => await RunAsync(view, url, outDir, ua);
        app.Run(win);
    }

    private static async Task RunAsync(WebView2 view, string url, string outDir, string ua)
    {
        var log = new StringBuilder();
        var index = 0;

        try
        {
            var userData = Path.Combine(outDir, "wv2data");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await view.EnsureCoreWebView2Async(env);

            var core = view.CoreWebView2;
            core.Settings.UserAgent = ua;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;

            core.WebResourceResponseReceived += async (_, e) =>
            {
                try
                {
                    var uri = e.Request.Uri;
                    if (!(uri.Contains("/aweme/") || uri.Contains("detail") || uri.Contains("iteminfo")))
                        return;

                    var body = await e.Response.GetContentAsync();
                    using var reader = new StreamReader(body, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync();
                    if (text.Length < 200) return;

                    var n = Interlocked.Increment(ref index);
                    var name = $"{n:00}_{Safe(uri)}.json";
                    await File.WriteAllTextAsync(Path.Combine(outDir, name), text, Encoding.UTF8);
                    lock (log) log.AppendLine($"saved {name} ({text.Length} chars) from {uri[..Math.Min(160, uri.Length)]}");
                }
                catch (Exception ex)
                {
                    lock (log) log.AppendLine("intercept error: " + ex.Message);
                }
            };

            var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) =>
            {
                lock (log) log.AppendLine($"nav completed: success={e.IsSuccess} status={e.HttpStatusCode} url={core.Source}");
                if (e.IsSuccess) nav.TrySetResult(true);
            };

            core.Navigate(url);

            await Task.WhenAny(nav.Task, Task.Delay(30000));

            // 给页面里的异步请求留时间，同时读一下 <video>
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(1000);
                try
                {
                    var src = await core.ExecuteScriptAsync(
                        "(function(){var v=document.querySelector('video');return v? v.currentSrc||v.src||'' : '';})()");
                    if (!string.IsNullOrEmpty(src) && src != "\"\"")
                    {
                        lock (log) log.AppendLine($"video.src={src}");
                        await File.WriteAllTextAsync(Path.Combine(outDir, "video.txt"), src, Encoding.UTF8);
                        break;
                    }
                }
                catch { /* 页面可能还在跳转 */ }

                if (index > 0 && i >= 5) break;
            }

            try
            {
                var html = await core.ExecuteScriptAsync("document.documentElement.outerHTML");
                await File.WriteAllTextAsync(Path.Combine(outDir, "dom.html"), Unescape(html), Encoding.UTF8);
                var title = await core.ExecuteScriptAsync("document.title");
                lock (log) log.AppendLine("title=" + title);
            }
            catch { }
        }
        catch (Exception ex)
        {
            lock (log) log.AppendLine("fatal: " + ex);
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "probe.log"), log.ToString(), Encoding.UTF8);
        Application.Current.Shutdown();
    }

    /// <summary>ExecuteScriptAsync 返回的是 JSON 字符串字面量，这里还原成原始文本。</summary>
    private static string Unescape(string jsonLiteral)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>(jsonLiteral) ?? "";
        }
        catch
        {
            return jsonLiteral;
        }
    }

    private static string Safe(string uri)
    {
        var path = Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.AbsolutePath : uri;
        var sb = new StringBuilder();
        foreach (var c in path)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var s = sb.ToString().Trim('_');
        return s.Length > 70 ? s[..70] : s;
    }
}
