using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MediaDownloader.Core;

namespace MediaDownloader.App.ViewModels;

/// <summary>输入列表里的一行。</summary>
public sealed class CompressInput
{
    public CompressInput(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        try { SizeBytes = new FileInfo(path).Length; } catch { SizeBytes = 0; }
    }

    public string Path { get; }
    public string Name { get; }
    public long SizeBytes { get; }
    public string SizeText => SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024 / 1024:F2} GB",
        >= 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024:F1} MB",
        >= 1024 => $"{SizeBytes / 1024.0:F0} KB",
        _ => $"{SizeBytes} B"
    };
}

/// <summary>队列里的一条压缩任务。</summary>
public sealed class CompressTaskViewModel : ObservableObject
{
    public enum TaskState { Waiting, Running, Completed, Failed, Cancelled }

    public CompressTaskViewModel(string inputPath, string outputPath, string argsSummary)
    {
        InputPath = inputPath;
        OutputPath = outputPath;
        ArgsSummary = argsSummary;
    }

    public string InputPath { get; }
    public string OutputPath { get; }
    public string ArgsSummary { get; }
    public string FileName => System.IO.Path.GetFileName(InputPath);

    private TaskState _state = TaskState.Waiting;
    public TaskState State { get => _state; set => Set(ref _state, value); }

    private double _percent;
    public double Percent { get => _percent; internal set => Set(ref _percent, value); }

    private string _sizeText = "—";
    public string SizeText { get => _sizeText; internal set => Set(ref _sizeText, value); }

    public string StateText => State switch
    {
        TaskState.Waiting => "排队",
        TaskState.Running => "压缩中",
        TaskState.Completed => "完成",
        TaskState.Failed => "失败",
        _ => "已取消"
    };

    /// <summary>后台执行用。窗口关闭时整体取消。</summary>
    public CancellationTokenSource TokenSource { get; } = new();

    private ICommand? _openCommand;
    public ICommand OpenCommand => _openCommand ??= new RelayCommand(_ =>
    {
        try
        {
            if (File.Exists(OutputPath))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{OutputPath}\"");
        }
        catch { /* 忽略 */ }
    }, _ => File.Exists(OutputPath));
}

/// <summary>
/// 视频压缩页的 VM：输入文件列表、三种模式、估算、串行队列。
/// </summary>
public sealed class CompressViewModel : ObservableObject
{
    private string _ffmpeg = "";
    private string? _ffprobe;
    private string _envStatus = "";
    private bool _running;
    private readonly Settings _settings;

    public Func<IReadOnlyList<string>, DuplicateChoice>? DuplicateHandler { get; set; }

    public CompressViewModel()
    {
        _settings = Settings.Load();
        ReloadEnvironment();
    }

    // ---------- 环境 ----------

    public string EnvStatus { get => _envStatus; private set => Set(ref _envStatus, value); }
    private bool Ready => File.Exists(_ffmpeg) && _ffprobe is { } && File.Exists(_ffprobe);

    public void ReloadEnvironment()
    {
        _ffmpeg = FfmpegRunner.ResolveFfmpeg(_settings.FfmpegPath) ?? AppPaths.DefaultFfmpegPath;
        _ffprobe = File.Exists(_ffmpeg) ? FfmpegRunner.ResolveFfprobe(_ffmpeg) : null;

        EnvStatus = !File.Exists(_ffmpeg)
            ? "未找到 ffmpeg.exe，请先在「设置」里指定路径"
            : _ffprobe is not null
                ? $"ffmpeg 就绪：{_ffmpeg}"
                : "已找到 ffmpeg，但同目录缺少 ffprobe.exe";
    }

    // ---------- 输入 ----------

