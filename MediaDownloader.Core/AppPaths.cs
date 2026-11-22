using System.Text.Json;

namespace MediaDownloader.Core;

public static class AppPaths
{
    /// <summary>exe 所在目录（单文件发布时也指向 exe 目录）。</summary>
    public static string ExeDir { get; } =
        (AppContext.BaseDirectory ?? Directory.GetCurrentDirectory())
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>默认下载目录：exe 目录下的 down。</summary>
    public static string DefaultDownloadDir => Path.Combine(ExeDir, "down");

    /// <summary>默认 ffmpeg：exe 目录下的 tools\ffmpeg\ffmpeg.exe。</summary>
    public static string DefaultFfmpegPath => Path.Combine(ExeDir, "tools", "ffmpeg", "ffmpeg.exe");

    public static string DataDir { get; } = InitDataDir();

    /// <summary>
    /// 数据目录。项目从 JJDownloader 改名 MediaDownloader 时目录也跟着换，
    /// 这里负责把旧目录里的设置 / B站凭证 / 抖音和头条的登录态整体搬过来（只搬一次，
    /// 用复制而不是移动，旧目录留着不动，万一要回滚旧版本也不丢数据）。
    /// </summary>
    private static string InitDataDir()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(baseDir, "MediaDownloader");

        try
        {
            var legacy = Path.Combine(baseDir, "JJDownloader");
            if (Directory.Exists(legacy) && !Directory.Exists(dir))
                CopyTree(legacy, dir);
        }
        catch
        {
            // 迁移失败就当没有旧数据，用新目录从头开始
        }

        return dir;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(from))
            CopyTree(sub, Path.Combine(to, Path.GetFileName(sub)));
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string CredentialFile => Path.Combine(DataDir, "credential.bin");
    public static string LogFile => Path.Combine(DataDir, "app.log");

    /// <summary>抖音内嵌浏览器的用户数据目录。登录态就存在这里，跨启动复用。</summary>
    public static string DouyinProfileDir => Path.Combine(DataDir, "douyin-profile");

    /// <summary>今日头条内嵌浏览器的用户数据目录。和抖音分开，互不干扰。</summary>
    public static string ToutiaoProfileDir => Path.Combine(DataDir, "toutiao-profile");

    public static void Ensure() => Directory.CreateDirectory(DataDir);
}

/// <summary>下载目录中已存在同名文件时的处理方式。</summary>
public enum FileExistsAction
{
    /// <summary>弹窗询问（默认选项为跳过）。</summary>
    Ask,
    /// <summary>静默跳过，不询问。</summary>
    Skip,
    /// <summary>静默覆盖重新下载。</summary>
    OverwriteAll
}

public sealed class Settings
{
    public string DownloadDir { get; set; } = AppPaths.DefaultDownloadDir;

    public FileExistsAction OnFileExists { get; set; } = FileExistsAction.Ask;

    public int Segments { get; set; } = 8;
    public long SpeedLimitBytesPerSecond { get; set; } = 0;
    public string? FfmpegPath { get; set; } = AppPaths.DefaultFfmpegPath;
    public bool DeleteTempAfterMerge { get; set; } = true;

    /// <summary>
    /// 是否把运行日志写到 exe 目录的 logs\ 下（每日一个文件）。默认关闭，
    /// 需要排障时再打开。崩溃兜底日志（%APPDATA% 里的 app.log）不受这个开关影响。
    /// </summary>
    public bool EnableFileLog { get; set; }

    /// <summary>用户是否手动指定过下载目录。true 时不再被默认值覆盖。</summary>
    public bool DownloadDirUserSet { get; set; }
    /// <summary>用户是否手动指定过 ffmpeg 路径。true 时不再被默认值覆盖。</summary>
    public bool FfmpegPathUserSet { get; set; }

    public static Settings Load()
    {
        AppPaths.Ensure();
        Settings s;
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var text = File.ReadAllText(AppPaths.SettingsFile);
                s = JsonSerializer.Deserialize<Settings>(text) ?? new Settings();
            }
            else s = new Settings();
        }
        catch { s = new Settings(); }

        s.Migrate();
        try { s.Save(); } catch { /* 只读环境忽略 */ }
        return s;
    }

    /// <summary>
    /// 用户没手动选过就用跟随 exe 目录的默认值；选过则原样保留用户的选择。
    /// </summary>
    private void Migrate()
    {
        if (!DownloadDirUserSet || string.IsNullOrWhiteSpace(DownloadDir))
            DownloadDir = AppPaths.DefaultDownloadDir;

        if (!FfmpegPathUserSet || string.IsNullOrWhiteSpace(FfmpegPath))
            FfmpegPath = AppPaths.DefaultFfmpegPath;

        try { Directory.CreateDirectory(DownloadDir); } catch { /* 无权限时忽略 */ }
    }

    public void Save()
    {
        AppPaths.Ensure();
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
