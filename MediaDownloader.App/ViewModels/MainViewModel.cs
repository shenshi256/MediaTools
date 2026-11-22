using System.Collections.ObjectModel;
using System.IO;
using MediaDownloader.Core;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;

namespace MediaDownloader.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly BilibiliClient _client = new();
    private readonly SpeedLimiter _limiter = new();
    private readonly DownloadEngine _engine;
    private Settings _settings;

    private string _inputText = "";
    private string _statusMessage = "就绪";
    private string _videoTitle = "";
    private string _upName = "";
    private VideoMeta? _meta;
    private PageItem? _previewPage;
    private QualityOption? _selectedQuality;
    private bool _isLoggedIn;
    private string _loginStatus = "未登录";
    private string _sessDataInput = "";
    private readonly SemaphoreSlim _downloadQueue = new(1, 1);

    /// <summary>由主窗口注入：弹出扫码窗口并返回 cookies。</summary>
    public Func<QrSession, LoginService, Dictionary<string, string>?>? QrLoginHandler { get; set; }

    /// <summary>由主窗口注入：文件已存在时询问用户，返回处理方式。</summary>
    public Func<IReadOnlyList<string>, DuplicateChoice>? DuplicateHandler { get; set; }

    public MainViewModel()
    {
        _settings = Settings.Load();
        _engine = new DownloadEngine(_limiter);
        _limiter.BytesPerSecond = _settings.SpeedLimitBytesPerSecond;
        LoadSavedCredential();
    }

    /// <summary>视频工具三页共用的 VM，页面之间切换不会丢状态。</summary>
    public VideoToolsViewModel Tools { get; } = new();

    /// <summary>抖音下载页的 VM。</summary>
    public DouyinViewModel Douyin { get; } = new();

    /// <summary>今日头条下载页的 VM。</summary>
    public ToutiaoViewModel Toutiao { get; } = new();

    /// <summary>腾讯视频下载页的 VM。</summary>
    public TencentViewModel Tencent { get; } = new();

    /// <summary>YouTube 下载页的 VM。</summary>
    public YoutubeViewModel Youtube { get; } = new();

    /// <summary>视频压缩页的 VM。</summary>
    public CompressViewModel Compress { get; } = new();

    private string _currentPageKey = "Bilibili";

    /// <summary>左侧导航当前选中项，取值见 PageKeys。</summary>
    public string CurrentPageKey
    {
        get => _currentPageKey;
        set
        {
            if (!Set(ref _currentPageKey, value)) return;
            OnPropertyChanged(nameof(CurrentPageTitle));
        }
    }

    public string CurrentPageTitle => CurrentPageKey switch
    {
        "Douyin" => "抖音下载",
        "Toutiao" => "今日头条下载",
        "Tencent" => "腾讯视频下载",
        "Youtube" => "YouTube 下载",
        "Trim" => "视频截取",
        "Merge" => "视频合并",
        "Separate" => "音视分离",
        "Compress" => "视频压缩",
        "Settings" => "设置",
        _ => "哔哩哔哩下载"
    };

    public string InputText { get => _inputText; set => Set(ref _inputText, value); }

    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public string VideoTitle { get => _videoTitle; set => Set(ref _videoTitle, value); }
    public string UpName { get => _upName; set => Set(ref _upName, value); }

    public ObservableCollection<PageItem> Pages { get; } = new();
    public ObservableCollection<QualityOption> Qualities { get; } = new();
    public ObservableCollection<DownloadTaskViewModel> Tasks { get; } = new();

    /// <summary>用于预览清晰度列表的分 P（第一个选中的），实际下载按勾选逐个执行。</summary>
    public PageItem? SelectedPage
    {
        get => _previewPage;
        set
        {
            if (!Set(ref _previewPage, value)) return;
            _ = LoadQualitiesAsync();
        }
    }

    public string PageSelectionText
        => _meta is null ? "分 P" : $"分 P（已选 {Pages.Count(p => p.IsSelected)}/{Pages.Count}）";

    public QualityOption? SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (!Set(ref _selectedQuality, value)) return;
            (DownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsLoggedIn { get => _isLoggedIn; set => Set(ref _isLoggedIn, value); }
    public string LoginStatus { get => _loginStatus; set => Set(ref _loginStatus, value); }
    public string SessDataInput { get => _sessDataInput; set => Set(ref _sessDataInput, value); }

    // ---------- 设置 ----------

    public string DownloadDir
    {
        get => _settings.DownloadDir;
        set
        {
            if (_settings.DownloadDir == value) return;
            _settings.DownloadDir = value;
            _settings.DownloadDirUserSet = true;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public int Segments
    {
        get => _settings.Segments;
        set { if (_settings.Segments == value) return; _settings.Segments = value; _settings.Save(); OnPropertyChanged(); }
    }

    /// <summary>设置页「记录日志」开关。开了之后运行日志写到 exe 目录的 logs\ 下，按天分文件。</summary>
    public bool EnableFileLog
    {
        get => _settings.EnableFileLog;
        set
        {
            if (_settings.EnableFileLog == value) return;
            _settings.EnableFileLog = value;
            _settings.Save();
            Log.Enabled = value;
            OnPropertyChanged();
        }
    }

    public double SpeedLimitMB
    {
        get => _settings.SpeedLimitBytesPerSecond / 1024.0 / 1024.0;
        set
        {
            var bps = (long)Math.Max(0, value) * 1024 * 1024;
            _settings.SpeedLimitBytesPerSecond = bps;
            _limiter.BytesPerSecond = bps;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>下载目录已有同名文件时的处理：0=询问（默认跳过）1=直接跳过 2=直接覆盖。</summary>
    public int OnFileExists
    {
        get => (int)_settings.OnFileExists;
        set
        {
            var v = (FileExistsAction)Math.Clamp(value, 0, 2);
            if (_settings.OnFileExists == v) return;
            _settings.OnFileExists = v;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public string FfmpegPath
    {
        get => string.IsNullOrWhiteSpace(_settings.FfmpegPath) ? AppPaths.DefaultFfmpegPath : _settings.FfmpegPath;
        set
        {
            if ((_settings.FfmpegPath ?? "") == value) return;
            _settings.FfmpegPath = value;
            _settings.FfmpegPathUserSet = true;
            _settings.Save();
            OnPropertyChanged();
            Tools.ReloadEnvironment();
            Compress.ReloadEnvironment();
        }
    }

    private ICommand? _parseCommand, _downloadCommand, _loginCommand, _logoutCommand, _saveCookieCommand, _browseDirCommand, _browseFfmpegCommand, _selectAllPagesCommand, _clearPagesCommand;

    public ICommand ParseCommand => _parseCommand ??= new AsyncRelayCommand(_ => ParseAsync());
    public ICommand DownloadCommand => _downloadCommand ??= new AsyncRelayCommand(_ => DownloadAsync(), _ => SelectedQuality is not null);
    public ICommand LoginCommand => _loginCommand ??= new AsyncRelayCommand(_ => LoginAsync());
    public ICommand LogoutCommand => _logoutCommand ??= new RelayCommand(_ => Logout());
    public ICommand SaveCookieCommand => _saveCookieCommand ??= new RelayCommand(_ => SaveManualCookie());
    public ICommand BrowseDirCommand => _browseDirCommand ??= new RelayCommand(_ => BrowseDir());
    public ICommand BrowseFfmpegCommand => _browseFfmpegCommand ??= new RelayCommand(_ => BrowseFfmpeg());
    public ICommand SelectAllPagesCommand => _selectAllPagesCommand ??= new RelayCommand(_ => SetAllPages(true));
    public ICommand ClearPagesCommand => _clearPagesCommand ??= new RelayCommand(_ => SetAllPages(false));

    private void SetAllPages(bool selected)
    {
        foreach (var p in Pages) p.IsSelected = selected;
        OnPropertyChanged(nameof(PageSelectionText));
    }

    // ---------- 解析 ----------

    public async Task ParseAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText))
        {
            StatusMessage = "请输入 B站视频链接、BV 号或 av 号";
            return;
        }

        try
        {
            StatusMessage = "解析中…";
            var expanded = await _client.ExpandUrlAsync(InputText);
            var parsed = BilibiliClient.Parse(expanded);
            if (parsed is null)
            {
                StatusMessage = "无法识别该链接（支持 BV 号、av 号、视频页链接、b23.tv 短链）";
                return;
            }

            _meta = await _client.GetVideoInfoAsync(parsed);
            VideoTitle = _meta.Title;
            UpName = _meta.UpName;

            Pages.Clear();
            foreach (var p in _meta.Pages) Pages.Add(new PageItem(p));

            foreach (var item in Pages)
            {
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PageItem.IsSelected))
                        OnPropertyChanged(nameof(PageSelectionText));
                };
            }

            var want = parsed.Page;
            var first = Pages.FirstOrDefault(x => x.Page.Page == want) ?? Pages.FirstOrDefault();
            if (first is not null) first.IsSelected = true;
            SelectedPage = first;
            OnPropertyChanged(nameof(PageSelectionText));
            StatusMessage = $"解析成功：共 {Pages.Count} 个分 P，已默认勾选 P{first?.Page.Page}，可勾选多个一起下载";
            Log.Info("bilibili", $"解析成功 {_meta.Bvid} 分P={Pages.Count}");
        }
        catch (BilibiliException ex)
        {
            Log.Warn("bilibili", $"解析失败 code={ex.Code} {ex.Message}");
            StatusMessage = $"接口返回 {ex.Code}：{ex.Message}";
        }
        catch (Exception ex)
        {
            Log.Error("bilibili", ex);
            StatusMessage = "解析失败：" + ex.Message;
        }
    }

    private async Task LoadQualitiesAsync()
    {
        if (_meta is null || _previewPage is null) return;
        try
        {
            StatusMessage = "获取清晰度列表…";
            var list = await _client.GetQualityOptionsAsync(_meta.Bvid, _previewPage.Page.Cid);
            Qualities.Clear();
            foreach (var q in list) Qualities.Add(q);
            SelectedQuality = Qualities.FirstOrDefault();
            StatusMessage = $"可用清晰度 {Qualities.Count} 档";
        }
        catch (BilibiliException ex)
        {
            StatusMessage = $"获取清晰度失败（{ex.Code}）：{ex.Message}";
        }
        catch (Exception ex)
        {
            StatusMessage = "获取清晰度失败：" + ex.Message;
        }
    }

    // ---------- 下载 ----------

    public async Task DownloadAsync()
    {
        // 任务是排队执行的，这里只负责创建任务并立刻返回，因此没有需要 await 的操作
        await Task.CompletedTask;

        if (_meta is null || _selectedQuality is null) return;

        var selected = Pages.Where(p => p.IsSelected).Select(p => p.Page).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "请先在分 P 列表勾选要下载的分 P";
            return;
        }

        var bvid = _meta.Bvid;
        var qn = _selectedQuality.Id;
        var qualityName = _selectedQuality.Name;
        var referer = BilibiliClient.RefererOf(bvid);

        var ffmpeg = MediaMerger.FindFfmpeg(_settings.FfmpegPath);
        if (ffmpeg is null)
        {
            StatusMessage = "未找到 ffmpeg，无法合并 DASH 流。请在设置里指定 ffmpeg.exe 路径";
            return;
        }

        // 先规划出每个分 P 的目标文件名
        var planned = new List<(VideoPage Page, string BaseName, string OutputPath, string TmpDir)>();
        foreach (var page in selected)
        {
            var baseName = SafeFileName(Pages.Count > 1
                ? $"{_meta.Title} - P{page.Page:00} {page.Part}"
                : _meta.Title);
            var outputPath = Path.Combine(_settings.DownloadDir, baseName + ".mp4");
            var tmpDir = Path.Combine(_settings.DownloadDir, ".jjtmp", $"{bvid}_{page.Cid}_{qn}");
            planned.Add((page, baseName, outputPath, tmpDir));
        }

        // 查重：下载目录里已存在同名文件时按策略处理
        var duplicates = planned.Where(p => File.Exists(p.OutputPath)).ToList();
        if (duplicates.Count > 0)
        {
            var action = _settings.OnFileExists;
            if (action == FileExistsAction.Ask)
            {
                var choice = DuplicateHandler?.Invoke(duplicates.Select(p => Path.GetFileName(p.OutputPath)).ToList())
                             ?? DuplicateChoice.Skip;   // 没有弹窗时默认跳过
                if (choice == DuplicateChoice.Cancel)
                {
                    StatusMessage = "已取消，未创建任何下载任务";
                    return;
                }
                action = choice == DuplicateChoice.DownloadAll ? FileExistsAction.OverwriteAll : FileExistsAction.Skip;
            }

            if (action == FileExistsAction.Skip)
            {
                planned = planned.Where(p => !File.Exists(p.OutputPath)).ToList();
                if (planned.Count == 0)
                {
                    StatusMessage = $"所选 {duplicates.Count} 个文件都已下载过，已全部跳过";
                    return;
                }
                StatusMessage = $"跳过 {duplicates.Count} 个已存在的，开始下载其余 {planned.Count} 个";
            }
            else
            {
                StatusMessage = $"覆盖重新下载 {planned.Count} 个（含 {duplicates.Count} 个已存在的）";
            }
        }

        foreach (var (page, baseName, outputPath, tmpDir) in planned)
        {

            DownloadTaskViewModel? vm = null;
            vm = new DownloadTaskViewModel(baseName, qualityName, outputPath, async () =>
            {
                QualityOption stream;
                try
                {
                    StatusMessage = $"P{page.Page}：获取下载地址…";
                    stream = await _client.ResolveStreamAsync(bvid, page.Cid, qn)
                             ?? throw new IOException("获取下载地址失败");
                }
                catch (BilibiliException ex)
                {
                    throw new IOException($"P{page.Page} 获取下载地址失败（{ex.Code}）：{ex.Message}");
                }

                if (string.IsNullOrEmpty(stream.VideoUrl))
                    throw new IOException("该清晰度暂时没有可用地址，可能需要登录或大会员");

                Directory.CreateDirectory(tmpDir);
                var videoPath = Path.Combine(tmpDir, "v.m4s");
                var audioPath = Path.Combine(tmpDir, "a.m4s");

                var pv = new Progress<DownloadProgress>(p => vm!.ReportVideo(p));
                var pa = new Progress<DownloadProgress>(p => vm!.ReportAudio(p));

                var seg = Math.Clamp(_settings.Segments, 1, 32);
                var vTask = _engine.DownloadAsync(new DownloadRequest
                {
                    Url = stream.VideoUrl,
                    BackupUrls = stream.VideoBackupUrls,
                    DestinationPath = videoPath,
                    Referer = referer,
                    Segments = seg
                }, pv, vm!.Token);

                Task<long> aTask = Task.FromResult(0L);
                if (stream.IsDash && !string.IsNullOrEmpty(stream.AudioUrl))
                {
                    aTask = _engine.DownloadAsync(new DownloadRequest
                    {
                        Url = stream.AudioUrl,
                        BackupUrls = stream.AudioBackupUrls,
                        DestinationPath = audioPath,
                        Referer = referer,
                        Segments = Math.Max(1, seg / 2)
                    }, pa, vm.Token);
                }

                await Task.WhenAll(vTask, aTask);

                if (!stream.IsDash)
                {
                    File.Move(videoPath, outputPath, overwrite: true);
                }
                else
                {
                    vm.State = DownloadState.Merging;
                    vm.Refresh();
                    var result = await MediaMerger.MergeAsync(ffmpeg!, videoPath,
                        File.Exists(audioPath) ? audioPath : null, outputPath);
                    if (!result.Success) throw new IOException("ffmpeg 合并失败：" + result.Output);
                }

                if (_settings.DeleteTempAfterMerge)
                {
                    try { Directory.Delete(tmpDir, recursive: true); } catch { /* 忽略 */ }
                }

                vm.State = DownloadState.Completed;
                vm.Refresh();
                StatusMessage = "完成：" + Path.GetFileName(outputPath);
                Log.Info("bilibili", $"完成 {Path.GetFileName(outputPath)}");
            }, () => Tasks.Remove(vm!));

            Tasks.Add(vm);
            _ = RunInQueueAsync(vm);
            Log.Info("bilibili", $"加入任务 P{page.Page} {qualityName} -> {Path.GetFileName(outputPath)}");
        }

        if (duplicates.Count == 0)
        {
            StatusMessage = planned.Count == 1
                ? "已开始下载 1 个任务"
                : $"已加入 {planned.Count} 个下载任务（排队执行）";
        }
    }

    /// <summary>任务串行执行，避免多 P 同时下载互相抢带宽、触发风控。</summary>
    private async Task RunInQueueAsync(DownloadTaskViewModel vm)
    {
        await _downloadQueue.WaitAsync();
        try { await vm.StartAsync(); }
        finally { _downloadQueue.Release(); }
    }

    // ---------- 登录 ----------

    private void LoadSavedCredential()
    {
        var cookies = CredentialStore.Load();
        if (cookies is null || cookies.Count == 0) return;
        foreach (var (k, v) in cookies) _client.SetCookie(k, v);
        RefreshLoginState(cookies.ContainsKey("SESSDATA"));
    }

    private async Task LoginAsync()
    {
        if (QrLoginHandler is null)
        {
            StatusMessage = "登录窗口未就绪";
            return;
        }

        try
        {
            StatusMessage = "获取登录二维码…";
            using var svc = new LoginService();
            var session = await svc.RequestQrAsync();
            var cookies = QrLoginHandler(session, svc);

            if (cookies is { Count: > 0 })
            {
                foreach (var (k, v) in cookies) _client.SetCookie(k, v);
                CredentialStore.Save(cookies);
                RefreshLoginState(cookies.ContainsKey("SESSDATA"));
                StatusMessage = "登录成功，已加密保存凭证";
                Log.Info("bilibili", "扫码登录成功");
            }
            else
            {
                StatusMessage = "登录未完成";
            }
        }
        catch (Exception ex)
        {
            Log.Warn("bilibili", "登录失败：" + ex.Message);
            StatusMessage = "登录失败：" + ex.Message;
        }
    }

    private void Logout()
    {
        CredentialStore.Clear();
        RefreshLoginState(false);
        StatusMessage = "已退出登录，凭证已删除";
        Log.Info("bilibili", "已退出登录");
    }

    private void SaveManualCookie()
    {
        var value = SessDataInput.Trim();
        if (string.IsNullOrEmpty(value))
        {
            StatusMessage = "请先粘贴 SESSDATA";
            return;
        }
        _client.SetCookie("SESSDATA", value);
        CredentialStore.Save(new Dictionary<string, string> { ["SESSDATA"] = value });
        RefreshLoginState(true);
        SessDataInput = "";
        StatusMessage = "SESSDATA 已加密保存";
    }

    private void RefreshLoginState(bool loggedIn)
    {
        IsLoggedIn = loggedIn;
        LoginStatus = loggedIn ? "已登录" : "未登录（仅可下载低清晰度）";
    }

    // ---------- 设置 ----------

    private void BrowseDir()
    {
        var dlg = new OpenFolderDialog { Title = "选择下载目录" };
        if (Directory.Exists(DownloadDir)) dlg.InitialDirectory = DownloadDir;
        if (dlg.ShowDialog() == true) DownloadDir = dlg.FolderName;
    }

    private void BrowseFfmpeg()
    {
        var dlg = new OpenFileDialog { Title = "选择 ffmpeg.exe", Filter = "ffmpeg|ffmpeg.exe|所有文件|*.*" };
        if (dlg.ShowDialog() == true) FfmpegPath = dlg.FileName;
    }

    // ---------- 工具 ----------

    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            if (Array.IndexOf(invalid, c) >= 0 || c == '\n' || c == '\r') sb.Append('_');
            else sb.Append(c);
        }
        var s = sb.ToString().Trim().TrimEnd('.');
        return s.Length == 0 ? "video" : (s.Length > 100 ? s[..100] : s);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    public void Dispose()
    {
        Tools.CancelAll();
        Douyin.Dispose();
        Toutiao.Dispose();
        Tencent.Dispose();
        Youtube.Dispose();
        Compress.CancelAll();
        _engine.Dispose();
        _client.Dispose();
    }
}
