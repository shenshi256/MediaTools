using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MediaDownloader.Core;

namespace MediaDownloader.App.ViewModels;

/// <summary>
/// 腾讯视频下载页的 VM。
///
/// 和头条那套的区别：
/// - 一个链接对应**一整部剧**（几十集），不是单条视频，所以有分集勾选列表；
/// - 解析阶段只拿档位元数据，直链要到下载时按档位单独请求（腾讯一份响应只给一个档位）；
/// - **只做免费内容**：会员 / DRM 的集会在列表里标出来并禁用勾选，不提供任何绕过。
/// </summary>
public sealed class TencentViewModel : ObservableObject, IDisposable
{
    private readonly TencentClient _client = new();
    private readonly SpeedLimiter _limiter = new();
    private readonly DownloadEngine _engine;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly Settings _settings;

    /// <summary>由主窗口注入：文件已存在时的询问弹窗。</summary>
    public Func<IReadOnlyList<string>, DuplicateChoice>? DuplicateHandler { get; set; }

    private TencentVideo? _video;
    private TencentQuality? _selectedQuality;
    private string _inputText = "";
    private string _statusMessage = "粘贴 v.qq.com 链接后点解析 · 仅支持免费内容，会员视频不下载";
    private string _videoTitle = "";
    private string _videoInfo = "";
    private bool _isParsing;

    public TencentViewModel()
    {
        _settings = Settings.Load();
        _engine = new DownloadEngine(_limiter);
        _limiter.BytesPerSecond = _settings.SpeedLimitBytesPerSecond;
    }

    public ObservableCollection<TencentEpisodeItem> Episodes { get; } = new();
    public ObservableCollection<TencentQuality> Qualities { get; } = new();
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

