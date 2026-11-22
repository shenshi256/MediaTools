using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaDownloader.Core;

/// <summary>
/// 腾讯视频解析。和 <see cref="ToutiaoParser"/> 一样做成纯静态、只吃字符串，
/// 不用联网也能跑回归（见 tools/MediaCli 的腾讯段）。
///
/// 实测得到的两个数据来源：
///   1. node.video.qq.com/x/api/float_vinfo2?cid=xxx  —— 专辑元数据 + video_ids 数组
///   2. vv.video.qq.com/getinfo?vids=xxx&amp;platform=101001&amp;charge=0&amp;otype=json
///      —— 清晰度档位 + fvkey + 直链前缀，**不需要 ckey 签名，也不需要登录**
///
/// 第 2 步返回的是完整 mp4 直链（dltype=1、fn 以 .mp4 结尾、cl.fc=0），不是 m3u8 分片，
/// 所以下载走现有的 DownloadEngine 就够，不需要 HLS 引擎。
/// </summary>
public static class TencentParser
{
    public const string Referer = "https://v.qq.com/";

    /// <summary>getinfo 的固定参数。platform=101001 是网页端，charge=0 表示不带付费参数。</summary>
    public const string GetInfoEndpoint = "https://vv.video.qq.com/getinfo";

    /// <summary>专辑信息接口。</summary>
    public const string AlbumEndpoint = "https://node.video.qq.com/x/api/float_vinfo2";

    /// <summary>
    /// 拼 getinfo 地址。<paramref name="defn"/> 决定返回哪个档位的文件名，
    /// 传 fhd 表示"要最高的"——未登录时腾讯会自动降到账号允许的上限。
    /// </summary>
    public static string BuildGetInfoUrl(string vid, string defn = "fhd")
        => $"{GetInfoEndpoint}?vids={Uri.EscapeDataString(vid)}&platform=101001&charge=0&otype=json&defn={Uri.EscapeDataString(defn)}&defnpayver=1";

    public static string BuildAlbumUrl(string cid)
        => $"{AlbumEndpoint}?cid={Uri.EscapeDataString(cid)}";

    // ---------- 专辑信息 ----------

