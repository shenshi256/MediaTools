using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MediaDownloader.App.Services;
using MediaDownloader.Core;

namespace MediaDownloader.App.ViewModels;

/// <summary>
/// 抖音下载页的 VM。与 B 站那套的区别：
/// - 没有分 P，一链接一作品；
/// - 单流 mp4（不分离音视频），因此不需要 ffmpeg 合并；
/// - 详情由内嵌浏览器拦下来，登录态复用同一个浏览器 profile。
/// </summary>
public sealed class DouyinViewModel : ObservableObject, IDisposable
{
    private readonly DouyinClient _client = new();
    private readonly DouyinBrowser _browser = new();
    private readonly SpeedLimiter _limiter = new();
    private readonly DownloadEngine _engine;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private Settings _settings;

    /// <summary>由主窗口注入：文件已存在时的询问弹窗。</summary>
    public Func<IReadOnlyList<string>, DuplicateChoice>? DuplicateHandler { get; set; }

    /// <summary>由主窗口注入：退出登录前的阻断式确认弹窗，返回是否确认。</summary>
    public Func<bool>? ConfirmLogoutHandler { get; set; }

    /// <summary>由主窗口注入：轻提示（toast）。</summary>
    public Action<string>? ToastHandler { get; set; }

    private DouyinVideo? _video;
    private DouyinStream? _selectedQuality;
    private string _inputText = "";
    private string _statusMessage = "粘贴抖音链接后点解析";
    private string _loginStatus = "未登录";
    private string _videoTitle = "";
    private string _videoInfo = "";
    private bool _isParsing;
    private bool _isLoggingIn;

    public DouyinViewModel()
    {
        _settings = Settings.Load();
        _engine = new DownloadEngine(_limiter);
        _limiter.BytesPerSecond = _settings.SpeedLimitBytesPerSecond;
    }

    public ObservableCollection<DouyinStream> Qualities { get; } = new();
    public ObservableCollection<DownloadTaskViewModel> Tasks { get; } = new();

    public string InputText { get => _inputText; set => Set(ref _inputText, value); }
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public string LoginStatus { get => _loginStatus; set => Set(ref _loginStatus, value); }
    public string VideoTitle { get => _videoTitle; set => Set(ref _videoTitle, value); }
    public string VideoInfo { get => _videoInfo; set => Set(ref _videoInfo, value); }

    public bool IsParsing
    {
        get => _isParsing;
        private set { if (Set(ref _isParsing, value)) RefreshCommands(); }
    }

    public bool IsLoggingIn
    {
        get => _isLoggingIn;
        private set { if (Set(ref _isLoggingIn, value)) RefreshCommands(); }
    }