    public ObservableCollection<CompressInput> Inputs { get; } = new();

    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Inputs.Any(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            Inputs.Add(new CompressInput(p));
        }
        RefreshEstimate();
        RefreshCommands();
    }

    // ---------- 设置 ----------

    /// <summary>0=CRF 1=目标体积 2=自定义。</summary>
    public int ModeIndex { get => _modeIndex; set { if (Set(ref _modeIndex, value)) { RefreshEstimate(); RefreshPanels(); } } }
    private int _modeIndex;

    /// <summary>0=H.265（推荐） 1=H.264（兼容）。</summary>
    public int EncoderIndex { get => _encoderIndex; set { if (Set(ref _encoderIndex, value)) RefreshEstimate(); } }
    private int _encoderIndex;

    /// <summary>0=视觉无损 22 / 1=推荐 24 / 2=较小 26（x265 口径）。</summary>
    public int QualityIndex { get => _qualityIndex; set { if (Set(ref _qualityIndex, value)) RefreshEstimate(); } }
    private int _qualityIndex = 1;

    public string Preset { get => _preset; set { if (Set(ref _preset, value)) RefreshEstimate(); } }
    private string _preset = "medium";

    /// <summary>0=复制 1=AAC 96k 2=AAC 128k。</summary>
    public int AudioIndex { get => _audioIndex; set => Set(ref _audioIndex, value); }
    private int _audioIndex = 1;

    public string TargetSizeMB { get => _targetSizeMB; set { if (Set(ref _targetSizeMB, value)) RefreshEstimate(); } }
    private string _targetSizeMB = "50";

    public string CustomArgs { get => _customArgs; set => Set(ref _customArgs, value); }
    private string _customArgs = "";

    /// <summary>0=与源同目录（_c 后缀） 1=下载目录。</summary>
    public int OutputIndex { get => _outputIndex; set => Set(ref _outputIndex, value); }
    private int _outputIndex;

    public string EstimateText { get => _estimateText; private set => Set(ref _estimateText, value); }
    private string _estimateText = "";

    /// <summary>控制三块设置面板的显隐。</summary>
    public bool CrfVisible => ModeIndex == 0;
    public bool SizeVisible => ModeIndex == 1;
    public bool AdvVisible => ModeIndex == 2;

    private void RefreshPanels()
    {
        OnPropertyChanged(nameof(CrfVisible));
        OnPropertyChanged(nameof(SizeVisible));
        OnPropertyChanged(nameof(AdvVisible));
    }

    private CompressOptions BuildOptions()
    {
        return new CompressOptions
        {
            Mode = (CompressMode)Math.Clamp(ModeIndex, 0, 2),
            Encoder = EncoderIndex == 1 ? CompressEncoder.H264 : CompressEncoder.H265,
            Crf = QualityIndex switch { 0 => 22, 2 => 26, _ => 24 },
            Preset = Preset,
            Audio = (CompressAudio)Math.Clamp(AudioIndex, 0, 2),
            TargetSizeMB = long.TryParse(TargetSizeMB, out var mb) && mb > 0 ? mb : 50,
            CustomArgs = CustomArgs
        };
    }

    private string ArgsSummary(CompressOptions o) => o.Mode switch
    {
        CompressMode.Crf => $"{(o.Encoder == CompressEncoder.H265 ? "H.265" : "H.264")} · CRF {CompressService.CrfFor(o.Encoder, o.Crf):0.#} · {o.Preset}",
        CompressMode.TargetSize => $"{(o.Encoder == CompressEncoder.H265 ? "H.265" : "H.264")} · 目标 {o.TargetSizeMB} MB · 2Pass",
        _ => "自定义参数"
    };

    private void RefreshEstimate()
    {
        var total = Inputs.Sum(i => i.SizeBytes);
        if (total <= 0) { EstimateText = "添加文件后显示体积预估"; return; }

        var o = BuildOptions();
        var est = o.Mode switch
        {
            CompressMode.Crf => (long)(total * CompressService.EstimateRatio(o)),
            CompressMode.TargetSize => o.TargetSizeMB * 1024L * 1024L * Math.Max(1, Inputs.Count),
            _ => 0L
        };

        EstimateText = o.Mode switch
        {
            CompressMode.Crf => $"按当前参数估算：{FmtSize(total)} → 约 {FmtSize(est)}（省 {100 - est * 100.0 / total:F0}%）",
            CompressMode.TargetSize => $"目标体积模式：每个文件约 {o.TargetSizeMB} MB，跑两遍编码，耗时约 ×2.1",
            _ => "自定义参数不做体积预估 —— 以 ffmpeg 实际输出为准"
        };
    }

    private static string FmtSize(long b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024 / 1024:F2} GB",
        >= 1024 * 1024 => $"{b / 1024.0 / 1024:F1} MB",
        _ => $"{b / 1024.0:F0} KB"
    };

    // ---------- 队列 ----------

    public ObservableCollection<CompressTaskViewModel> Tasks { get; } = new();

    // ---------- 命令 ----------

    private ICommand? _addFilesCommand, _removeInputCommand, _clearInputsCommand, _addQueueCommand, _testCommand, _queueRemoveCommand;

    public ICommand AddFilesCommand => _addFilesCommand ??= new RelayCommand(_ => AddFilesByDialog());
    public ICommand RemoveInputCommand => _removeInputCommand ??= new RelayCommand(p =>
    {
        if (p is CompressInput i && Inputs.Remove(i)) { RefreshEstimate(); RefreshCommands(); }
    });
    public ICommand ClearInputsCommand => _clearInputsCommand ??= new RelayCommand(_ =>
    {
        Inputs.Clear(); RefreshEstimate(); RefreshCommands();
    });
    public ICommand AddQueueCommand => _addQueueCommand ??= new AsyncRelayCommand(_ => AddQueueAsync(), _ => CanQueue());
    public ICommand TestCommand => _testCommand ??= new AsyncRelayCommand(_ => TestAsync(), _ => CanQueue());

    /// <summary>队列行的按钮：排队中=移出队列，压缩中=取消，完成/失败/取消=清出行。</summary>
    public ICommand QueueRemoveCommand => _queueRemoveCommand ??= new RelayCommand(p =>
    {
        if (p is not CompressTaskViewModel t) return;
        if (t.State == CompressTaskViewModel.TaskState.Running)
        {
            try { t.TokenSource.Cancel(); } catch { }
            StatusMessage = "已请求取消：" + t.FileName;
            return;
        }
        if (t.State == CompressTaskViewModel.TaskState.Waiting)
        {
            try { t.TokenSource.Cancel(); } catch { }
        }
        Tasks.Remove(t);
        RefreshCommands();
    });

    private bool CanQueue() => Ready && Inputs.Count > 0 && Tasks.All(t => t.State != CompressTaskViewModel.TaskState.Running);

    private void RefreshCommands()
    {
        (AddQueueCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (TestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void AddFilesByDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要压缩的视频",
            Filter = "视频文件|*.mp4;*.mkv;*.mov;*.m4v;*.ts;*.flv|所有文件|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() is true) AddPaths(dlg.FileNames);
    }

    // ---------- 入队与执行 ----------

    private async Task AddQueueAsync()
    {
        if (!CanQueue()) return;
        var o = BuildOptions();

        foreach (var input in Inputs)
        {
            var output = CompressService.BuildOutputPath(input.Path,
                OutputIndex == 1 ? _settings.DownloadDir : null);
            if (File.Exists(output))
            {
                var choice = DuplicateHandler?.Invoke(new[] { System.IO.Path.GetFileName(output) }) ?? DuplicateChoice.Skip;
                if (choice == DuplicateChoice.Cancel) { StatusMessage = "已取消，未创建压缩任务"; return; }
                if (choice == DuplicateChoice.Skip) { StatusMessage = $"同名文件已存在，跳过：{System.IO.Path.GetFileName(output)}"; continue; }
                try { File.Delete(output); } catch { /* 删不掉就让 ffmpeg -y 覆盖 */ }
            }

            var task = new CompressTaskViewModel(input.Path, output, ArgsSummary(o));
            Tasks.Add(task);
        }

        StatusMessage = "已加入压缩队列（串行执行）";
        RefreshCommands();
        await RunQueueAsync();
    }

    /// <summary>串行消费队列：同一时间只有一个 ffmpeg 在跑。</summary>
    private async Task RunQueueAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            while (true)
            {
                var task = Tasks.FirstOrDefault(t => t.State == CompressTaskViewModel.TaskState.Waiting);
                if (task is null) break;
                await RunOneAsync(task);
                RefreshCommands();
            }
        }
        finally { _running = false; }
        RefreshCommands();
    }

    private async Task RunOneAsync(CompressTaskViewModel task)
    {
        var o = BuildOptions();
        var ct = task.TokenSource.Token;

        try
        {
            MediaProbe? probe;
            try
            {
                probe = await FfmpegRunner.ProbeAsync(_ffprobe!, task.InputPath, ct)
                        ?? throw new IOException("无法读取媒体信息（文件可能损坏或不是视频）");
            }
            catch (OperationCanceledException) { task.State = CompressTaskViewModel.TaskState.Cancelled; return; }

            task.State = CompressTaskViewModel.TaskState.Running;
            var srcLen = new FileInfo(task.InputPath).Length;

            if (o.Mode == CompressMode.TargetSize)
            {
                // 两遍编码：pass1 报 0~50%，pass2 报 50~100%
                var stats = Path.Combine(Path.GetTempPath(),
                    "mediadown_" + Guid.NewGuid().ToString("N")[..8] + ".stats");
                var (p1, p2, _, _) = CompressService.BuildTwoPassArgs(o, task.InputPath, task.OutputPath,
                    probe.Duration.TotalSeconds, stats);

                var half = probe.Duration.TotalSeconds;
                var r1 = await FfmpegRunner.RunAsync(_ffmpeg, p1, probe.Duration,
                    new Progress<FfmpegProgress>(p => task.Percent = p.Percent / 2), ct, "2Pass 分析");
                if (!r1.Success) throw new IOException("第一遍编码失败：" + LastLine(r1.Output));

                var r2 = await FfmpegRunner.RunAsync(_ffmpeg, p2, probe.Duration,
                    new Progress<FfmpegProgress>(p => task.Percent = 50 + p.Percent / 2), ct, "2Pass 编码");
                try { File.Delete(stats); } catch { }
                if (!r2.Success) throw new IOException("第二遍编码失败：" + LastLine(r2.Output));
            }
            else if (o.Mode == CompressMode.Custom)
            {
                var custom = CompressService.SplitCustomArgs(o.CustomArgs);
                if (custom.Length == 0) throw new IOException("自定义参数为空");

                var args = new List<string> { "-y", "-i", task.InputPath };
                args.AddRange(custom);
                if (!custom.Contains("-movflags", StringComparer.Ordinal))
                    args.AddRange(new[] { "-movflags", "+faststart" });
                args.Add(task.OutputPath);

                var r = await FfmpegRunner.RunAsync(_ffmpeg, args, probe.Duration,
                    new Progress<FfmpegProgress>(p => task.Percent = p.Percent), ct, "压缩");
                if (!r.Success) throw new IOException(LastLine(r.Output));
            }
            else
            {
                var r = await FfmpegRunner.RunAsync(_ffmpeg,
                    CompressService.BuildCrfArgs(o, task.InputPath, task.OutputPath), probe.Duration,
                    new Progress<FfmpegProgress>(p => task.Percent = p.Percent), ct, "压缩");
                if (!r.Success) throw new IOException(LastLine(r.Output));
            }

            ct.ThrowIfCancellationRequested();

            task.Percent = 100;
            var dstLen = File.Exists(task.OutputPath) ? new FileInfo(task.OutputPath).Length : 0;
            var saved = srcLen > 0 ? (1 - dstLen * 100.0 / srcLen) : 0;
            task.SizeText = $"{FmtSize(srcLen)} → {FmtSize(dstLen)}（省 {saved:F0}%）";
            task.State = CompressTaskViewModel.TaskState.Completed;
            StatusMessage = "压缩完成：" + System.IO.Path.GetFileName(task.OutputPath);
            Log.Info("compress", $"完成 {task.FileName} {FmtSize(srcLen)} -> {FmtSize(dstLen)}（省 {saved:F0}%）");
        }
        catch (OperationCanceledException)
        {
            task.State = CompressTaskViewModel.TaskState.Cancelled;
            TryDelete(task.OutputPath);
            StatusMessage = "已取消：" + task.FileName;
        }
        catch (Exception ex)
        {
            Log.Error("compress", ex);
            task.State = CompressTaskViewModel.TaskState.Failed;
            task.SizeText = ex.Message;
            StatusMessage = "压缩失败：" + ex.Message;
            TryDelete(task.OutputPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string LastLine(string ffmpegLog)
    {
        var lines = ffmpegLog.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[^1].Trim() : "ffmpeg 返回失败";
    }

    // ---------- 试压 ----------

    private async Task TestAsync()
    {
        if (!CanQueue()) return;

        var input = Inputs[0];
        StatusMessage = $"试压中：取「{input.Name}」约 1/4 处的 30 秒…";

        try
        {
            var probe = await FfmpegRunner.ProbeAsync(_ffprobe!, input.Path)
                        ?? throw new IOException("无法读取媒体信息");
            var o = BuildOptions();
            var outPath = CompressService.BuildOutputPath(input.Path).Replace("_c.mp4", "_试压30秒.mp4", StringComparison.Ordinal);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await FfmpegRunner.RunAsync(_ffmpeg,
                CompressService.BuildTestArgs(o, input.Path, outPath, probe.Duration.TotalSeconds),
                TimeSpan.FromSeconds(30), new Progress<FfmpegProgress>(p => StatusMessage = $"试压中… {p.Percent:F0}%"), default, "试压");
            if (!r.Success) throw new IOException(LastLine(r.Output));

            var len = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            Log.Info("compress", $"试压完成 {input.Name} -> {FmtSize(len)}");
            MessageBox.Show(
                $"试压片段完成（30 秒，编码耗时 {sw.Elapsed.TotalSeconds:F0} 秒）\n\n" +
                $"文件：{outPath}\n" +
                $"大小：{FmtSize(len)}（按此推算全片约 {FmtSize((long)(len * probe.Duration.TotalSeconds / 30))}）\n\n" +
                $"请播放确认画质可以接受，再点「加入压缩队列」压全片。",
                "试压完成", MessageBoxButton.OK, MessageBoxImage.Information);

            StatusMessage = "试压完成，确认画质后可加入队列";
        }
        catch (Exception ex)
        {
            Log.Error("compress", ex);
            StatusMessage = "试压失败：" + ex.Message;
            MessageBox.Show("试压失败：\n" + ex.Message, "MediaDownloader", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    private string _statusMessage = "添加视频文件后点「加入压缩队列」";

    /// <summary>关窗时停掉正在跑的编码。</summary>
    public void CancelAll()
    {
        foreach (var t in Tasks.Where(t => t.State is CompressTaskViewModel.TaskState.Waiting
                                       or CompressTaskViewModel.TaskState.Running))
        {
            try { t.TokenSource.Cancel(); } catch { }
        }
    }
}
