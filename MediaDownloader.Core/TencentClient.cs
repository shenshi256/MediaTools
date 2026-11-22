using System.Net;
using System.Text.RegularExpressions;

namespace MediaDownloader.Core;

/// <summary>
/// 腾讯视频的 HTTP 部分，纯直连，不需要内嵌浏览器：
///   1. float_vinfo2 拿专辑元数据和 video_ids（一次请求）；
///   2. getinfo 逐集拿清晰度和直链（每集一次，并发拉）。
/// 未登录就能拿到免费内容的全部清晰度，所以这个 Client 没有登录态概念。
/// </summary>
public sealed class TencentClient : IDisposable
{
    /// <summary>桌面 UA。接口按网页端 platform=101001 设计，用桌面 UA 最贴近。</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    public const string Referer = TencentParser.Referer;

    /// <summary>逐集并发上限。腾讯这边单次响应很快，6 路足够，再高容易触发限流。</summary>
    private const int MaxConcurrency = 6;

    private readonly HttpClient _http;

    public TencentClient()
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
    /// 一步到位：链接 → 专辑 + 每集清晰度。
    /// <paramref name="onProgress"/> 会报「已解析 x/y 集」，界面可以显示进度。
    /// </summary>
    public async Task<TencentVideo> FetchAsync(
        string input,
        IProgress<(int Done, int Total)>? onProgress = null,
        CancellationToken ct = default)
    {
        var (cid, vid) = TencentParser.ExtractIds(input);
        if (cid.Length == 0 && vid.Length == 0)
            throw new TencentException("看不懂这个链接。支持 v.qq.com/x/cover/… 和 v.qq.com/x/page/…");

        TencentVideo album;

        if (cid.Length > 0)
        {
            album = await FetchAlbumAsync(cid, ct);
        }
        else
        {
            // 只有 vid：先从 getinfo 反查 cid，能查到就按专辑走，查不到就当单集
            var probe = await FetchEpisodeAsync(vid, ct);
            var foundCid = await TryGetCidAsync(vid, ct);
            if (foundCid.Length > 0)
            {
                album = await FetchAlbumAsync(foundCid, ct);
            }
            else
            {
                album = new TencentVideo
                {
                    Cid = "",
                    Title = probe.Title,
                    Episodes = new[] { TencentParser.Merge(new TencentEpisode { Vid = vid }, probe) }
                };
                return album;
            }
        }

        // 逐集补清晰度
        var episodes = album.Episodes.ToList();
        var total = episodes.Count;
        var done = 0;

        using var gate = new SemaphoreSlim(MaxConcurrency);
        var jobs = new List<Task>(total);

        for (var i = 0; i < total; i++)
        {
            var index = i;
            jobs.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var detail = await FetchEpisodeAsync(episodes[index].Vid, ct);
                    lock (episodes) episodes[index] = TencentParser.Merge(episodes[index], detail);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 单集失败不拖垮整页
                    lock (episodes)
                        episodes[index] = TencentParser.Merge(episodes[index], new TencentEpisode
                        {
                            Vid = episodes[index].Vid,
                            CanDownload = false,
                            BlockReason = "解析失败：" + ex.Message
                        });
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref done);
                    onProgress?.Report((n, total));
                }
            }, ct));
        }

        await Task.WhenAll(jobs);

        return album with { Episodes = Sort(episodes) };
    }

    private async Task<TencentVideo> FetchAlbumAsync(string cid, CancellationToken ct)
    {
        var body = await GetStringAsync(TencentParser.BuildAlbumUrl(cid), ct);
        return TencentParser.ParseAlbum(body, cid);
    }

    /// <summary>单集播放信息。下载前如果 vkey 过期，也可以单独调它刷新。</summary>
    public async Task<TencentEpisode> FetchEpisodeAsync(string vid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vid))
            throw new TencentException("视频 ID 为空");

        // 要最高的那档：未登录时腾讯会自动降级，正好顺带问出真实上限
        var body = await GetStringAsync(TencentParser.BuildGetInfoUrl(vid, "fhd"), ct);
        return TencentParser.ParsePlayInfo(body, vid);
    }

    /// <summary>
    /// 按指定档位取真实直链。**必须在下载前调**：
    /// 一份 getinfo 只给一个档位的文件名，而且 vkey 有有效期（实测约数小时）。
    /// 返回的 Height 是这次实际给的分辨率——请求 1080P 但没登录时，腾讯会给 720P。
    /// </summary>
    public async Task<TencentQuality> FetchStreamAsync(string vid, TencentQuality quality, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vid))
            throw new TencentException("视频 ID 为空");

        var defn = string.IsNullOrWhiteSpace(quality.Defn) ? "fhd" : quality.Defn;
        var body = await GetStringAsync(TencentParser.BuildGetInfoUrl(vid, defn), ct);
        var (url, backups, actualHeight) = TencentParser.ParseStreamUrl(body);

        var q = quality.WithUrl(url, backups);
        if (actualHeight <= 0 || actualHeight == quality.Height) return q;

        // 服务器降档了：把显示名改成真实的，避免"显示 1080P 下来是 720P"
        return new TencentQuality
        {
            Name = quality.Name,
            Defn = quality.Defn,
            Height = actualHeight,
            SizeBytes = quality.SizeBytes,
            Url = url,
            BackupUrls = backups,
            IsDrm = quality.IsDrm
        };
    }

    /// <summary>只有 vid 时用于反查所属专辑。</summary>
    private async Task<string> TryGetCidAsync(string vid, CancellationToken ct)
    {
        try
        {
            var body = await GetStringAsync(TencentParser.BuildGetInfoUrl(vid), ct);
            var json = TencentParser.ExtractJsonObject(body);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("vl", out var vl) &&
                vl.TryGetProperty("vi", out var vi) &&
                vi.ValueKind == System.Text.Json.JsonValueKind.Array &&
                vi.GetArrayLength() > 0)
            {
                var first = vi[0];
                if (first.TryGetProperty("cid", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                    return c.GetString() ?? "";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("tencent", "反查 cid 失败：" + ex.Message);
        }
        return "";
    }

    /// <summary>
    /// 按标题里的「第 N 集」排序。接口给的 video_ids 顺序是乱的，
    /// 全都能抠出集数才排，否则保持原顺序（信息不全时排序反而更错）。
    /// </summary>
    private static List<TencentEpisode> Sort(List<TencentEpisode> episodes)
    {
        var rx = new Regex(@"第\s*(\d+)\s*集");
        var allParsed = true;

        for (var i = 0; i < episodes.Count; i++)
        {
            var m = rx.Match(episodes[i].Title ?? "");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            {
                episodes[i].Number = n;
            }
            else
            {
                allParsed = false;
                break;
            }
        }

        return allParsed
            ? episodes.OrderBy(e => e.Number).ToList()
            : episodes;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri(Referer);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if ((int)resp.StatusCode != 200)
                throw new TencentException($"请求失败：HTTP {(int)resp.StatusCode}");

            // 不能直接 ReadAsStringAsync：腾讯有些接口的 Content-Type 里 charset 是非法值，
            // 那个方法会抛 "character set provided in ContentType is invalid"。这里统一按 UTF-8 解。
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (TencentException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new TencentException("已取消"); }
        catch (TaskCanceledException) { throw new TencentException("请求超时，请检查网络"); }
        catch (Exception ex) { throw new TencentException("请求失败：" + ex.Message); }
    }
}
