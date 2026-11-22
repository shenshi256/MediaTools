using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MediaDownloader.Core;

namespace MediaDownloader.App.ViewModels;

/// <summary>
/// YouTube 下载页的 VM。
///
/// 和前面平台最大的区别：下载不经过 DownloadEngine，而是让 yt-dlp 一条命令
/// 干完「解析签名 + 下载分段 + 音视频合并」。所以进度也走 yt-dlp 的输出解析，
/// 而不是 DownloadEngine 的 DownloadProgress。
///
/// 网络：不做代理配置。yt-dlp 走系统代理，连不上就报错，用户自己解决网络。
/// </summary>
public sealed class YoutubeViewModel : ObservableObject, IDisposable
{
    private readonly YoutubeClient _client = new();
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly Settings _settings;

    private YoutubeVideo? _video;
    private YoutubeFormat? _selectedFormat;
    private string _inputText = "";
    private string _statusMessage = "粘贴 YouTube 链接后点解析（需要本机网络能访问 YouTube）";
    private string _videoTitle = "";
    private string _videoInfo = "";
    private bool _isParsing;

    // Cookie 配置
    private string _cookieText = "";
    private string _cookieStatus;
    private bool _isCookiePanelOpen;

    public YoutubeViewModel()
    {
        _settings = Settings.Load();
        _cookieStatus = YoutubeClient.CookiePath is not null
            ? "已配置 Cookie（登录态解析可用）"
            : "未配置 Cookie（仅支持下载公开视频）";
    }

    public ObservableCollection<YoutubeFormat> Formats { get; } = new();
    public ObservableCollection<DownloadTaskViewModel> Tasks { get; } = new();

    public string InputText { get => _inputText; set => Set(ref _inputText, value); }
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public string VideoTitle { get => _videoTitle; set => Set(ref _videoTitle, value); }
    public string VideoInfo { get => _videoInfo; set => Set(ref _videoInfo, value); }

    public bool IsParsing
    {
        get => _isParsing;
        private set { if (Set(ref _isParsing, value)) RefreshCommands(); }
    }

