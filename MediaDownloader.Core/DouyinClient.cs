using System.Net;

namespace MediaDownloader.Core;

/// <summary>
/// 抖音的 HTTP 部分：只负责把用户输入的链接变成作品 ID。
/// 真正的详情请求由内嵌浏览器页面自己发出（抖音的 Argus 反爬要求 a_bogus 签名，
/// 那个签名由页面里的混淆 JS 生成，我们不重复实现），详见 App 里的 DouyinBrowser。
/// </summary>
public sealed class DouyinClient : IDisposable
{
    /// <summary>桌面 UA。抓详情时 WebView2 也用它，保持一致。</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    public const string Referer = "https://www.douyin.com/";

    private readonly HttpClient _http;

    public DouyinClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// 把用户输入（链接 / 分享口令 / 纯数字 ID）解析成作品 ID。
    /// 短链 v.douyin.com 只是 302，不需要签名，直接跟一次就行。
    /// </summary>
    public async Task<string> ResolveAwemeIdAsync(string input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new DouyinException("请先粘贴抖音链接或作品 ID");

        var id = DouyinParser.TryExtractAwemeId(input);
        if (id is not null) return id;

        var shortUrl = DouyinParser.TryExtractShortUrl(input);
        if (shortUrl is null)
            throw new DouyinException("看不懂这个链接。支持 douyin.com/video/…、v.douyin.com/… 和纯数字作品 ID");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, shortUrl);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            id = DouyinParser.TryExtractAwemeId(finalUrl);
            if (id is null)
                throw new DouyinException("短链没能解析出作品 ID，可能已失效：" + finalUrl);
            return id;
        }
        catch (DouyinException) { throw; }
        catch (Exception ex)
        {
            throw new DouyinException("展开短链失败：" + ex.Message);
        }
    }
}
