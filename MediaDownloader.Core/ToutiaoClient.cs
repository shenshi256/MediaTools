using System.Net;

namespace MediaDownloader.Core;

/// <summary>
/// 今日头条的 HTTP 部分。和抖音不同，头条这套链路纯 HTTP 就能打通，不需要浏览器发请求：
///   info（m.toutiao.com/i{id}/info/）本身没有签名校验；
///   播放地址走 VOD 的 GetPlayInfo，鉴权参数整个预置在接口返回的 token 里。
/// 内嵌浏览器只用来扫码登录，登录后的 Cookie 可以带进来（见 ToutiaoBrowser）。
/// </summary>
public sealed class ToutiaoClient : IDisposable
{
    /// <summary>移动端 UA。info 接口本来是给 m.toutiao.com 用的，用移动 UA 最贴近它的预期。</summary>
    public const string UserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

    /// <summary>
    /// 内嵌浏览器用桌面 UA：登录要走网页版，桌面版的二维码看得清、也不会被导去下载 App。
    /// 和接口 UA 分开，各用各的。
    /// </summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    public const string Referer = "https://www.toutiao.com/";

    private readonly HttpClient _http;

    public ToutiaoClient()
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
    /// 把用户输入（链接 / 分享文案 / 纯数字 ID）解析成条目 ID。
    /// m.toutiao.com/is/xxx 短链只是 302，跟着走一次即可，不需要签名。
    /// </summary>
    public async Task<string> ResolveGroupIdAsync(string input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ToutiaoException("请先粘贴今日头条链接或条目 ID");

        var id = ToutiaoParser.TryExtractGroupId(input);
        if (id is not null) return id;

        var shortUrl = ToutiaoParser.TryExtractShortUrl(input);
        if (shortUrl is null)
            throw new ToutiaoException("看不懂这个链接。支持 toutiao.com/video/…、m.toutiao.com/is/… 和纯数字条目 ID");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, shortUrl);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            id = ToutiaoParser.TryExtractGroupId(finalUrl);
            if (id is null)
                throw new ToutiaoException("短链没能解析出条目 ID，可能已失效：" + finalUrl);
            return id;
        }
        catch (ToutiaoException) { throw; }
        catch (Exception ex)
        {
            throw new ToutiaoException("展开短链失败：" + ex.Message);
        }
    }

    /// <summary>取元数据（标题 / 作者 / 封面 / 时长 / 播放 token）。</summary>
    public async Task<ToutiaoVideo> FetchInfoAsync(string groupId, string? cookie = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            throw new ToutiaoException("条目 ID 为空");

        var url = $"https://m.toutiao.com/i{groupId}/info/";
        var body = await GetStringAsync(url, cookie, ct);
        return ToutiaoParser.ParseInfo(body);
    }

    /// <summary>用 info 里的 token 换清晰度档位。</summary>
    public async Task<IReadOnlyList<ToutiaoStream>> FetchPlayInfoAsync(string token, CancellationToken ct = default)
    {
        var url = ToutiaoParser.BuildPlayInfoUrl(token);
        var body = await GetStringAsync(url, cookie: null, ct);
        return ToutiaoParser.ParsePlayInfo(body);
    }

    /// <summary>一步到位：链接到完整解析结果。cookie 可为空（不登录也能下，登录只影响受限内容）。</summary>
    public async Task<ToutiaoVideo> FetchAsync(string input, string? cookie = null, CancellationToken ct = default)
    {
        var groupId = await ResolveGroupIdAsync(input, ct);
        var info = await FetchInfoAsync(groupId, cookie, ct);
        var streams = await FetchPlayInfoAsync(info.PlayToken, ct);
        return ToutiaoParser.Combine(info, streams);
    }

    private async Task<string> GetStringAsync(string url, string? cookie, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri(Referer);
            if (!string.IsNullOrWhiteSpace(cookie))
                req.Headers.TryAddWithoutValidation("Cookie", cookie);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if ((int)resp.StatusCode != 200)
                throw new ToutiaoException($"请求失败：HTTP {(int)resp.StatusCode}");

            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (ToutiaoException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new ToutiaoException("已取消"); }
        catch (TaskCanceledException) { throw new ToutiaoException("请求超时，请检查网络"); }
        catch (Exception ex) { throw new ToutiaoException("请求失败：" + ex.Message); }
    }
}
