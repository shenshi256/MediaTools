using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaDownloader.Core;

/// <summary>
/// 抖音 aweme_detail 响应解析。刻意做成纯静态、只吃字符串，
/// 这样不用 WebView2 也能跑测试（见 tools/MediaCli 的 [12] 段）。
/// </summary>
public static class DouyinParser
{
    /// <summary>解析 /aweme/v1/web/aweme/detail/ 的响应体。</summary>
    public static DouyinVideo Parse(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            throw new DouyinException("接口返回空内容");

        // 落盘再读回时可能带 BOM
        responseJson = responseJson.TrimStart('\uFEFF');

        JsonDocument doc;
        try { doc = JsonDocument.Parse(responseJson); }
        catch (JsonException ex) { throw new DouyinException("接口返回的不是合法 JSON：" + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;

            var status = GetLong(root, "status_code");
            if (status is not null and not 0)
                throw new DouyinException($"接口返回 status_code={status}，可能触发了风控");

            var detail = Prop(root, "aweme_detail");
            if (detail is null)
                throw new DouyinException("拿不到视频信息：可能已删除、设为私密，或该链接不是视频作品");

            var a = detail.Value;

            var video = Prop(a, "video");
            var images = Prop(a, "images");
            var isImagePost = images is not null &&
                              images.Value.ValueKind == JsonValueKind.Array &&
                              images.Value.GetArrayLength() > 0;

            var streams = isImagePost ? new List<DouyinStream>() : ExtractStreams(video);

            var title = FirstNonEmpty(
                GetString(a, "desc"),
                GetString(a, "item_title"),
                GetString(a, "preview_title"));

            var author = Prop(a, "author") is { } au ? GetString(au, "nickname") : "";

            var music = Prop(a, "music");
            var musicTitle = music is not null ? GetString(music.Value, "title") : "";

            var durationMs = video is not null ? GetLong(video.Value, "duration") : null;
            durationMs ??= GetLong(a, "duration") ?? 0;

            return new DouyinVideo
            {
                AwemeId = GetString(a, "aweme_id"),
                Title = title,
                Author = author,
                CoverUrl = CoverOf(video),
                MusicTitle = musicTitle,
                Duration = TimeSpan.FromMilliseconds(durationMs.Value),
                Width = (int)(video is not null ? GetLong(video.Value, "width") ?? 0 : 0),
                Height = (int)(video is not null ? GetLong(video.Value, "height") ?? 0 : 0),
                IsImagePost = isImagePost,
                Streams = streams
            };
        }
    }

    private static List<DouyinStream> ExtractStreams(JsonElement? video)
    {
        var result = new List<DouyinStream>();
        if (video is null) return result;

        var v = video.Value;

        // bit_rate 里是完整档位表；play_addr 只是"当前默认"那一路，两者会重复，所以优先用 bit_rate
        if (Prop(v, "bit_rate") is { ValueKind: JsonValueKind.Array } rates)
        {
            foreach (var b in rates.EnumerateArray())
            {
                var addr = Prop(b, "play_addr");
                var url = addr is null ? "" : FirstUrl(addr.Value);
                if (string.IsNullOrEmpty(url)) continue;

                result.Add(new DouyinStream
                {
                    GearName = GetString(b, "gear_name"),
                    QualityType = (int)(GetLong(b, "quality_type") ?? 0),
                    BitRate = GetLong(b, "bit_rate") ?? 0,
                    Url = url,
                    BackupUrls = Urls(addr!.Value),
                    Width = (int)(GetLong(addr.Value, "width") ?? 0),
                    Height = (int)(GetLong(addr.Value, "height") ?? 0),
                    DataSize = GetLong(addr.Value, "data_size") ?? 0
                });
            }
        }

        if (result.Count == 0 && Prop(v, "play_addr") is { } mainAddr)
        {
            var url = FirstUrl(mainAddr);
            if (!string.IsNullOrEmpty(url))
            {
                result.Add(new DouyinStream
                {
                    GearName = "play_addr",
                    Url = url,
                    BackupUrls = Urls(mainAddr),
                    Width = (int)(GetLong(mainAddr, "width") ?? 0),
                    Height = (int)(GetLong(mainAddr, "height") ?? 0),
                    DataSize = GetLong(mainAddr, "data_size") ?? 0
                });
            }
        }

        // 同一路地址可能在多个档位里重复出现
        var seen = new HashSet<string>(StringComparer.Ordinal);
        result = result.Where(x => seen.Add(x.Url)).ToList();

        // 清晰度优先（按短边，竖屏视频的高度是长边），其次码率
        return result
            .OrderByDescending(x => x.ShortSide)
            .ThenByDescending(x => x.BitRate)
            .ToList();
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string CoverOf(JsonElement? video)
    {
        if (video is null) return "";
        if (Prop(video.Value, "cover") is { } c && FirstUrl(c) is { Length: > 0 } u1) return u1;
        if (Prop(video.Value, "origin_cover") is { } oc && FirstUrl(oc) is { Length: > 0 } u2) return u2;
        return "";
    }

    private static string FirstUrl(JsonElement addr)
    {
        var urls = Urls(addr);
        return urls.Length > 0 ? urls[0] : "";
    }

    private static string[] Urls(JsonElement addr)
    {
        if (addr.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        if (!addr.TryGetProperty("url_list", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return list.EnumerateArray()
            .Select(x => x.GetString() ?? "")
            .Where(x => x.Length > 0)
            .ToArray();
    }

    // ---------- 文本输入 ----------

    private static readonly Regex ShortUrlRx =
        new(@"https?://v\.douyin\.com/[A-Za-z0-9_\-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PathRx =
        new(@"(?:video|note|slides|share/video)/(\d{15,20})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareIdRx = new(@"\b(\d{15,20})\b", RegexOptions.Compiled);

    /// <summary>从任意文本里抠出作品 ID。抠不到再让调用方去展开短链。</summary>
    public static string? TryExtractAwemeId(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();

        // 路径里的 ID 最可靠，先看这个（短链本身不含 ID）
        var m = PathRx.Match(s);
        if (m.Success) return m.Groups[1].Value;

        m = BareIdRx.Match(s);
        if (m.Success) return m.Groups[1].Value;

        return null;
    }

    /// <summary>提取 v.douyin.com 短链。分享口令里常混着中文，所以只取链接那一段。</summary>
    public static string? TryExtractShortUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = ShortUrlRx.Match(text);
        return m.Success ? m.Value : null;
    }

    /// <summary>是否是图文作品链接（/note/）。</summary>
    public static bool IsNoteUrl(string text)
        => text.Contains("/note/", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("/slides/", StringComparison.OrdinalIgnoreCase);

    // ---------- JSON 小工具 ----------

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
}
