using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MediaDownloader.App.Services;
using MediaDownloader.Core;

namespace MediaDownloader.App.ViewModels;

/// <summary>
/// 今日头条下载页的 VM。和抖音那套的区别：
/// - 解析走纯 HTTP（ToutiaoClient），不经过内嵌浏览器，所以快得多（约 1 秒）；
/// - 内嵌浏览器只用来扫码登录，登录后的 Cookie 会带进解析请求；
/// - 单流 mp4，不需要 ffmpeg 合并。
/// </summary>
public sealed class ToutiaoViewModel : ObservableObject, IDisposable
{
    private readonly ToutiaoClient _client = new();
    private readonly ToutiaoBrowser _browser = new();
    private readonly SpeedLimiter _limiter = new();
    private readonly DownloadEngine _engine;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly Settings _settings;

    /// <summary>由主窗口注入：文件已存在时的询问弹窗。</summary>
    public Func<IReadOnlyList<string>, DuplicateChoice>? DuplicateHandler { get; set; }

    /// <summary>由主窗口注入：退出登录前的阻断式确认弹窗，返回是否确认。</summary>
    public Func<bool>? ConfirmLogoutHandler { get; set; }

    /// <summary>由主窗口注入：轻提示（toast）。</summary>
    public Action<string>? ToastHandler { get; set; }

    private ToutiaoVideo? _video;
    private ToutiaoStream? _selectedQuality;
    private string _inputText = "";
    private string _statusMessage = "粘贴今日头条链接后点解析";
    private string _loginStatus = "未登录";
    private string _videoTitle = "";
    private string _videoInfo = "";
    private bool _isParsing;
    private bool _isLoggingIn;
    private bool _isLoggedIn;

    public ToutiaoViewModel()
    {
        _settings = Settings.Load();
        _engine = new DownloadEngine(_limiter);
        _limiter.BytesPerSecond = _settings.SpeedLimitBytesPerSecond;
    }

    public ObservableCollection<ToutiaoStream> Qualities { get; } = new();
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

    public ToutiaoStream? SelectedQuality
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
            _isLoggedIn = await _browser.HasLoginCookieAsync();
            LoginStatus = _isLoggedIn ? "已登录" : "未登录（公开视频也能下）";
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
            StatusMessage = "已打开头条网页，请用今日头条 App 扫码并在手机上确认…";
            var ok = await _browser.ShowLoginAsync();
            await RefreshLoginStateAsync();
            StatusMessage = ok ? "登录成功" : "登录未完成（3 分钟没等到扫码），可以再试一次";
            Log.Info("toutiao", ok ? "扫码登录成功" : "扫码登录未完成（超时）");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消登录";
        }
        catch (Exception ex)
        {
            Log.Warn("toutiao", "登录失败：" + ex.Message);
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
        _isLoggedIn = false;
        LoginStatus = "未登录";
        StatusMessage = "已退出今日头条登录";
        Log.Info("toutiao", "已退出登录");
    }

    // ---------- 解析 ----------

    public async Task ParseAsync()
    {
        if (IsParsing) return;

        try
        {
            IsParsing = true;
            StatusMessage = "解析中：正在请求视频信息…";

            // 只在登录后才带 Cookie：未登录时接口本来就不需要，
            // 把 tt_webid / _ga 那堆匿名 cookie 一起发过去只会有多余的风控风险
            var cookie = _isLoggedIn ? await _browser.ExportCookieHeaderAsync() : null;

            var video = await _client.FetchAsync(InputText, cookie);

            if (video.Streams.Count == 0)
            {
                ResetVideo();
                StatusMessage = "没解析到可用的清晰度，可能这条视频需要登录";
                return;
            }

            _video = video;
            VideoTitle = string.IsNullOrWhiteSpace(video.Title) ? "（无标题）" : video.Title.Trim();
            VideoInfo = video.InfoText;

            Qualities.Clear();
            foreach (var s in video.Streams) Qualities.Add(s);
            SelectedQuality = Qualities.FirstOrDefault();

            StatusMessage = $"解析成功，共 {Qualities.Count} 档清晰度";
            Log.Info("toutiao", $"解析成功 {video.GroupId} 档位={Qualities.Count}");
        }
        catch (ToutiaoException ex)
        {
            Log.Warn("toutiao", "解析失败：" + ex.Message);
            ResetVideo();
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("toutiao", ex);
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
            string.IsNullOrWhiteSpace(_video.Title) ? "toutiao_" + _video.GroupId : _video.Title);

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
                Referer = ToutiaoClient.Referer,
                Segments = Math.Clamp(_settings.Segments, 1, 32)
            }, progress, vm!.Token);

            vm.State = DownloadState.Completed;
            vm.Refresh();
            StatusMessage = "完成：" + Path.GetFileName(outPath);
            Log.Info("toutiao", $"完成 {Path.GetFileName(outPath)}");
        }, () => Tasks.Remove(vm!));

        Tasks.Add(vm);
        StatusMessage = "已加入下载任务";
        _ = RunInQueueAsync(vm);
        Log.Info("toutiao", $"加入任务 {quality.Label} -> {Path.GetFileName(outPath)}");
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
