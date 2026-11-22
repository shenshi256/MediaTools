using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MediaDownloader.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MediaDownloader.App.Services;

/// <summary>
/// 抖音内嵌浏览器。
///
/// 为什么需要它：抖音的详情接口带着 Argus 反爬，请求里必须有 a_bogus 签名，
/// 那个签名由页面里的混淆 JS 算出来。与其重复实现一套随时会失效的算法，
/// 不如让真实页面自己去请求，我们只把它的响应拦下来。
///
/// 同一个实例兼做扫码登录：登录态本来就落在 UserDataFolder 的 cookie 里，
/// 解析时复用同一个 profile，登录过就有登录态，不需要额外导 cookie。
/// </summary>
public sealed class DouyinBrowser : IDisposable
{
    // 平时把窗口挪到屏幕外：WebView2 必须有真实 HWND 才能初始化，
    // 所以不能 Hide()，只能移走。
    private const double OffscreenX = -6000;
    private const double OffscreenY = -6000;

    private Window? _window;
    private WebView2? _view;
    private Task? _initTask;

    public bool IsReady => _view?.CoreWebView2 is not null;

    // ---------- 初始化 ----------

    public async Task EnsureReadyAsync()
    {
        if (IsReady) return;

        if (_initTask is null || _initTask.IsFaulted)
            _initTask = InitAsync();

        await _initTask;
    }

    private async Task InitAsync()
    {
        var window = new Window
        {
            Width = 1120,
            Height = 840,
            Left = OffscreenX,
            Top = OffscreenY,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ShowInTaskbar = false,
            ShowActivated = false,
            Title = "抖音",
            Icon = UiIcons.Blank, // 标题栏不显示图标
            Background = System.Windows.Media.Brushes.White
        };

        var view = new WebView2();
        window.Content = view;

        // 必须先 Show 出来才会有 HWND，否则 EnsureCoreWebView2Async 会抛
        window.Show();

        try
        {
            AppPaths.Ensure();
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.DouyinProfileDir);
            await view.EnsureCoreWebView2Async(env);

            var core = view.CoreWebView2
                       ?? throw new DouyinException("内嵌浏览器初始化失败（WebView2 运行时可能没装）");

            core.Settings.UserAgent = DouyinClient.UserAgent;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            _window = window;
            _view = view;
        }
        catch
        {
            try { window.Close(); } catch { /* 忽略 */ }
            throw;
        }
    }

    // ---------- 解析 ----------

    /// <summary>打开视频页，等页面自己请求详情接口，把响应体拿回来解析。</summary>
    public async Task<DouyinVideo> FetchAsync(string awemeId, CancellationToken ct = default)
    {
        await EnsureReadyAsync();
        var core = _view!.CoreWebView2!;

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        async void Handler(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                if (tcs.Task.IsCompleted) return;
                if (!e.Request.Uri.Contains("/aweme/v1/web/aweme/detail/", StringComparison.Ordinal)) return;

                var stream = await e.Response.GetContentAsync();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(CancellationToken.None);

                // SPA 可能并发发好几次，只认带 aweme_detail 的那一个
                if (text.Contains("\"aweme_detail\"", StringComparison.Ordinal))
                    tcs.TrySetResult(text);
            }
            catch
            {
                // 这一次读不到就算了，等下一次回调
            }
        }

        core.WebResourceResponseReceived += Handler;
        try
        {
            MoveOffscreen();
            core.Navigate($"https://www.douyin.com/video/{awemeId}");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

            string json;
            try
            {
                json = await tcs.Task;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new DouyinException("等待页面返回视频信息超时（35 秒）。可能网络太慢、被风控，或者这条视频需要登录");
            }

            return DouyinParser.Parse(json);
        }
        finally
        {
            core.WebResourceResponseReceived -= Handler;
        }
    }

    // ---------- 登录 ----------

    /// <summary>把浏览器移到屏幕中央让用户扫码，成功后挪回屏幕外。返回是否登录成功。</summary>
    public async Task<bool> ShowLoginAsync(CancellationToken ct = default)
    {
        await EnsureReadyAsync();
        var core = _view!.CoreWebView2!;

        ShowOnScreen();
        try
        {
            core.Navigate(DouyinClient.Referer);

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1200, ct);
                if (await HasLoginCookieAsync(default)) return true;
            }
            return false;
        }
        finally
        {
            MoveOffscreen();
        }
    }

    public async Task<bool> HasLoginCookieAsync(CancellationToken ct = default)
    {
        if (_view?.CoreWebView2 is null) return false;
        try
        {
            var cookies = await _view.CoreWebView2.CookieManager.GetCookiesAsync(DouyinClient.Referer);
            return cookies.Any(c =>
                (c.Name is "sessionid" or "sessionid_ss") && !string.IsNullOrEmpty(c.Value));
        }
        catch
        {
            return false;
        }
    }

    public void SignOut()
    {
        try { _view?.CoreWebView2?.CookieManager.DeleteAllCookies(); }
        catch { /* 忽略 */ }
    }

    /// <summary>唤醒浏览器（浏览器窗口在屏幕外时用于把它提到前面）。</summary>
    public void BringToFrontIfVisible()
    {
        if (_window is { Topmost: true }) _window.Activate();
    }

    // ---------- 窗口位置 ----------

    private void ShowOnScreen()
    {
        if (_window is null) return;

        var wa = SystemParameters.WorkArea;
        _window.Width = Math.Min(1160, Math.Max(720, wa.Width - 120));
        _window.Height = Math.Min(880, Math.Max(560, wa.Height - 120));
        _window.Left = wa.Left + (wa.Width - _window.Width) / 2;
        _window.Top = wa.Top + (wa.Height - _window.Height) / 2;
        _window.Topmost = true;
        _window.Activate();
        _window.Focus();
    }

    private void MoveOffscreen()
    {
        if (_window is null) return;
        _window.Topmost = false;
        _window.Left = OffscreenX;
        _window.Top = OffscreenY;
    }

    // ---------- 释放 ----------

    public void Dispose()
    {
        try
        {
            if (_view is not null) _view.Dispose();
            if (_window is not null) _window.Close();
        }
        catch
        {
            // 关闭阶段出错不关心
        }
    }
}
