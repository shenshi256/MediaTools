using System.Net;
using System.Text.Json;

namespace MediaDownloader.Core;

public sealed class BilibiliException : Exception
{
    public int Code { get; }
    public BilibiliException(int code, string message) : base(message) => Code = code;
}

public sealed class ParsedInput
{
    public string? Bvid { get; init; }
    public long? Aid { get; init; }
    public int Page { get; init; } = 1;
}

/// <summary>B站 Web 接口客户端。只做公开可访问内容的解析，不做任何鉴权绕过。</summary>
public sealed class BilibiliClient : IDisposable
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private const string ApiHost = "https://api.bilibili.com";
    private const string PassportHost = "https://passport.bilibili.com";

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private string? _mixinKey;

    public BilibiliClient()
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 16
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.Add("Accept", "*/*");
        _http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
        _http.DefaultRequestHeaders.Add("Origin", "https://www.bilibili.com");
    }

    public void Dispose() => _http.Dispose();

    // ---------- 凭证 ----------

    public void SetCookie(string name, string value)
        => _cookies.Add(new Cookie(name, value, "/", ".bilibili.com"));

    public string? GetCookie(string name)
        => _cookies.GetCookies(new Uri("https://www.bilibili.com"))[name]?.Value;

    public bool IsLoggedIn => !string.IsNullOrEmpty(GetCookie("SESSDATA"));

    // ---------- 初始化：buvid ----------

    public async Task EnsureFingerprintAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(GetCookie("buvid3"))) return;

        try
        {
            var doc = await GetJsonAsync($"{ApiHost}/x/frontend/finger/spi", referer: null, ct: ct);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("b_3", out var b3)) SetCookie("buvid3", b3.GetString()!);
                if (data.TryGetProperty("b_4", out var b4)) SetCookie("buvid4", b4.GetString()!);
            }
        }
        catch
        {
            // 降级：访问首页让服务端下发 cookie
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.bilibili.com/");
                using var resp = await _http.SendAsync(req, ct);
            }
            catch { /* 离线时忽略，解析仍可能成功 */ }
        }
    }

    // ---------- WBI ----------

    private async Task<string> GetMixinKeyAsync(CancellationToken ct)
    {
        if (_mixinKey is not null) return _mixinKey;

        // nav 未登录时返回 code=-101，但 wbi_img 仍然可用，因此这里不检查业务 code
        var doc = await GetJsonRawAsync($"{ApiHost}/x/web-interface/nav", referer: "https://www.bilibili.com/", ct: ct);
        var img = doc.RootElement.GetProperty("data").GetProperty("wbi_img");
        var imgUrl = img.GetProperty("img_url").GetString()!;
        var subUrl = img.GetProperty("sub_url").GetString()!;
        var imgKey = Path.GetFileNameWithoutExtension(new Uri(imgUrl).AbsolutePath);
        var subKey = Path.GetFileNameWithoutExtension(new Uri(subUrl).AbsolutePath);
        _mixinKey = Wbi.BuildMixinKey(imgKey, subKey);
        return _mixinKey;
    }

    // ---------- 输入解析 ----------

    public static ParsedInput? Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();

        var m = System.Text.RegularExpressions.Regex.Match(s, @"(BV[0-9A-Za-z]{10})");
        if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(s, @"\bav(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        int page = 1;
        var pm = System.Text.RegularExpressions.Regex.Match(s, @"[?&]p=(\d+)");
        if (pm.Success && int.TryParse(pm.Groups[1].Value, out var p)) page = p;

        if (!m.Success) return null;

        if (m.Groups[0].Value.StartsWith("BV", StringComparison.Ordinal))
            return new ParsedInput { Bvid = m.Groups[0].Value, Page = page };

        if (long.TryParse(m.Groups[1].Value, out var aid))
            return new ParsedInput { Aid = aid, Page = page };

        return null;
    }

    /// <summary>把 b23.tv 等短链展开为标准链接（失败时返回原串）。</summary>
    public async Task<string> ExpandUrlAsync(string url, CancellationToken ct = default)
    {
        if (!url.Contains("b23.tv", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("bili2233.cn", StringComparison.OrdinalIgnoreCase)) return url;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.Add("User-Agent", UserAgent);
            using var resp = await _http.SendAsync(req, ct);
            return resp.RequestMessage?.RequestUri?.ToString() ?? url;
        }
        catch { return url; }
    }

    // ---------- 视频信息 ----------

    public async Task<VideoMeta> GetVideoInfoAsync(ParsedInput input, CancellationToken ct = default)
    {
        await EnsureFingerprintAsync(ct);
        var mixin = await GetMixinKeyAsync(ct);

        var q = new SortedDictionary<string, string>();
        if (input.Bvid is not null) q["bvid"] = input.Bvid;
        else if (input.Aid is not null) q["aid"] = input.Aid.Value.ToString();
        else throw new BilibiliException(-1, "无法识别的视频标识");

        Wbi.Sign(q, mixin);
        var doc = await GetJsonAsync($"{ApiHost}/x/web-interface/view?{BuildQuery(q)}", RefererOf(input), ct);

        var data = doc.RootElement.GetProperty("data");
        var meta = new VideoMeta
        {
            Bvid = data.GetProperty("bvid").GetString() ?? "",
            Aid = data.GetProperty("aid").GetInt64(),
            Title = data.GetProperty("title").GetString() ?? "",
            Cover = data.TryGetProperty("pic", out var pic) ? pic.GetString() ?? "" : "",
            UpName = data.TryGetProperty("owner", out var o) && o.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            Duration = data.TryGetProperty("duration", out var d) ? d.GetInt64() : 0
        };

        if (data.TryGetProperty("pages", out var pages))
        {
            foreach (var pg in pages.EnumerateArray())
            {
                meta.Pages.Add(new VideoPage
                {
                    Cid = pg.GetProperty("cid").GetInt64(),
                    Page = pg.GetProperty("page").GetInt32(),
                    Part = pg.TryGetProperty("part", out var part) ? part.GetString() ?? "" : "",
                    Duration = pg.TryGetProperty("duration", out var pd) ? pd.GetInt64() : 0
                });
            }
        }
        return meta;
    }

    // ---------- 播放地址 ----------

    /// <summary>取某 cid 下可选的清晰度档位（不含实际地址），并附带最高档的实际地址。</summary>
    public async Task<List<QualityOption>> GetQualityOptionsAsync(string bvid, long cid, CancellationToken ct = default)
    {
        var result = await RequestPlayUrlAsync(bvid, cid, 127, ct);
        return result;
    }

    /// <summary>取指定清晰度的实际流地址。</summary>
    public async Task<QualityOption?> ResolveStreamAsync(string bvid, long cid, int qn, CancellationToken ct = default)
    {
        var list = await RequestPlayUrlAsync(bvid, cid, qn, ct);
        return list.FirstOrDefault(x => x.Id == qn)
            ?? list.FirstOrDefault(x => !string.IsNullOrEmpty(x.VideoUrl))
            ?? list.FirstOrDefault();
    }

    private async Task<List<QualityOption>> RequestPlayUrlAsync(string bvid, long cid, int qn, CancellationToken ct)
    {
        await EnsureFingerprintAsync(ct);
        var mixin = await GetMixinKeyAsync(ct);

        var q = new SortedDictionary<string, string>
        {
            ["bvid"] = bvid,
            ["cid"] = cid.ToString(),
            ["qn"] = qn.ToString(),
            ["fnval"] = "4048",
            ["fnver"] = "0",
            ["fourk"] = "1",
            ["otype"] = "json",
            ["platform"] = "pc"
        };
        Wbi.Sign(q, mixin);

        var doc = await GetJsonAsync($"{ApiHost}/x/player/wbi/playurl?{BuildQuery(q)}", RefererOf(bvid), ct);
        var data = doc.RootElement.GetProperty("data");

        var options = new List<QualityOption>();
        var names = new Dictionary<int, (string name, bool vip)>();
        if (data.TryGetProperty("support_formats", out var formats))
        {
            foreach (var f in formats.EnumerateArray())
            {
                var id = f.GetProperty("quality").GetInt32();
                var name = f.TryGetProperty("new_description", out var nd) ? nd.GetString() ?? id.ToString() : id.ToString();
                var vip = f.TryGetProperty("need_vip", out var nv) && nv.GetBoolean();
                names[id] = (name, vip);
            }
        }

        if (data.TryGetProperty("dash", out var dash))
        {
            var videos = dash.TryGetProperty("video", out var v) ? v.EnumerateArray().ToList() : new();
            var audios = dash.TryGetProperty("audio", out var a) ? a.EnumerateArray().ToList() : new();

            // support_formats 里没有、但 dash 实际返回的档位也要列出来
            foreach (var vid in videos)
            {
                var id = vid.GetProperty("id").GetInt32();
                if (!names.ContainsKey(id)) names[id] = (id.ToString(), false);
            }

            var bestAudio = audios.Count > 0
                ? audios.OrderByDescending(x => x.TryGetProperty("bandwidth", out var b) ? b.GetInt64() : 0).First()
                : (JsonElement?)null;

            foreach (var (id, meta) in names.OrderByDescending(x => x.Key))
            {
                var vid = videos.FirstOrDefault(x => x.GetProperty("id").GetInt32() == id);
                if (vid.ValueKind != JsonValueKind.Object) continue;

                options.Add(new QualityOption
                {
                    Id = id,
                    Name = meta.name,
                    NeedVip = meta.vip,
                    VideoUrl = vid.GetProperty("baseUrl").GetString() ?? "",
                    VideoBackupUrls = Backups(vid),
                    AudioUrl = bestAudio?.GetProperty("baseUrl").GetString(),
                    AudioBackupUrls = bestAudio is null ? Array.Empty<string>() : Backups(bestAudio.Value),
                    Width = vid.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
                    Height = vid.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                    Codecs = vid.TryGetProperty("codecs", out var c) ? c.GetString() ?? "" : ""
                });
            }
        }
        else if (data.TryGetProperty("durl", out var durl))
        {
            var first = durl.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                var url = first.GetProperty("url").GetString() ?? "";
                options.Add(new QualityOption
                {
                    Id = data.TryGetProperty("quality", out var qq) ? qq.GetInt32() : qn,
                    Name = names.TryGetValue(data.TryGetProperty("quality", out var q2) ? q2.GetInt32() : qn, out var nm) ? nm.name : "默认",
                    SingleUrl = url,
                    VideoUrl = url
                });
            }
        }

        return options;
    }

    private static string[] Backups(JsonElement e)
        => e.TryGetProperty("backupUrl", out var b) && b.ValueKind == JsonValueKind.Array
            ? b.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();

    // ---------- 底层 ----------

    public static string RefererOf(ParsedInput input)
        => input.Bvid is not null ? $"https://www.bilibili.com/video/{input.Bvid}" : "https://www.bilibili.com/";

    public static string RefererOf(string bvid) => $"https://www.bilibili.com/video/{bvid}";

    public static string BuildQuery(SortedDictionary<string, string> q)
        => string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    /// <summary>只校验 HTTP 状态，不校验返回体里的 code（用于 nav 这类未登录也返回 -101 的接口）。</summary>
    public async Task<JsonDocument> GetJsonRawAsync(string url, string? referer, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer is not null) req.Headers.Add("Referer", referer);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new BilibiliException((int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}：{Truncate(text, 120)}");
        return JsonDocument.Parse(text);
    }

    public async Task<JsonDocument> GetJsonAsync(string url, string? referer, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer is not null) req.Headers.Add("Referer", referer);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new BilibiliException((int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}：{Truncate(text, 120)}");

        var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
        {
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var codeValue = code.GetInt32();
            doc.Dispose();
            throw new BilibiliException(codeValue, msg);
        }
        return doc;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