    public TencentQuality? SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (!Set(ref _selectedQuality, value)) return;
            RefreshCommands();
        }
    }

    private int SelectedCount => Episodes.Count(e => e.IsSelected);

    public string SelectionText => _video is null
        ? "分集"
        : $"已选 {SelectedCount}/{Episodes.Count} 集（可下 {_video.FreeCount}）";

    /// <summary>有没有被挡下的集，用来决定提示条显不显示。</summary>
    public bool HasBlocked => _video is not null && _video.BlockedCount > 0;

    public string BlockedHint => _video is null || !HasBlocked
        ? ""
        : $"其中 {_video.BlockedCount} 集需要会员或受 DRM 保护，本工具不下载。";

    private ICommand? _parseCommand, _downloadCommand, _selectAllCommand, _clearCommand;

    public ICommand ParseCommand => _parseCommand ??= new AsyncRelayCommand(_ => ParseAsync(), _ => !IsParsing);
    public ICommand DownloadCommand => _downloadCommand ??= new AsyncRelayCommand(_ => DownloadAsync(), _ => CanDownload());
    public ICommand SelectAllCommand => _selectAllCommand ??= new RelayCommand(_ => SetAll(true));
    public ICommand ClearCommand => _clearCommand ??= new RelayCommand(_ => SetAll(false));

    private bool CanDownload() => _video is not null && SelectedQuality is not null && SelectedCount > 0;

    private void RefreshCommands()
    {
        (ParseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DownloadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasBlocked));
        OnPropertyChanged(nameof(BlockedHint));
    }

    private void SetAll(bool selected)
    {
        foreach (var e in Episodes) e.IsSelected = selected;
        RefreshCommands();
    }

    // ---------- 解析 ----------

    public async Task ParseAsync()
    {
        if (IsParsing) return;

        try
        {
            IsParsing = true;
            StatusMessage = "解析中：正在读取剧集列表…";

            var progress = new Progress<(int Done, int Total)>(p =>
                StatusMessage = $"解析中：已读取 {p.Done}/{p.Total} 集…");

            var video = await _client.FetchAsync(InputText, progress);

            if (video.Episodes.Count == 0)
            {
                ResetVideo();
                StatusMessage = "没解析到任何剧集";
                return;
            }

            _video = video;
            VideoTitle = string.IsNullOrWhiteSpace(video.Title) ? "（无标题）" : video.Title.Trim();
            VideoInfo = video.InfoText;

            Episodes.Clear();
            foreach (var ep in video.Episodes)
            {
                var item = new TencentEpisodeItem(ep, isSelected: ep.CanDownload);
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(TencentEpisodeItem.IsSelected)) RefreshCommands();
                };
                Episodes.Add(item);
            }

            // 档位取第一个能下的集（同一部剧各集档位一致）
            Qualities.Clear();
            var firstPlayable = video.Episodes.FirstOrDefault(e => e.CanDownload);
            if (firstPlayable is not null)
                foreach (var q in firstPlayable.Qualities) Qualities.Add(q);
            SelectedQuality = Qualities.FirstOrDefault();

            if (video.FreeCount == 0)
                StatusMessage = "这部剧没有免费集，本工具不下载会员内容";
            else if (HasBlocked)
                StatusMessage = $"解析完成：{video.FreeCount} 集可下，{video.BlockedCount} 集需会员（已跳过）";
            else
                StatusMessage = $"解析完成：{video.FreeCount} 集全部可下";

            OnPropertyChanged(nameof(SelectionText));
            OnPropertyChanged(nameof(HasBlocked));
            OnPropertyChanged(nameof(BlockedHint));
            Log.Info("tencent", $"解析成功 cid={video.Cid} 集数={video.Episodes.Count} 免费={video.FreeCount}");
        }
        catch (TencentException ex)
        {
            Log.Warn("tencent", "解析失败：" + ex.Message);
            ResetVideo();
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("tencent", ex);
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
        Episodes.Clear();
        Qualities.Clear();
        SelectedQuality = null;
        RefreshCommands();
    }

    // ---------- 下载 ----------

    public async Task DownloadAsync()
    {
        await Task.CompletedTask;

        if (_video is null || SelectedQuality is null) return;

        var picked = Episodes.Where(e => e.IsSelected).ToList();
        if (picked.Count == 0)
        {
            StatusMessage = "没有勾选任何集";
            return;
        }

        var quality = SelectedQuality;
        var dir = _settings.DownloadDir;

        // 先把要下的文件名算出来，重名一次性问清楚
        var planned = picked
            .Select(e => (Item: e, Name: MainViewModel.SafeFileName(
                string.IsNullOrWhiteSpace(e.Episode.DisplayTitle) ? "tencent_" + e.Episode.Vid : e.Episode.DisplayTitle)))
            .ToList();

        var existing = planned
            .Select(p => p.Name + ".mp4")
            .Where(n => File.Exists(Path.Combine(dir, n)))
            .ToList();

        var action = _settings.OnFileExists;
        if (existing.Count > 0)
        {
            if (action == FileExistsAction.Ask)
            {
                var choice = DuplicateHandler?.Invoke(existing) ?? DuplicateChoice.Skip;
                if (choice == DuplicateChoice.Cancel)
                {
                    StatusMessage = "已取消，未创建下载任务";
                    return;
                }
                action = choice == DuplicateChoice.DownloadAll
                    ? FileExistsAction.OverwriteAll
                    : FileExistsAction.Skip;
            }
            if (action == FileExistsAction.Skip)
                Log.Info("tencent", $"跳过已存在的 {existing.Count} 个文件");
        }

        var dirFull = dir;
        var queued = 0;

        foreach (var (item, name) in planned)
        {
            var fileName = name + ".mp4";
            var outPath = Path.Combine(dirFull, fileName);

            if (action == FileExistsAction.Skip && File.Exists(outPath)) continue;

            var ep = item.Episode;
            DownloadTaskViewModel? vm = null;
            vm = new DownloadTaskViewModel(name, quality.Label, outPath, async () =>
            {
                var progress = new Progress<DownloadProgress>(p => vm!.ReportVideo(p));

                // 每次下载前现取地址：vkey 有时效（实测约数小时），解析时拿的未必还活着
                var stream = await _client.FetchStreamAsync(ep.Vid, quality, vm!.Token);

                await _engine.DownloadAsync(new DownloadRequest
                {
                    Url = stream.Url,
                    BackupUrls = stream.BackupUrls,
                    DestinationPath = outPath,
                    Referer = TencentClient.Referer,
                    Segments = Math.Clamp(_settings.Segments, 1, 32)
                }, progress, vm!.Token);

                vm.State = DownloadState.Completed;
                vm.Refresh();
                Log.Info("tencent", $"完成 {fileName}（{stream.Height}P）");
            }, () => Tasks.Remove(vm!));

            Tasks.Add(vm);
            queued++;
            _ = RunInQueueAsync(vm);
        }

        StatusMessage = queued == 0
            ? "同名文件全部已存在，按设置跳过"
            : $"已加入 {queued} 个下载任务（串行执行）";
    }

    private async Task RunInQueueAsync(DownloadTaskViewModel vm)
    {
        await _queue.WaitAsync();
        try
        {
            await vm.StartAsync();
            // 全部任务跑完后刷新一次提示
            if (Tasks.All(t => t.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled))
                StatusMessage = "全部任务已结束";
        }
        finally { _queue.Release(); }
    }

    public void Dispose()
    {
        _engine.Dispose();
        _client.Dispose();
        _queue.Dispose();
    }
}