    /// <summary>
    /// 解析 float_vinfo2。这个接口对短剧 / 电影都返回 video_ids，
    /// 但**没有**每集的标题和时长，那些要靠 getinfo 逐集补（见 CombineEpisode）。
    /// </summary>
    public static TencentVideo ParseAlbum(string responseJson, string cidFallback = "")
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            throw new TencentException("专辑接口返回空内容");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(responseJson.TrimStart('\uFEFF')); }
        catch (JsonException ex) { throw new TencentException("专辑接口返回的不是合法 JSON：" + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;
            var c = Prop(root, "c")
                ?? throw new TencentException("这个链接没有专辑信息，可能不是视频页");

            var ids = new List<string>();
            if (Prop(c, "video_ids") is { ValueKind: JsonValueKind.Array } arr)
                foreach (var v in arr.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                        ids.Add(s);

            // 单条视频的页面上没有 video_ids，退回到 c.vid
            if (ids.Count == 0 && GetString(c, "vid") is { Length: > 0 } single)
                ids.Add(single);

            if (ids.Count == 0)
                throw new TencentException("专辑里没有可下载的视频");

            return new TencentVideo
            {
                Cid = string.IsNullOrEmpty(GetString(c, "column_id")) || GetString(c, "column_id") == "None"
                    ? cidFallback
                    : GetString(c, "column_id"),
                Title = GetString(c, "title"),
                Description = GetString(c, "description"),
                CoverUrl = GetString(c, "pic"),
                Episodes = ids.Select(id => new TencentEpisode { Vid = id }).ToList()
            };
        }
    }

    // ---------- 单集播放信息 ----------

    /// <summary>
    /// 解析 getinfo 的响应，得到一个能下载的集（含清晰度）。
    ///
    /// 免费 / 会员的判定靠两个字段，都是实测出来的：
    ///   - 顶层 <c>exem</c>：0 = 正常，非 0（会员内容实测是 2）= 受限；
    ///   - <c>vl.vi[0].drm</c>：0 = 无 DRM，非 0 = 加密流。
    /// 任一命中就判为不可下载，并且**不去拼地址**——避免越过账号权限。
    /// </summary>
    public static TencentEpisode ParsePlayInfo(string responseJson, string vidFallback = "")
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            throw new TencentException("播放接口返回空内容");

        var json = ExtractJsonObject(responseJson);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new TencentException("播放接口返回的不是合法 JSON：" + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;
            var vid = vidFallback;

            var viList = Prop(root, "vl") is { } vl ? Prop(vl, "vi") : null;
            JsonElement vi = default;
            var hasVi = viList is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0;
            if (hasVi) vi = viList!.Value[0];

            if (hasVi && GetString(vi, "vid") is { Length: > 0 } v) vid = v;

            // em 非 0 是接口级失败（下架 / 地区限制 / 参数不对）
            var em = GetLong(root, "em") ?? 0;
            if (em != 0)
                throw new TencentException($"播放接口返回 em={em}，该视频可能已下架或受地区限制");

            // ---- 受限判定 ----
            var exem = GetLong(root, "exem") ?? 0;
            var viDrm = hasVi ? GetLong(vi, "drm") ?? 0 : 0;

            string? blockReason = null;
            if (exem != 0) blockReason = "需会员，本工具不下载";
            else if (viDrm != 0) blockReason = "DRM 加密流，本工具不下载";
            else if (!hasVi || string.IsNullOrEmpty(GetString(vi, "fn"))) blockReason = "拿不到播放地址，可能需会员";

            if (blockReason is not null)
            {
                return new TencentEpisode
                {
                    Vid = vid,
                    Title = hasVi ? GetString(vi, "ti") : "",
                    Duration = TimeSpan.Zero,
                    CanDownload = false,
                    BlockReason = blockReason
                };
            }

            var title = GetString(vi, "ti");
            var dur = GetDouble(vi, "td") ?? 0;

            // 这次实际给的是哪一档：未登录要 1080P 会被降到 720P，先问清楚
            var maxHeight = MatchHeight(root, GetLong(vi, "br") ?? 0);

            var qualities = ParseQualities(root);
            if (maxHeight > 0)
                qualities = qualities.Where(q => q.Height <= maxHeight).ToList();

            if (qualities.Count == 0)
            {
                return new TencentEpisode
                {
                    Vid = vid,
                    Title = title,
                    Duration = TimeSpan.FromSeconds(dur),
                    MaxAvailableHeight = maxHeight,
                    CanDownload = false,
                    BlockReason = maxHeight > 0
                        ? $"解析不到可用档位（上限 {maxHeight}P）"
                        : "没有可下载的清晰度"
                };
            }

            return new TencentEpisode
            {
                Vid = vid,
                Title = title,
                Duration = TimeSpan.FromSeconds(dur),
                MaxAvailableHeight = maxHeight,
                CanDownload = true,
                Qualities = qualities
            };
        }
    }

    /// <summary>把 getinfo 的结果补到专辑里的某一集上。</summary>
    public static TencentEpisode Merge(TencentEpisode slot, TencentEpisode detail)
        => new()
        {
            Vid = string.IsNullOrEmpty(detail.Vid) ? slot.Vid : detail.Vid,
            Title = string.IsNullOrEmpty(detail.Title) ? slot.Title : detail.Title,
            Number = slot.Number,
            Duration = detail.Duration,
            CanDownload = detail.CanDownload,
            BlockReason = detail.BlockReason,
            MaxAvailableHeight = detail.MaxAvailableHeight,
            Qualities = detail.Qualities
        };

    // ---------- 清晰度 ----------

    /// <summary>
    /// fl.fi[] 是清晰度表，每家一条：带 cname（「标清;(480P)」）、fs（字节数）、drm、formatdefn。
    ///
    /// 注意：**一份 getinfo 响应只给当前 defn 那一个档位的文件名**（fn 里的 .f622/.f632 是内部档位号，
    /// 和 fl 的 id 对不上，没法靠替换推出来）。所以这里只登记档位元数据，
    /// 真正的地址留到下载时按 <c>formatdefn</c> 再请求一次（见 <see cref="ParseStreamUrl"/>）。
    /// </summary>
    private static List<TencentQuality> ParseQualities(JsonElement root)
    {
        var list = new List<TencentQuality>();

        if (Prop(root, "fl") is not { } fl || Prop(fl, "fi") is not { ValueKind: JsonValueKind.Array } fi)
            return list;

        foreach (var f in fi.EnumerateArray())
        {
            var cname = GetString(f, "cname");
            var height = ParseHeight(cname);
            if (height <= 0) height = (int)(GetLong(f, "height") ?? 0);

            // 单个档位自己带 drm 标记（4K / HDR 那些），有就跳过
            if ((GetLong(f, "drm") ?? 0) != 0) continue;

            var defn = GetString(f, "formatdefn");
            if (defn.Length == 0) defn = GetString(f, "name");
            if (defn.Length == 0) continue;

            list.Add(new TencentQuality
            {
                Name = GetString(f, "name"),
                Defn = defn,
                Height = height,
                SizeBytes = GetLong(f, "fs") ?? 0
            });
        }

        return list
            .GroupBy(q => q.Defn, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(q => q.Height)
            .ToList();
    }

    /// <summary>
    /// 从一次 getinfo 响应里取出**当前 defn** 的直链：ui[0].url + fn + "?vkey=" + fvkey。
    /// 反过来也能读出这次实际给的是哪一档（未登录要 1080P 时腾讯会悄悄降到 720P）。
    /// </summary>
    public static (string Url, string[] Backups, int Height) ParseStreamUrl(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            throw new TencentException("播放接口返回空内容");

        var json = ExtractJsonObject(responseJson);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new TencentException("播放接口返回的不是合法 JSON：" + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;
            var viList = Prop(root, "vl") is { } vl ? Prop(vl, "vi") : null;
            if (viList is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() == 0)
                throw new TencentException("播放接口没有返回视频信息");

            var vi = arr[0];
            var fn = GetString(vi, "fn");
            var fvkey = GetString(vi, "fvkey");
            if (fn.Length == 0 || fvkey.Length == 0)
                throw new TencentException("拿不到播放地址，可能需会员或该档位不可用");

            var bases = new List<string>();
            if (Prop(vi, "ul") is { } ul && Prop(ul, "ui") is { ValueKind: JsonValueKind.Array } ui)
                foreach (var u in ui.EnumerateArray())
                    if (GetString(u, "url") is { Length: > 0 } s)
                        bases.Add(s);

            if (bases.Count == 0) throw new TencentException("播放接口没有返回服务器地址");

            var path = fn + "?vkey=" + fvkey;
            var all = new List<string>();
            foreach (var b in bases)
            {
                var u = b.EndsWith('/') ? b + path : b + "/" + path;
                if (!all.Contains(u)) all.Add(u);
            }

            // 实际档位：用 br 去 fl 里比对，比对不上就按文件名里的档位号估
            var height = MatchHeight(root, GetLong(vi, "br") ?? 0);

            return (all[0], all.Skip(1).ToArray(), height);
        }
    }

    /// <summary>把 vi.br 对回 fl.fi[] 里的分辨率。腾讯的 br 是档位码率序号，各档唯一。</summary>
    private static int MatchHeight(JsonElement root, long br)
    {
        if (br <= 0) return 0;
        if (Prop(root, "fl") is not { } fl || Prop(fl, "fi") is not { ValueKind: JsonValueKind.Array } fi)
            return 0;

        foreach (var f in fi.EnumerateArray())
            if ((GetLong(f, "br") ?? -1) == br)
            {
                var h = ParseHeight(GetString(f, "cname"));
                if (h <= 0) h = (int)(GetLong(f, "height") ?? 0);
                return h;
            }

        return 0;
    }

    /// <summary>从「高清SDR;(1080P)」里抠出 1080。</summary>
    private static int ParseHeight(string cname)
    {
        if (string.IsNullOrEmpty(cname)) return 0;
        var m = Regex.Match(cname, @"(\d{3,4})\s*P", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    // ---------- 文本输入 ----------

    /// <summary>cover/{cid}/{vid}.html、cover/{cid}.html、x/page/{vid}.html 三种都要认。</summary>
    private static readonly Regex CoverWithVidRx = new(
        @"v\.qq\.com/x/cover/([A-Za-z0-9]{8,})/([A-Za-z0-9]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CoverRx = new(
        @"v\.qq\.com/x/cover/([A-Za-z0-9]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PageRx = new(
        @"v\.qq\.com/x/(?:page|detail)/([A-Za-z0-9]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>裸 vid：11 位左右、字母数字混排。太短的容易误判，所以要求 8 位以上。</summary>
    private static readonly Regex BareVidRx = new(@"^\s*([A-Za-z0-9]{8,16})\s*$", RegexOptions.Compiled);

    /// <summary>从链接 / 分享文案里抠出专辑 ID 和视频 ID，任意一个都可能为空。</summary>
    public static (string Cid, string Vid) ExtractIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("", "");
        var s = text.Trim();

        var m = CoverWithVidRx.Match(s);
        if (m.Success) return (m.Groups[1].Value, m.Groups[2].Value);

        m = CoverRx.Match(s);
        if (m.Success) return (m.Groups[1].Value, "");

        m = PageRx.Match(s);
        if (m.Success) return ("", m.Groups[1].Value);

        m = BareVidRx.Match(s);
        if (m.Success) return ("", m.Groups[1].Value);

        return ("", "");
    }

    // ---------- 小工具 ----------

    /// <summary>
    /// getinfo 的响应体带 <c>QZOutputJson=</c> 前缀，尾部偶尔还有杂字符，
    /// 所以取第一个 '{' 到最后一个 '}' 之间的内容。
    /// </summary>
    public static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        if (start < 0) throw new TencentException("播放接口返回的内容里没有 JSON");

        var end = raw.LastIndexOf('}');
        if (end <= start) throw new TencentException("播放接口返回的 JSON 不完整");

        return raw.Substring(start, end - start + 1);
    }

    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : v;
    }

    private static string GetString(JsonElement e, string name)
    {
        var p = Prop(e, name);
        return p?.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "";
    }

    private static long? GetLong(JsonElement e, string name)
    {
        var p = Prop(e, name);
        return p?.ValueKind switch
        {
            JsonValueKind.Number when p.Value.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(p.Value.GetString(), out var n) => n,
            _ => null
        };
    }

    /// <summary>td（时长，秒）是浮点，175.168 这种。</summary>
    private static double? GetDouble(JsonElement e, string name)
    {
        var p = Prop(e, name);
        return p?.ValueKind switch
        {
            JsonValueKind.Number when p.Value.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(p.Value.GetString(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }
}
