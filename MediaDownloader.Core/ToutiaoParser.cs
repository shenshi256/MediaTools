using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaDownloader.Core;

/// <summary>
/// 今日头条解析。和 <see cref="DouyinParser"/> 一样刻意做成纯静态、只吃字符串，
/// 这样不用网络也能跑测试（见 tools/MediaCli 的头条段）。
///
/// 头条有两条数据来源，都要在这里处理：
///   1. m.toutiao.com/i{groupId}/info/  —— 元数据（标题 / 作者 / 封面 / 时长 / 播放 token）
///   2. vod.bytedanceapi.com 的 GetPlayInfo —— 清晰度档位和真实播放地址
/// 第 2 步的鉴权参数整个塞在 token 里，所以这个文件还要负责拆 token。
/// </summary>
public static class ToutiaoParser
{
    /// <summary>GetPlayInfo 的落地主机，token 直接拼在查询串上。</summary>
    public const string PlayInfoEndpoint = "https://vod.bytedanceapi.com/";

    // ---------- 第一段：info 响应 ----------

    /// <summary>解析 m.toutiao.com/i{groupId}/info/ 的响应体。Streams 留空，等 PlayInfo 再补。</summary>
    public static ToutiaoVideo ParseInfo(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            throw new ToutiaoException("接口返回空内容");

        responseJson = responseJson.TrimStart('\uFEFF');

        JsonDocument doc;
        try { doc = JsonDocument.Parse(responseJson); }
        catch (JsonException ex) { throw new ToutiaoException("接口返回的不是合法 JSON：" + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;

            // success 为 false 时，data 里通常也没有可用字段
            if (Prop(root, "success") is { ValueKind: JsonValueKind.False })
                throw new ToutiaoException("接口返回 success=false，可能已删除或触发风控");

            var data = Prop(root, "data")
                ?? throw new ToutiaoException("拿不到视频信息：可能已删除、设为私密，或该链接不是视频");

            // content 里内嵌着同样的 token，接口字段偶尔为空，所以两条路都认
            var content = GetString(data, "content");
            var videoId = FirstNonEmpty(
                GetString(data, "video_id"),
                MatchAttr(content, "tt-videoid"));

            if (string.IsNullOrEmpty(videoId))
                throw new ToutiaoException("该链接不是视频内容（图文 / 微头条暂不支持）");

            var token = FirstNonEmpty(
                GetString(data, "play_auth_token_v2"),
                MatchAttr(content, "data-token"));

            if (string.IsNullOrEmpty(token))
                throw new ToutiaoException("拿不到播放凭证：可能视频已下架，或需要在软件里扫码登录后再试");

            var durationSec = GetDouble(data, "video_duration") ?? 0;

            return new ToutiaoVideo
            {
                GroupId = GetString(data, "gid"),
                VideoId = videoId,
                Title = GetString(data, "title"),
                Author = Prop(data, "media_user") is { } u ? GetString(u, "screen_name") : "",
                CoverUrl = GetString(data, "poster_url"),
                Duration = TimeSpan.FromSeconds(durationSec),
                PublishTime = GetLong(data, "publish_time") is { } ts and > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(ts)
                    : null,
                PlayToken = token
            };
        }
    }

    // ---------- 第二段：GetPlayInfo 响应 ----------

    /// <summary>解析 GetPlayInfo 的响应体，返回按清晰度从高到低排好的档位表。</summary>
    public static IReadOnlyList<ToutiaoStream> ParsePlayInfo(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            throw new ToutiaoException("播放地址接口返回空内容");

        responseJson = responseJson.TrimStart('\uFEFF');

        JsonDocument doc;
        try { doc = JsonDocument.Parse(responseJson); }
        catch (JsonException ex) { throw new ToutiaoException("播放地址接口返回的不是合法 JSON：" + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;
            var result = Prop(root, "Result")
                ?? throw new ToutiaoException("播放地址接口返回结构异常（缺 Result）");
            var data = Prop(result, "Data")
                ?? throw new ToutiaoException("播放地址接口返回结构异常（缺 Data）");

            var status = GetLong(data, "Status");
            if (status is not null and not 10)
                throw new ToutiaoException($"播放地址接口返回 Status={status}，视频可能已下架或受限");

            var list = new List<ToutiaoStream>();
            if (Prop(data, "PlayInfoList") is { ValueKind: JsonValueKind.Array } arr)
            {
                foreach (var p in arr.EnumerateArray())
                {
                    var url = GetString(p, "MainPlayUrl");
                    if (string.IsNullOrEmpty(url)) continue;

                    var backup = GetString(p, "BackupPlayUrl");

                    list.Add(new ToutiaoStream
                    {
                        Definition = GetString(p, "Definition"),
                        Width = (int)(GetLong(p, "Width") ?? 0),
                        Height = (int)(GetLong(p, "Height") ?? 0),
                        BitRate = GetLong(p, "Bitrate") ?? 0,
                        DataSize = GetLong(p, "Size") ?? 0,
                        Codec = GetString(p, "Codec"),
                        Url = url,
                        BackupUrls = string.IsNullOrEmpty(backup) ? Array.Empty<string>() : new[] { backup }
                    });
                }
            }

            if (list.Count == 0)
                throw new ToutiaoException("播放地址接口没有返回可用的清晰度");

            // 同一路地址可能在多个档位里重复出现
            var seen = new HashSet<string>(StringComparer.Ordinal);
            list = list.Where(x => seen.Add(x.Url)).ToList();

            // 清晰度优先（按短边，竖屏视频的高度是长边），其次码率
            return list
                .OrderByDescending(x => x.ShortSide)
                .ThenByDescending(x => x.BitRate)
                .ToList();
        }
    }

    /// <summary>把两次请求的结果合起来。</summary>
    public static ToutiaoVideo Combine(ToutiaoVideo info, IReadOnlyList<ToutiaoStream> streams)
        => info with { Streams = streams };

    // ---------- token ----------

    /// <summary>
    /// token 是 base64 过的一层壳，剥开后是 {"GetPlayInfoToken":"Action=GetPlayInfo&amp;...","Version":"v1"}。
    /// 里面那串查询参数本身就是签好名的，原样拼到 <see cref="PlayInfoEndpoint"/> 后面即可。
    /// </summary>
    public static string DecodePlayToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ToutiaoException("播放凭证为空");

        var t = token.Trim();
        // base64url 变体也一起兼容
        t = t.Replace('-', '+').Replace('_', '/');
        var pad = t.Length % 4;
        if (pad > 0) t = t.PadRight(t.Length + (4 - pad), '=');

        string inner;
        try { inner = Encoding.UTF8.GetString(Convert.FromBase64String(t)); }
        catch (FormatException) { throw new ToutiaoException("播放凭证不是合法的 base64"); }

        try
        {
            using var doc = JsonDocument.Parse(inner.TrimStart('\uFEFF'));
            var q = GetString(doc.RootElement, "GetPlayInfoToken");
            // 少数情况直接就是查询串
            if (string.IsNullOrEmpty(q) && inner.Contains("Action=GetPlayInfo", StringComparison.Ordinal))
                q = inner.Trim();

            if (string.IsNullOrEmpty(q))
                throw new ToutiaoException("播放凭证里没有 GetPlayInfo 参数");

            // JSON 里 & 被转义成 \u0026，解析后会还原；再兜一层以防上游又转义了一次
            return q.Replace("\\u0026", "&", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            if (inner.Contains("Action=GetPlayInfo", StringComparison.Ordinal))
                return inner.Trim();
            throw new ToutiaoException("播放凭证结构异常");
        }
    }

    /// <summary>拼出可以直接 GET 的播放地址接口 URL。</summary>
    public static string BuildPlayInfoUrl(string token)
        => PlayInfoEndpoint + "?" + DecodePlayToken(token);

    // ---------- 文本输入 ----------

    private static readonly Regex ShortUrlRx =
        new(@"https?://(?:m|www)\.toutiao\.com/is/[A-Za-z0-9_\-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// video/7351230474867442216/ 、article/同名 / item/ 一样；
    /// 移动版是粘连写法 m.toutiao.com/i7351230474867442216/ ，所以 i 单独一条分支。
    /// </summary>
    private static readonly Regex PathRx = new(
        @"(?:/(?:video|article|item|w)/|/i)(\d{15,20})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareIdRx = new(@"\b(\d{15,20})\b", RegexOptions.Compiled);

    /// <summary>从任意文本里抠出条目 ID。抠不到再让调用方去展开短链。</summary>
    public static string? TryExtractGroupId(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();

        var m = PathRx.Match(s);
        if (m.Success) return m.Groups[1].Value;

        m = BareIdRx.Match(s);
        if (m.Success) return m.Groups[1].Value;

        return null;
    }

    /// <summary>提取 m.toutiao.com/is/xxx 短链。分享文案里常混着中文，只取链接那一段。</summary>
    public static string? TryExtractShortUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = ShortUrlRx.Match(text);
        return m.Success ? m.Value : null;
    }

    // ---------- 小工具 ----------

    /// <summary>从 content 的 HTML 片段里取 tt-videoid='...' / data-token='...' 这类属性。</summary>
    private static string MatchAttr(string html, string attr)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var m = Regex.Match(html, attr + @"='([^']+)'", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

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

    /// <summary>video_duration 可能是 61 也可能是 61.5，统一按 double 收。</summary>
    private static double? GetDouble(JsonElement e, string name)
    {
        var p = Prop(e, name);
        return p?.ValueKind switch
        {
            JsonValueKind.Number when p.Value.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(p.Value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }
}
