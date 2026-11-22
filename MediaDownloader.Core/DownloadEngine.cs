using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MediaDownloader.Core;

public sealed class DownloadRequest
{
    public string Url { get; init; } = "";
    public string[] BackupUrls { get; init; } = Array.Empty<string>();
    public string DestinationPath { get; init; } = "";
    public string Referer { get; init; } = "";
    public int Segments { get; init; } = 8;
}

/// <summary>全局令牌桶限速器（0 表示不限速）。</summary>
public sealed class SpeedLimiter
{
    private long _bytesPerSecond;
    private long _tokens;
    private long _lastTick = Environment.TickCount64;
    private readonly object _sync = new();

    public long BytesPerSecond { get => _bytesPerSecond; set { lock (_sync) { _bytesPerSecond = value; _tokens = 0; } } }

    public async Task ConsumeAsync(int count, CancellationToken ct)
    {
        long bps;
        lock (_sync) bps = _bytesPerSecond;
        if (bps <= 0) return;

        while (true)
        {
            int waitMs;
            lock (_sync)
            {
                var now = Environment.TickCount64;
                var elapsed = now - _lastTick;
                _lastTick = now;
                var capacity = Math.Max(bps / 4, 64 * 1024);
                _tokens = Math.Min(capacity, _tokens + elapsed * bps / 1000);
                if (_tokens >= count) { _tokens -= count; return; }
                var need = count - _tokens;
                waitMs = (int)Math.Clamp(need * 1000 / bps + 1, 1, 500);
            }
            await Task.Delay(waitMs, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>多线程分段下载，支持断点续传、限速与暂停。</summary>
public sealed class DownloadEngine : IDisposable
{
    private readonly HttpClient _http;
    private readonly SpeedLimiter _limiter;

    public DownloadEngine(SpeedLimiter limiter)
    {
        _limiter = limiter;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 32
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Add("User-Agent", BilibiliClient.UserAgent);
    }

    public void Dispose() => _http.Dispose();

    public async Task<long> DownloadAsync(DownloadRequest req, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var dest = req.DestinationPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        var probe = await ProbeAsync(req, ct);
        if (probe.TotalSize <= 0 || !probe.SupportsRange)
            return await DownloadSingleAsync(req, probe.TotalSize, progress, ct);

        var plan = Plan.Load(dest, probe.TotalSize, req.Segments);
        using (var init = new FileStream(dest, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            if (init.Length != probe.TotalSize) init.SetLength(probe.TotalSize);
        }

        long received = plan.Segments.Sum(s => s.Done);
        var sw = Stopwatch.StartNew();
        long lastReport = 0;

        void Report()
        {
            var now = sw.ElapsedMilliseconds;
            if (now - lastReport < 200 && received < probe.TotalSize) return;
            lastReport = now;
            var speed = sw.Elapsed.TotalSeconds > 0 ? received / sw.Elapsed.TotalSeconds : 0;
            progress?.Report(new DownloadProgress
            {
                ReceivedBytes = received,
                TotalBytes = probe.TotalSize,
                BytesPerSecond = speed,
                State = DownloadState.Running
            });
        }

        try
        {
            var tasks = plan.Segments
                .Where(s => s.Done < s.Length)
                .Select(async seg =>
                {
                    await DownloadSegmentAsync(req, probe, dest, seg, n =>
                    {
                        Interlocked.Add(ref received, n);
                        Report();
                    }, ct).ConfigureAwait(false);
                });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            plan.Save(dest);
            throw;
        }
        catch
        {
            plan.Save(dest);
            throw;
        }

        sw.Stop();
        progress?.Report(new DownloadProgress
        {
            ReceivedBytes = probe.TotalSize,
            TotalBytes = probe.TotalSize,
            State = DownloadState.Running
        });
        Plan.Delete(dest);
        return probe.TotalSize;
    }

    private async Task DownloadSegmentAsync(DownloadRequest req, Probe probe, string dest, Segment seg,
        Action<int> onBytes, CancellationToken ct)
    {
        using var fs = new FileStream(dest, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        fs.Seek(seg.Start + seg.Done, SeekOrigin.Begin);

        var buffer = new byte[128 * 1024];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var start = seg.Start + seg.Done;
                using var request = new HttpRequestMessage(HttpMethod.Get, probe.Url);
                request.Headers.Range = new RangeHeaderValue(start, seg.End);
                if (!string.IsNullOrEmpty(req.Referer)) request.Headers.Add("Referer", req.Referer);

                using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (seg.Done + read > seg.Length) read = (int)(seg.Length - seg.Done);
                    if (read <= 0) break;

                    await _limiter.ConsumeAsync(read, ct).ConfigureAwait(false);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    seg.Done += read;
                    onBytes(read);

                    if (seg.Done >= seg.Length) break;
                }

                if (seg.Done >= seg.Length) return;
            }
            catch (OperationCanceledException) { throw; }
            catch when (attempt < 2)
            {
                await Task.Delay(500 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
        throw new IOException($"分段 {seg.Start}-{seg.End} 下载失败");
    }

    private async Task<long> DownloadSingleAsync(DownloadRequest req, long knownSize,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var dest = req.DestinationPath;
        using var request = new HttpRequestMessage(HttpMethod.Get, req.Url);
        if (!string.IsNullOrEmpty(req.Referer)) request.Headers.Add("Referer", req.Referer);
        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = knownSize > 0 ? knownSize : resp.Content.Headers.ContentLength ?? 0;
        await using var net = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read);

        var buffer = new byte[128 * 1024];
        long received = 0;
        var sw = Stopwatch.StartNew();
        long lastReport = 0;

        int read;
        while ((read = await net.ReadAsync(buffer, ct)) > 0)
        {
            await _limiter.ConsumeAsync(read, ct);
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;

            var now = sw.ElapsedMilliseconds;
            if (now - lastReport >= 200 || received == total)
            {
                lastReport = now;
                progress?.Report(new DownloadProgress
                {
                    ReceivedBytes = received,
                    TotalBytes = total,
                    BytesPerSecond = sw.Elapsed.TotalSeconds > 0 ? received / sw.Elapsed.TotalSeconds : 0,
                    State = DownloadState.Running
                });
            }
        }
        return received;
    }

    private sealed record Probe(string Url, long TotalSize, bool SupportsRange);

    private async Task<Probe> ProbeAsync(DownloadRequest req, CancellationToken ct)
    {
        var candidates = new[] { req.Url }.Concat(req.BackupUrls).Where(u => !string.IsNullOrEmpty(u));
        foreach (var url in candidates)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(0, 1);
                if (!string.IsNullOrEmpty(req.Referer)) request.Headers.Add("Referer", req.Referer);
                using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (resp.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = resp.Content.Headers.ContentRange;
                    if (range?.Length is { } len)
                        return new Probe(url, len, true);
                }
                if (resp.IsSuccessStatusCode)
                    return new Probe(url, resp.Content.Headers.ContentLength ?? 0, false);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 换下一个备用地址 */ }
        }
        return new Probe(req.Url, 0, false);
    }

    // ---------- 续传计划 ----------

    private sealed class Segment
    {
        public long Start { get; set; }
        public long End { get; set; }
        public long Done { get; set; }
        public long Length => End - Start + 1;
    }

    private sealed class Plan
    {
        public long Total { get; set; }
        public List<Segment> Segments { get; set; } = new();

        private static string PathOf(string dest) => dest + ".jjprog";

        public static Plan Load(string dest, long total, int segmentCount)
        {
            var file = PathOf(dest);
            if (File.Exists(file))
            {
                try
                {
                    var saved = JsonSerializer.Deserialize<Plan>(File.ReadAllText(file));
                    if (saved is not null && saved.Total == total && saved.Segments.Count > 0)
                        return saved;
                }
                catch { /* 进度文件损坏则重新下载 */ }
                TryDelete(file);
            }

            var plan = new Plan { Total = total };
            int n = Math.Max(1, Math.Min(segmentCount, (int)Math.Ceiling(total / (double)(2 * 1024 * 1024))));
            var size = (long)Math.Ceiling(total / (double)n);
            for (int i = 0; i < n; i++)
            {
                var start = i * size;
                var end = Math.Min(start + size - 1, total - 1);
                if (start > end) break;
                plan.Segments.Add(new Segment { Start = start, End = end });
            }
            return plan;
        }

        public void Save(string dest)
        {
            try { File.WriteAllText(PathOf(dest), JsonSerializer.Serialize(this)); } catch { /* 忽略 */ }
        }

        public static void Delete(string dest) => TryDelete(PathOf(dest));

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
        }
    }
}
