using System.IO;
using System.Windows;
using System.Windows.Controls;
using MediaDownloader.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MediaDownloader.App.Services;

/// <summary>
/// 今日头条内嵌浏览器。
///
/// 和 <see cref="DouyinBrowser"/> 不一样，这里**不拦截任何请求**：
/// 头条的 info 接口和播放地址接口纯 HTTP 就能打通（见 ToutiaoClient），
/// 所以浏览器唯一的职责就是"让用户扫码登录"，再把登录后的 Cookie 导出来给 HttpClient 用。
///
/// 登录态落在 <see cref="AppPaths.ToutiaoProfileDir"/> 的 cookie 里，跨启动复用，
/// 和抖音的 profile 完全分开，互不影响。
/// </summary>
public sealed class ToutiaoBrowser : IDisposable
{
    // WebView2 必须有真实 HWND 才能初始化，所以不能 Hide()，只能把窗口挪到屏幕外
    private const double OffscreenX = -6000;
    private const double OffscreenY = -6000;

    /// <summary>登录成功后头条会种这些 cookie，命中任意一个就当已登录。</summary>
    private static readonly string[] LoginCookieNames = { "sessionid", "sessionid_ss", "sid_guard" };

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
            Title = "今日头条 登录",
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
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.ToutiaoProfileDir);
            await view.EnsureCoreWebView2Async(env);

            var core = view.CoreWebView2
                       ?? throw new ToutiaoException("内嵌浏览器初始化失败（WebView2 运行时可能没装）");

            core.Settings.UserAgent = ToutiaoClient.BrowserUserAgent;
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

    // ---------- 登录 ----------

    /// <summary>
    /// 把浏览器移到屏幕中央让用户扫码，成功后挪回屏幕外。返回是否登录成功。
    /// 头条网页版未登录时右上角有「登录」按钮，点开就是二维码，所以这里会顺手点一下，
    /// 点不到也没关系，用户自己点即可。
    /// </summary>
    public async Task<bool> ShowLoginAsync(CancellationToken ct = default)
    {
        await EnsureReadyAsync();
        var core = _view!.CoreWebView2!;

        ShowOnScreen();
        try
        {
            core.Navigate("https://www.toutiao.com/");

            // 等首屏渲染完，再尝试点开登录弹窗（失败无所谓，纯属省一步操作）
            await Task.Delay(2000, ct);
            await TryClickLoginAsync(core);

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

    /// <summary>找页面上文字正好是「登录」的元素点一下。选择器保守，宁可点不到也不乱点。</summary>
    private static async Task TryClickLoginAsync(CoreWebView2 core)
    {
        const string script = """
        (function () {
            var nodes = Array.from(document.querySelectorAll('a,button,span,div'));
            for (var i = 0; i < nodes.length; i++) {
                var t = (nodes[i].textContent || '').trim();
                if (t === '登录' || t === '登录/注册') {
                    try { nodes[i].click(); return true; } catch (e) { }
                }
            }
            return false;
        })();
        """;

        try { await core.ExecuteScriptAsync(script); }
        catch { /* 页面还没加载完或脚本被拦，忽略 */ }
    }

    public async Task<bool> HasLoginCookieAsync(CancellationToken ct = default)
    {
        if (_view?.CoreWebView2 is null) return false;
        try
        {
            var cookies = await _view.CoreWebView2.CookieManager.GetCookiesAsync("https://www.toutiao.com/");
            return cookies.Any(c =>
                LoginCookieNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(c.Value));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把当前 Cookie 拼成 HttpClient 能直接用的 header 值。没登录就返回 null。</summary>
    public async Task<string?> ExportCookieHeaderAsync()
    {
        if (_view?.CoreWebView2 is null) return null;

        try
        {
            var cookies = await _view.CoreWebView2.CookieManager.GetCookiesAsync("https://www.toutiao.com/");
            var pairs = cookies
                .Where(c => !string.IsNullOrEmpty(c.Name))
                .Select(c => c.Name + "=" + c.Value)
                .ToArray();

            return pairs.Length == 0 ? null : string.Join("; ", pairs);
        }
        catch
        {
            return null;
        }
    }

    public void SignOut()
    {
        try { _view?.CoreWebView2?.CookieManager.DeleteAllCookies(); }
        catch { /* 忽略 */ }
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