    public DouyinStream? SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (!Set(ref _selectedQuality, value)) return;
            RefreshCommands();
        }
    }

    private ICommand? _parseCommand, _downloadCommand, _loginCommand, _logoutCommand;

    public ICommand ParseCommand => _parseCommand ??= new AsyncRelayCommand(_ => ParseAsync(), _ => !IsParsing && !IsLoggingIn);
    public ICommand DownloadCommand => _downloadCommand ??= new AsyncRelayCommand(_ => DownloadAsync(), _ => CanDownload());
    public ICommand LoginCommand => _loginCommand ??= new AsyncRelayCommand(_ => LoginAsync(), _ => !IsLoggingIn);
    public ICommand LogoutCommand => _logoutCommand ??= new RelayCommand(_ => Logout(), _ => !IsLoggingIn);

    private bool CanDownload() => _video is not null && SelectedQuality is not null;

    private void RefreshCommands()
    {
        (ParseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (LoginCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (LogoutCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ---------- 登录 ----------

    /// <summary>页面显示出来时调一次，把上次的登录态读回来。</summary>
    public async Task RefreshLoginStateAsync()
    {
        try
        {
            await _browser.EnsureReadyAsync();
            LoginStatus = await _browser.HasLoginCookieAsync() ? "已登录" : "未登录（公开视频也能下）";
        }
        catch (Exception ex)
        {
            LoginStatus = "未登录";
            StatusMessage = "内嵌浏览器初始化失败：" + ex.Message;
        }
    }

    private async Task LoginAsync()
    {
        try
        {
            IsLoggingIn = true;
            StatusMessage = "已打开抖音网页，请用抖音 App 扫码并在手机上确认…";
            var ok = await _browser.ShowLoginAsync();
            await RefreshLoginStateAsync();
            StatusMessage = ok ? "登录成功" : "登录未完成（3 分钟没等到扫码），可以再试一次";
            Log.Info("douyin", ok ? "扫码登录成功" : "扫码登录未完成（超时）");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消登录";
        }
        catch (Exception ex)
        {
            Log.Warn("douyin", "登录失败：" + ex.Message);
            StatusMessage = "登录失败：" + ex.Message;
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    private async void Logout()
    {
        // 先查是否真的登录，再决定弹确认框还是 toast
        bool loggedIn;
        try
        {
            await _browser.EnsureReadyAsync();
            loggedIn = await _browser.HasLoginCookieAsync();
        }
        catch
        {
            loggedIn = false;
        }

        if (!loggedIn)
        {
            ToastHandler?.Invoke("您当前还未登录");
            return;
        }

        var confirmed = ConfirmLogoutHandler?.Invoke() ?? false;
        if (!confirmed) return;

        _browser.SignOut();
        LoginStatus = "未登录";
        StatusMessage = "已退出抖音登录";
        Log.Info("douyin", "已退出登录");
    }

    // ---------- 解析 ----------

    public async Task ParseAsync()
    {
        if (IsParsing) return;

        try
        {
            IsParsing = true;
            StatusMessage = "解析中：正在让内嵌浏览器打开视频页…";

            var id = await _client.ResolveAwemeIdAsync(InputText);
            var video = await _browser.FetchAsync(id);

            if (video.IsImagePost)
            {
                ResetVideo();
                StatusMessage = "这是图文作品（图集），目前只支持视频下载";
                return;
            }

            if (video.Streams.Count == 0)
            {
                ResetVideo();
                StatusMessage = "没解析到可用的清晰度，可能这条视频需要登录";
                return;
            }

            _video = video;
            VideoTitle = string.IsNullOrWhiteSpace(video.Title) ? "（无标题）" : video.Title;
            VideoInfo = video.InfoText;

            Qualities.Clear();
            foreach (var s in video.Streams) Qualities.Add(s);
            SelectedQuality = Qualities.FirstOrDefault();

            StatusMessage = $"解析成功，共 {Qualities.Count} 档清晰度";
            Log.Info("douyin", $"解析成功 {video.AwemeId} 档位={Qualities.Count}");
        }
        catch (DouyinException ex)
        {
            Log.Warn("douyin", "解析失败：" + ex.Message);
            ResetVideo();
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("douyin", ex);
            ResetVideo();
            StatusMessage = "解析失败：" + ex.Message;
        }
        finally
        {
            IsParsing = false;
        }
    }

    private void ResetVideo()
    {
        _video = null;
        VideoTitle = "";
        VideoInfo = "";
        Qualities.Clear();
        SelectedQuality = null;
        RefreshCommands();
    }

    // ---------- 下载 ----------

    public async Task DownloadAsync()
    {
        await Task.CompletedTask;

        if (_video is null || SelectedQuality is null) return;
        var quality = SelectedQuality;

        var baseName = MainViewModel.SafeFileName(
            string.IsNullOrWhiteSpace(_video.Title) ? "douyin_" + _video.AwemeId : _video.Title);

        var outPath = Path.Combine(_settings.DownloadDir, baseName + ".mp4");

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

        var title = baseName;
        DownloadTaskViewModel? vm = null;
        vm = new DownloadTaskViewModel(title, quality.Label, outPath, async () =>
        {
            var progress = new Progress<DownloadProgress>(p => vm!.ReportVideo(p));

            await _engine.DownloadAsync(new DownloadRequest
            {
                Url = quality.Url,
                BackupUrls = quality.BackupUrls,
                DestinationPath = outPath,
                Referer = DouyinClient.Referer,
                Segments = Math.Clamp(_settings.Segments, 1, 32)
            }, progress, vm!.Token);

            vm.State = DownloadState.Completed;
            vm.Refresh();
            StatusMessage = "完成：" + Path.GetFileName(outPath);
            Log.Info("douyin", $"完成 {Path.GetFileName(outPath)}");
        }, () => Tasks.Remove(vm!));

        Tasks.Add(vm);
        StatusMessage = "已加入下载任务";
        _ = RunInQueueAsync(vm);
        Log.Info("douyin", $"加入任务 {quality.Label} -> {Path.GetFileName(outPath)}");
    }

    private async Task RunInQueueAsync(DownloadTaskViewModel vm)
    {
        await _queue.WaitAsync();
        try { await vm.StartAsync(); }
        finally { _queue.Release(); }
    }

    // ---------- 释放 ----------

    public void Dispose()
    {
        _engine.Dispose();
        _client.Dispose();
        _browser.Dispose();
        _queue.Dispose();
    }
}