    public YoutubeFormat? SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            if (!Set(ref _selectedFormat, value)) return;
            RefreshCommands();
        }
    }

    public bool ToolMissing => !_client.IsAvailable;

    public string ToolMissingHint => _client.AvailabilityHint;

    // ---------- Cookie ----------

    public string CookieText { get => _cookieText; set => Set(ref _cookieText, value); }

    public string CookieStatus
    {
        get => _cookieStatus;
        private set => Set(ref _cookieStatus, value);
    }

    public bool IsCookiePanelOpen
    {
        get => _isCookiePanelOpen;
        set => Set(ref _isCookiePanelOpen, value);
    }

    public bool HasCookie => YoutubeClient.CookiePath is not null;

    private ICommand? _saveCookieCommand, _clearCookieCommand, _openCookiePanelCommand, _cookieHelpCommand;

    public ICommand SaveCookieCommand => _saveCookieCommand ??= new RelayCommand(_ => SaveCookie());

    public ICommand ClearCookieCommand => _clearCookieCommand ??= new RelayCommand(_ => ClearCookie());

    public ICommand ToggleCookiePanelCommand =>
        _openCookiePanelCommand ??= new RelayCommand(_ => IsCookiePanelOpen = !IsCookiePanelOpen);

    public ICommand CookieHelpCommand => _cookieHelpCommand ??= new RelayCommand(_ => CookieHelpHandler?.Invoke());

    private void SaveCookie()
    {
        CookieStatus = YoutubeClient.SaveCookie(CookieText);
        OnPropertyChanged(nameof(HasCookie));
        if (!string.IsNullOrWhiteSpace(CookieText)) IsCookiePanelOpen = false;
    }

    private void ClearCookie()
    {
        CookieText = "";
        CookieStatus = YoutubeClient.SaveCookie(null);
        OnPropertyChanged(nameof(HasCookie));
    }

    private ICommand? _parseCommand, _downloadCommand;

    public ICommand ParseCommand => _parseCommand ??= new AsyncRelayCommand(_ => ParseAsync(), _ => !IsParsing);
    public ICommand DownloadCommand => _downloadCommand ??= new AsyncRelayCommand(_ => DownloadAsync(), _ => CanDownload());

    private bool CanDownload() => _video is not null && SelectedFormat is not null;

    private void RefreshCommands()
    {
        (ParseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    // ---------- 解析 ----------

    public async Task ParseAsync()
    {
        if (IsParsing) return;

        try
        {
            IsParsing = true;
            StatusMessage = "解析中：正在请求视频信息…";

            var video = await _client.FetchAsync(InputText);

            if (video.Formats.Count == 0)
            {
                ResetVideo();
                StatusMessage = "没解析到可用格式。可能是 EJS 解密运行时（node）没装，或视频受限";
                return;
            }

            _video = video;
            VideoTitle = string.IsNullOrWhiteSpace(video.Title) ? "（无标题）" : video.Title.Trim();
            VideoInfo = video.InfoText;

            Formats.Clear();
            foreach (var f in video.Formats) Formats.Add(f);
            SelectedFormat = PickDefault(video.Formats);

            StatusMessage = $"解析成功，共 {Formats.Count} 种格式";
            Log.Info("youtube", $"解析成功 {video.VideoId} 格式数={video.Formats.Count}");
        }
        catch (YoutubeException ex)
        {
            Log.Warn("youtube", "解析失败：" + ex.Message);
            ResetVideo();
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("youtube", ex);
            ResetVideo();
            StatusMessage = "解析失败：" + ex.Message;
        }
        finally
        {
            IsParsing = false;
        }
    }

    /// <summary>默认选合并流里最高的那一档（音画一体，省得再合并）。</summary>
    private static YoutubeFormat? PickDefault(IReadOnlyList<YoutubeFormat> formats)
        => formats.FirstOrDefault(f => f.IsCombined) ?? formats.FirstOrDefault();

    private void ResetVideo()
    {
        _video = null;
        VideoTitle = "";
        VideoInfo = "";
        Formats.Clear();
        SelectedFormat = null;
        RefreshCommands();
    }

    // ---------- 下载 ----------

    public async Task DownloadAsync()
    {
        await Task.CompletedTask;

        if (_video is null || SelectedFormat is null) return;
        var format = SelectedFormat;

        var baseName = MainViewModel.SafeFileName(
            string.IsNullOrWhiteSpace(_video.Title) ? "youtube_" + _video.VideoId : _video.Title);

        // 最终扩展名：仅视频流会加 bestaudio 合并成 mp4；其余用格式自带的扩展名。
        var finalExt = format.IsVideoOnly ? "mp4" : (format.Ext == "" ? "mp4" : format.Ext);

        var outPath = Path.Combine(_settings.DownloadDir, baseName + "." + finalExt);

        // 重名处理
        if (File.Exists(outPath))
        {
            var action = _settings.OnFileExists;
            if (action == FileExistsAction.Ask)
            {
                var choice = DuplicateHandler?.Invoke(new[] { Path.GetFileName(outPath) }) ?? DuplicateChoice.Skip;
                if (choice == DuplicateChoice.Cancel)
                {
                    StatusMessage = "已取消，未创建下载任务";
                    return;
                }
                action = choice == DuplicateChoice.DownloadAll ? FileExistsAction.OverwriteAll : FileExistsAction.Skip;
            }
            if (action == FileExistsAction.Skip)
            {
                StatusMessage = "同名文件已存在，按设置跳过：" + Path.GetFileName(outPath);
                return;
            }
        }

        // 格式选择器：选了"仅视频"流时，自动追加 bestaudio 让 yt-dlp 配音频并合并；
        // 合并流 / 纯音频流直接下。
        var formatSelector = format.IsVideoOnly
            ? format.FormatId + "+bestaudio"
            : format.FormatId;

        // yt-dlp 输出模板：下载到下载目录，文件名不带扩展名（yt-dlp 自己加）
        var outTemplate = Path.Combine(_settings.DownloadDir, baseName + ".%(ext)s");

        DownloadTaskViewModel? vm = null;
        vm = new DownloadTaskViewModel(baseName, format.Label, outPath, async () =>
        {
            var progress = new Progress<YoutubeProgress>(p => vm!.ReportYoutube(p));

            await _client.DownloadAsync(InputText, formatSelector, outTemplate, progress, vm!.Token);

            vm.State = DownloadState.Completed;
            vm.Refresh();
            Log.Info("youtube", $"完成 {Path.GetFileName(outPath)}");
        }, () => Tasks.Remove(vm!));

        Tasks.Add(vm);
        StatusMessage = "已加入下载任务";
        _ = RunInQueueAsync(vm);
        Log.Info("youtube", $"加入任务 {format.Label}");
    }

    private async Task RunInQueueAsync(DownloadTaskViewModel vm)
    {
        await _queue.WaitAsync();
        try
        {
            await vm.StartAsync();
            if (Tasks.All(t => t.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled))
                StatusMessage = "全部任务已结束";
        }
        finally { _queue.Release(); }
    }

    /// <summary>由主窗口注入：文件已存在时的询问弹窗。</summary>
    public Func<IReadOnlyList<string>, DuplicateChoice>? DuplicateHandler { get; set; }

    /// <summary>由主窗口注入：点「图文指导」时弹 Cookie 获取教程窗口。</summary>
    public Action? CookieHelpHandler { get; set; }

    public void Dispose()
    {
        _client.Dispose();
        _queue.Dispose();
    }
}
