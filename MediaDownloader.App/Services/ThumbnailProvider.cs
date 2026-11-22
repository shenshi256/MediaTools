using System.IO;
using System.Windows.Media.Imaging;
using MediaDownloader.Core;

namespace MediaDownloader.App.Services;

/// <summary>
/// 时间轴悬停缩略图：按时间点调用 ffmpeg 抽一帧，带 LRU 缓存。
/// 新请求会取消上一个未完成的抽帧，避免快速滑动时堆积大量进程。
/// </summary>
public sealed class ThumbnailProvider
{
    private const int MaxCached = 120;
    private const double BucketSeconds = 0.25;

    private readonly string _ffmpeg;
    private readonly Dictionary<string, BitmapSource> _cache = new();
    private readonly Queue<string> _order = new();
    private CancellationTokenSource? _pending;

    public ThumbnailProvider(string ffmpegPath) => _ffmpeg = ffmpegPath;

    public void Reset()
    {
        try { _pending?.Cancel(); } catch { /* 忽略 */ }
        _cache.Clear();
        _order.Clear();
    }

    public BitmapSource? TryGetCached(string file, TimeSpan t)
        => _cache.TryGetValue(Key(file, t), out var bmp) ? bmp : null;

    public async Task<BitmapSource?> GetAsync(string file, TimeSpan t, int width = 200)
    {
        if (!File.Exists(file)) return null;

        var key = Key(file, t);
        if (_cache.TryGetValue(key, out var hit)) return hit;

        _pending?.Cancel();
        _pending?.Dispose();
        var cts = new CancellationTokenSource();
        _pending = cts;

        byte[]? bytes;
        try
        {
            bytes = await FfmpegRunner.ExtractFrameAsync(_ffmpeg, file, t, width, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (bytes is null) return null;
        var bmp = Decode(bytes);
        if (bmp is null) return null;

        _cache[key] = bmp;
        _order.Enqueue(key);
        while (_order.Count > MaxCached)
        {
            var oldest = _order.Dequeue();
            _cache.Remove(oldest);
        }

        return bmp;
    }

    private static string Key(string file, TimeSpan t)
    {
        var bucket = Math.Round(t.TotalSeconds / BucketSeconds, MidpointRounding.AwayFromZero) * BucketSeconds;
        return $"{file}|{bucket:F2}";
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }
}
