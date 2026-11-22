using System.Diagnostics;
using System.Text;

namespace MediaDownloader.Core;

public sealed class FfmpegResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = "";
}

/// <summary>调用 ffmpeg 做 DASH 音视频合并（-c copy，不重编码）。</summary>
public static class MediaMerger
{
    /// <summary>在 PATH 或常见位置查找 ffmpeg。</summary>
    public static string? FindFfmpeg(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;

        // 其次：程序所在目录下的 ffmpeg（随包分发时）
        var exe = "ffmpeg.exe";
        try
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, exe),
                         Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", exe),
                         Path.Combine(AppContext.BaseDirectory, "tool", exe),
                         Path.Combine(AppContext.BaseDirectory, "ffmpeg", exe)
                     })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* 忽略 */ }
        var dir = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in dir.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(p.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 忽略非法 PATH 项 */ }
        }
        return null;
    }

    /// <summary>合并视频与音频流；若只有单流则直接移动文件。</summary>
    public static async Task<FfmpegResult> MergeAsync(string ffmpegPath, string videoPath, string? audioPath,
        string outputPath, IProgress<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath)) File.Delete(outputPath);

        if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
        {
            File.Move(videoPath, outputPath, overwrite: true);
            return new FfmpegResult { Success = true, Output = "单流，无需合并" };
        }

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[]
                 {
                     "-y", "-loglevel", "warning",
                     "-i", videoPath, "-i", audioPath,
                     "-map", "0:v:0", "-map", "1:a:0",
                     "-c", "copy", "-movflags", "+faststart",
                     outputPath
                 })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { sb.AppendLine(e.Data); log?.Report(e.Data); } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { sb.AppendLine(e.Data); log?.Report(e.Data); } };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);

        var ok = proc.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        return new FfmpegResult { Success = ok, Output = sb.ToString() };
    }
}
