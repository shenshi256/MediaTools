using System.Text.Json;
using MediaDownloader.Core;

// 冒烟测试：验证解析 → 取流 → 分段下载（含 Referer/Range）整条链路。
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
using var client = new BilibiliClient();

Console.WriteLine("[1] 取一个真实视频（B站热门榜）");
string bvid;
try
{
    var doc = await client.GetJsonAsync("https://api.bilibili.com/x/web-interface/popular?ps=3&pn=1",
        "https://www.bilibili.com/", cts.Token);
    bvid = doc.RootElement.GetProperty("data").GetProperty("list")
        .EnumerateArray().First().GetProperty("bvid").GetString()!;
}
catch (Exception ex)
{
    Console.WriteLine("    热门接口失败，回退排行榜：" + ex.Message);
    var doc = await client.GetJsonAsync("https://api.bilibili.com/x/web-interface/ranking/v2?rid=0&type=all",
        "https://www.bilibili.com/", cts.Token);
    bvid = doc.RootElement.GetProperty("data").GetProperty("list")
        .EnumerateArray().First().GetProperty("bvid").GetString()!;
}
Console.WriteLine("    bvid = " + bvid);

Console.WriteLine("[2] 解析视频信息（view + WBI 签名）");
var parsed = BilibiliClient.Parse($"https://www.bilibili.com/video/{bvid}");
var meta = await client.GetVideoInfoAsync(parsed!, cts.Token);
Console.WriteLine($"    标题 = {meta.Title}");
Console.WriteLine($"    UP   = {meta.UpName}");
Console.WriteLine($"    分P  = {meta.Pages.Count}，首个 cid = {meta.Pages[0].Cid}");

Console.WriteLine("[3] 取清晰度列表（playurl + WBI）");
var qualities = await client.GetQualityOptionsAsync(meta.Bvid, meta.Pages[0].Cid, cts.Token);
foreach (var q in qualities.Take(8))
    Console.WriteLine($"    {q.DisplayName}  dash={(q.IsDash ? "是" : "否")}  url={(q.VideoUrl.Length > 0 ? "有" : "无")}");
if (qualities.Count == 0) { Console.WriteLine("    没有可用清晰度，终止"); return 1; }

Console.WriteLine("[4] 解析最低档实际地址");
var lowest = qualities.OrderBy(x => x.Id).First();
var stream = await client.ResolveStreamAsync(meta.Bvid, meta.Pages[0].Cid, lowest.Id, cts.Token);
if (stream is null || string.IsNullOrEmpty(stream.VideoUrl)) { Console.WriteLine("    取地址失败"); return 1; }
Console.WriteLine($"    档位 = {stream.DisplayName}");
Console.WriteLine($"    视频流 = {stream.VideoUrl[..Math.Min(90, stream.VideoUrl.Length)]}…");
Console.WriteLine($"    音频流 = {(stream.AudioUrl is null ? "无" : "有")}");

Console.WriteLine("[5] 分段下载 3 秒后暂停，验证 Range/Referer/续传文件");
var limiter = new SpeedLimiter { BytesPerSecond = 300 * 1024 };
using var engine = new DownloadEngine(limiter);
var tmp = Path.Combine(Path.GetTempPath(), "jjprobe");
Directory.CreateDirectory(tmp);
var dest = Path.Combine(tmp, "probe.m4s");

var progress = new Progress<DownloadProgress>(p =>
    Console.Write($"\r    已下载 {p.ReceivedBytes / 1024} KB / {p.TotalBytes / 1024} KB  {p.SpeedText}      "));

var download = engine.DownloadAsync(new DownloadRequest
{
    Url = stream.VideoUrl,
    BackupUrls = stream.VideoBackupUrls,
    DestinationPath = dest,
    Referer = BilibiliClient.RefererOf(meta.Bvid),
    Segments = 4
}, progress, cts.Token);

await Task.Delay(3000);
cts.Cancel();
try { await download; } catch (OperationCanceledException) { }

Console.WriteLine();
var size = File.Exists(dest) ? new FileInfo(dest).Length : 0;
Console.WriteLine($"    已写入 {size / 1024} KB");
Console.WriteLine($"    续传进度文件存在 = {File.Exists(dest + ".jjprog")}");

var ok = size > 0 && File.Exists(dest + ".jjprog");

Console.WriteLine("[6] 扫码登录接口（RequestQrAsync）");
using (var svc = new LoginService())
{
    var session = await svc.RequestQrAsync();
    Console.WriteLine("    auth_code = " + session.AuthCode[..Math.Min(8, session.AuthCode.Length)] + "…");
    Console.WriteLine("    二维码 URL = " + (session.Url.Contains("passport-tv-login") ? "有效" : "异常: " + session.Url));
    ok = ok && session.AuthCode.Length > 0;
}

Console.WriteLine("[7] ffmpeg 探测");
var ffmpeg = MediaMerger.FindFfmpeg(args.Length > 0 ? args[0] : null);
Console.WriteLine("    ffmpeg = " + (ffmpeg ?? "未找到（合并功能不可用）"));

if (ffmpeg is not null)
{
    Console.WriteLine("[8] 完整下载 + ffmpeg 合并（端到端）");
    using var cts2 = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    using var engine2 = new DownloadEngine(new SpeedLimiter());
    var work = Path.Combine(tmp, "e2e");
    Directory.CreateDirectory(work);
    var vp = Path.Combine(work, "v.m4s");
    var ap = Path.Combine(work, "a.m4s");
    var outp = Path.Combine(work, "out.mp4");

    var pv2 = new Progress<DownloadProgress>(p => Console.Write($"\r    视频 {p.ReceivedBytes / 1024}/{p.TotalBytes / 1024} KB  {p.SpeedText}      "));
    var pa2 = new Progress<DownloadProgress>(p => Console.Write($"\r    音频 {p.ReceivedBytes / 1024}/{p.TotalBytes / 1024} KB  {p.SpeedText}      "));

    var t1 = engine2.DownloadAsync(new DownloadRequest
    {
        Url = stream.VideoUrl, BackupUrls = stream.VideoBackupUrls, DestinationPath = vp,
        Referer = BilibiliClient.RefererOf(meta.Bvid), Segments = 4
    }, pv2, cts2.Token);

    var t2 = stream.AudioUrl is null ? Task.FromResult(0L) : engine2.DownloadAsync(new DownloadRequest
    {
        Url = stream.AudioUrl, BackupUrls = stream.AudioBackupUrls, DestinationPath = ap,
        Referer = BilibiliClient.RefererOf(meta.Bvid), Segments = 2
    }, pa2, cts2.Token);

    await Task.WhenAll(t1, t2);
    Console.WriteLine($"\n    视频 {new FileInfo(vp).Length / 1024} KB，音频 {(File.Exists(ap) ? new FileInfo(ap).Length / 1024 : 0)} KB");

    var merged = await MediaMerger.MergeAsync(ffmpeg, vp, File.Exists(ap) ? ap : null, outp);
    Console.WriteLine("    合并成功 = " + merged.Success);
    if (merged.Success) Console.WriteLine("    输出 " + outp + "  大小 " + new FileInfo(outp).Length / 1024 + " KB");
    else Console.WriteLine("    ffmpeg 输出：" + merged.Output);

    ok = ok && merged.Success;
}
else
{
    Console.WriteLine("    跳过端到端合并测试");
}

Console.WriteLine(ok ? "\n冒烟测试通过" : "\n冒烟测试未通过");
return ok ? 0 : 1;
