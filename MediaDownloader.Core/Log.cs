using System.Text;

namespace MediaDownloader.Core;

/// <summary>
/// 轻量文件日志。刻意不用 log4net / NLog 这类框架：
/// 需求只有"开关 + 每日一个文件"，手写几十行零依赖，单文件发布也干净。
///
/// 规则：
/// - 默认关闭，由设置页的「记录日志」开关控制（Settings.EnableFileLog）；
/// - 开启后写到 exe 目录下 logs\yyyy-MM-dd.log（和下载目录放一起，删掉整个目录就清干净）；
/// - 全部操作吞异常：日志写不进去（exe 目录只读等）绝不能影响下载本身；
/// - 全局一把锁串行写，量级是"人工排障"而非"高频遥测"，够用。
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static bool _enabled;

    /// <summary>开关。设置页改完即时生效，不用重启。</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value) Info("log", "文件日志已开启");
        }
    }

    /// <summary>日志目录：exe 目录下的 logs。</summary>
    public static string LogsDir => Path.Combine(AppPaths.ExeDir, "logs");

    public static void Info(string source, string message) => Write("INFO", source, message);
    public static void Warn(string source, string message) => Write("WARN", source, message);
    public static void Error(string source, string message) => Write("ERROR", source, message);

    public static void Error(string source, Exception ex)
        => Write("ERROR", source, $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private static void Write(string level, string source, string message)
    {
        if (!_enabled) return;

        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogsDir);
                var file = Path.Combine(LogsDir, $"{DateTime.Now:yyyy-MM-dd}.log");
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] [{source}] {message}{Environment.NewLine}";
                // 不带 BOM 的 UTF-8：AppendAllText 用 Encoding.UTF8 会给每个新文件加 BOM，没必要
                File.AppendAllText(file, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // 日志写失败静默放弃；这里再抛就是用日志把应用弄挂
        }
    }
}
