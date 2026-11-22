using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MediaDownloader.Core;
using Microsoft.Win32;

namespace MediaDownloader.App.ViewModels;

/// <summary>合并列表里的一项。</summary>
public sealed class MergeFileItem : ObservableObject
{
    public MergeFileItem(string filePath) => FilePath = filePath;

    public string FilePath { get; }
    public string Name => Path.GetFileName(FilePath);
    public string Dir => Path.GetDirectoryName(FilePath) ?? "";

    private string _info = "";
    public string Info { get => _info; set => Set(ref _info, value); }
}

public sealed class VideoToolsViewModel : ObservableObject
{
    private string _ffmpeg = "";
    private string? _ffprobe;
    private CancellationTokenSource? _cts;

    public VideoToolsViewModel() => ReloadEnvironment();

    private string _envStatus = "";

    public string EnvStatus { get => _envStatus; private set => Set(ref _envStatus, value); }

    /// <summary>重新读取设置里的 ffmpeg 路径。设置页改完路径后会调用一次。</summary>
    public void ReloadEnvironment()
    {
        var settings = Settings.Load();
        _ffmpeg = FfmpegRunner.ResolveFfmpeg(settings.FfmpegPath) ?? AppPaths.DefaultFfmpegPath;
        _ffprobe = File.Exists(_ffmpeg) ? FfmpegRunner.ResolveFfprobe(_ffmpeg) : null;

        if (!File.Exists(_ffmpeg))
            EnvStatus = "未找到 ffmpeg.exe，请先在「设置」里指定路径";
        else if (_ffprobe is null)
            EnvStatus = $"已找到 ffmpeg，但同目录缺少 ffprobe.exe：{_ffmpeg}";
        else
            EnvStatus = $"ffmpeg 就绪：{_ffmpeg}";

        OnPropertyChanged(nameof(FfmpegPath));
    }

    /// <summary>供缩略图抽帧使用。</summary>
    public string FfmpegPath => _ffmpeg;

    private bool Ready => File.Exists(_ffmpeg);

    // ================= 截取 =================

    private string _trimInput = "";
    private string _trimSourceInfo = "";
    private TimeSpan _trimSourceDuration;
    private string _trimStart = "";
    private string _trimEnd = "";
    private bool _trimPrecise;
    private string _trimOutput = "";
    private double _trimPercent;
    private string _trimStatus = "选择视频后，在时间轴上单击即可插入切点";
    private string _trimResult = "";
    private bool _isTrimBusy;

    public string TrimInput
    {
        get => _trimInput;
        set
        {
            if (!Set(ref _trimInput, value)) return;

            // 换片子了，之前的切点全部失效
            _cuts.Clear();
            Segments.Clear();
            SelectedSegment = null;
            SyncBoxesFromSelection();
            OnPropertyChanged(nameof(CutCount));
            OnPropertyChanged(nameof(SegmentsInfo));

            if (File.Exists(value))
            {
                SuggestTrimOutput();
                _ = LoadTrimSourceAsync();
            }
            else
            {
                TrimSourceInfo = "";
                TrimSourceDuration = TimeSpan.Zero;
            }
            RefreshCommands();
        }
    }

    public string TrimSourceInfo { get => _trimSourceInfo; private set => Set(ref _trimSourceInfo, value); }
    public TimeSpan TrimSourceDuration { get => _trimSourceDuration; private set => Set(ref _trimSourceDuration, value); }

    public string TrimStart
    {
        get => _trimStart;
        set
        {
            if (!Set(ref _trimStart, value)) return;
            if (!_syncingBoxes) TryMoveBoundary(isStart: true, value);
            RefreshCommands();
        }
    }

    public string TrimEnd
    {
        get => _trimEnd;
        set
        {
            if (!Set(ref _trimEnd, value)) return;
            if (!_syncingBoxes) TryMoveBoundary(isStart: false, value);
            RefreshCommands();
        }
    }

    /// <summary>false=快速（无损 copy，起点对齐关键帧）true=精确（重编码，帧级准确）。</summary>
    public bool TrimPrecise
    {
        get => _trimPrecise;
        set
        {
            if (!Set(ref _trimPrecise, value)) return;
            OnPropertyChanged(nameof(TrimModeIndex));
            OnPropertyChanged(nameof(TrimModeHint));
        }
    }

    /// <summary>绑定下拉框用：0=快速 1=精确。</summary>
    public int TrimModeIndex
    {
        get => _trimPrecise ? 1 : 0;
        set => TrimPrecise = value == 1;
    }

    public string TrimModeHint => _trimPrecise
        ? "重编码：起点终点都精确到帧，耗时较长，画质有极轻微损失"
        : "无损复制：几乎瞬间完成，但 cut 只能落在关键帧上，起点可能比设定早几秒（结束点始终准确）";

    public string TrimOutput { get => _trimOutput; set => Set(ref _trimOutput, value); }
    public double TrimPercent { get => _trimPercent; private set => Set(ref _trimPercent, value); }
    public string TrimStatus { get => _trimStatus; private set => Set(ref _trimStatus, value); }
    public string TrimResult { get => _trimResult; private set => Set(ref _trimResult, value); }
    public bool IsTrimBusy { get => _isTrimBusy; private set { if (Set(ref _isTrimBusy, value)) RefreshCommands(); } }

    // ---------- 时间轴切点与分段 ----------

    /// <summary>用户插入的切点（不含首尾；首尾由时长自动补）。</summary>
    private readonly List<TimeSpan> _cuts = new();

    /// <summary>由切点推导出来的段，段数 = 切点数 + 1。</summary>
    public ObservableCollection<TrimSegment> Segments { get; } = new();

    private TrimSegment? _selectedSegment;
    public TrimSegment? SelectedSegment
    {
        get => _selectedSegment;
        set
        {
            if (!Set(ref _selectedSegment, value)) return;
            SyncBoxesFromSelection();
            RefreshCommands();
        }
    }

    /// <summary>太短的片段直接忽略，避免误点出 0.01 秒的碎段。</summary>
    private static readonly TimeSpan MinSegment = TimeSpan.FromSeconds(0.3);

    public int CutCount => _cuts.Count;

    public string SegmentsInfo => _cuts.Count == 0
        ? "整条视频就是一段。在时间轴上单击插入切点（切 1 下分 2 段，切 2 下分 3 段）；单击切点可删除，右键撤销"
        : $"共 {Segments.Count} 段 · {_cuts.Count} 个切点 · 合计 {Segments.Sum(s => s.Length.TotalSeconds):F1} 秒（单击橙色切点可删除）";

    /// <summary>在时间轴上插入切点。</summary>
    public void InsertCut(TimeSpan t)
    {
        if (TrimSourceDuration <= TimeSpan.Zero) return;

        t = Clamp(t);

        if (t < MinSegment || TrimSourceDuration - t < MinSegment)
        {
            TrimStatus = "太靠近首尾了，往里点一点";
            return;
        }

        if (_cuts.Any(c => Math.Abs((c - t).TotalSeconds) < MinSegment.TotalSeconds))
        {
            TrimStatus = "这个位置已经有切点了";
            return;
        }

        _cuts.Add(t);
        _cuts.Sort();

        RebuildSegments(IndexOfCut(t));
        TrimStatus = $"已在 {TrimSegment.Fmt(t)} 插入切点，现在 {Segments.Count} 段";
        TrimResult = "";
    }

    /// <summary>删除离 t 最近的切点（点中已有切点时用）。</summary>
    public void RemoveCutNear(TimeSpan t)
    {
        if (_cuts.Count == 0) return;

        var index = NearestCutIndex(t);
        if (index < 0) return;

        var removed = _cuts[index];
        _cuts.RemoveAt(index);

        RebuildSegments(index);
        TrimStatus = $"已删除切点 {TrimSegment.Fmt(removed)}，现在 {Segments.Count} 段";
        TrimResult = "";
    }

    /// <summary>右键：撤销最后一个切点。</summary>
    public void UndoLastCut()
    {
        if (_cuts.Count == 0)
        {
            TrimStatus = "没有可撤销的切点";
            return;
        }

        var last = _cuts[^1];
        _cuts.RemoveAt(_cuts.Count - 1);

        RebuildSegments(_cuts.Count);
        TrimStatus = $"已撤销切点 {TrimSegment.Fmt(last)}，现在 {Segments.Count} 段";
        TrimResult = "";
    }

    private void ClearCuts()
    {
        if (_cuts.Count == 0) return;

        _cuts.Clear();
        RebuildSegments(0);
        TrimStatus = "已清除全部切点，恢复成整条一段";
        TrimResult = "";
    }

    private TimeSpan Clamp(TimeSpan t)
    {
        if (t < TimeSpan.Zero) return TimeSpan.Zero;
        if (t > TrimSourceDuration) return TrimSourceDuration;
        return t;
    }

    private int IndexOfCut(TimeSpan t) => _cuts.IndexOf(t);

    private int NearestCutIndex(TimeSpan t)
    {
        var best = -1;
        var bestDiff = double.MaxValue;

        for (var i = 0; i < _cuts.Count; i++)
        {
            var diff = Math.Abs((_cuts[i] - t).TotalSeconds);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = i;
            }
        }

        return best;
    }

    /// <summary>按切点重建分段，段数 = 切点数 + 1，首尾自动取 0 与总时长。</summary>
    private void RebuildSegments(int selectIndex)
    {
        Segments.Clear();

        if (TrimSourceDuration <= TimeSpan.Zero)
        {
            SelectedSegment = null;
            OnPropertyChanged(nameof(CutCount));
            OnPropertyChanged(nameof(SegmentsInfo));
            RefreshCommands();
            return;
        }

        var boundaries = new List<TimeSpan> { TimeSpan.Zero };
        boundaries.AddRange(_cuts);
        boundaries.Add(TrimSourceDuration);

        for (var i = 0; i < boundaries.Count - 1; i++)
            Segments.Add(new TrimSegment(boundaries[i], boundaries[i + 1]) { Index = i + 1 });

        SelectedSegment = Segments.Count == 0
            ? null
            : Segments[Math.Clamp(selectIndex, 0, Segments.Count - 1)];

        OnPropertyChanged(nameof(CutCount));
        OnPropertyChanged(nameof(SegmentsInfo));
        RefreshCommands();
    }

    // ---------- 起点/终点输入框（跟随选中段，改一下就移动相邻切点） ----------

    private bool _syncingBoxes;

    private void SyncBoxesFromSelection()
    {
        _syncingBoxes = true;
        try
        {
            TrimStart = _selectedSegment is null ? "" : TrimSegment.Fmt(_selectedSegment.Start);
            TrimEnd = _selectedSegment is null ? "" : TrimSegment.Fmt(_selectedSegment.End);
        }
        finally
        {
            _syncingBoxes = false;
        }
    }

    /// <summary>
    /// 手动改起点/终点：首段的起点、末段的终点是固定边界（0 和总时长）不能改，
    /// 中间边界改的是对应的那个切点。
    /// </summary>
    private void TryMoveBoundary(bool isStart, string text)
    {
        var seg = _selectedSegment;
        if (seg is null || _cuts.Count == 0) return;

        if (!FfmpegRunner.TryParseTime(text, out var t))
        {
            SyncBoxesFromSelection();
            return;
        }

        var segIndex = Segments.IndexOf(seg);
        if (segIndex < 0) return;

        if (isStart && segIndex == 0) { SyncBoxesFromSelection(); return; }
        if (!isStart && segIndex == Segments.Count - 1) { SyncBoxesFromSelection(); return; }

        var cutIndex = isStart ? segIndex - 1 : segIndex;

        var lower = cutIndex - 1 >= 0 ? _cuts[cutIndex - 1] : TimeSpan.Zero;
        var upper = cutIndex + 1 < _cuts.Count ? _cuts[cutIndex + 1] : TrimSourceDuration;

        if (t <= lower + MinSegment || t >= upper - MinSegment)
        {
            SyncBoxesFromSelection();
            return;
        }

        _cuts[cutIndex] = t;
        _cuts.Sort();
        RebuildSegments(Segments.IndexOf(seg));
    }

    private ICommand? _undoCutCommand, _clearCutsCommand;

    public ICommand UndoCutCommand => _undoCutCommand ??=
        new RelayCommand(_ => UndoLastCut(), _ => _cuts.Count > 0 && !IsTrimBusy);

    public ICommand ClearCutsCommand => _clearCutsCommand ??=
        new RelayCommand(_ => ClearCuts(), _ => _cuts.Count > 0 && !IsTrimBusy);

    private async Task LoadTrimSourceAsync()
    {
        if (_ffprobe is null)
        {
            TrimSourceInfo = "缺少 ffprobe，无法读取时长";
            return;
        }

        var probe = await FfmpegRunner.ProbeAsync(_ffprobe, TrimInput);
        if (probe is null)
        {
            TrimSourceInfo = "读取失败（可能不是视频文件）";
            return;
        }

        TrimSourceDuration = probe.Duration;
        TrimSourceInfo = $"时长 {probe.Duration:hh\\:mm\\:ss\\.f} · {probe.Describe()} · " +
                         $"{probe.SizeBytes / 1024.0 / 1024.0:F1} MB";

        // 载入后默认就是整条一段
        _cuts.Clear();
        RebuildSegments(0);
        TrimStatus = "已完成载入，在时间轴上单击即可插入切点";
        RefreshCommands();
    }

    private void SuggestTrimOutput()
    {
        if (string.IsNullOrWhiteSpace(TrimInput)) return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(TrimInput)) ?? "";
        var name = Path.GetFileNameWithoutExtension(TrimInput);
        TrimOutput = Path.Combine(dir, name + "_截取.mp4");
    }

    private ICommand? _browseTrimInputCommand, _trimCommand, _cancelTrimCommand;

    public ICommand BrowseTrimInputCommand => _browseTrimInputCommand ??= new RelayCommand(_ => BrowseTrimInput());
    public ICommand TrimCommand => _trimCommand ??= new AsyncRelayCommand(_ => TrimAsync(), _ => CanTrim());
    public ICommand CancelTrimCommand => _cancelTrimCommand ??= new RelayCommand(_ => _cts?.Cancel(), _ => IsTrimBusy);

    private bool CanTrim()
    {
        if (!Ready || IsTrimBusy) return false;
        if (!File.Exists(TrimInput)) return false;
        if (string.IsNullOrWhiteSpace(TrimOutput)) return false;
        return Segments.Count > 0;
    }

    private void BrowseTrimInput()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择视频文件",
            Filter = "视频文件|*.mp4;*.mkv;*.flv;*.mov;*.ts;*.webm;*.avi|所有文件|*.*"
        };
        if (dlg.ShowDialog() == true) TrimInput = dlg.FileName;
    }

    private async Task TrimAsync()
    {
        if (Segments.Count == 0)
        {
            TrimStatus = "还没有可截取的区间，先选择一个视频";
            return;
        }

        // 切点把整条分成了 N 段，按顺序全部输出
        var jobs = Segments.Select(s => (Start: s.Start, End: s.End)).ToList();

        var targets = BuildOutputs(jobs.Count);
        if (targets.Count != jobs.Count)
        {
            TrimStatus = "请填写输出文件路径";
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsTrimBusy = true;
        TrimPercent = 0;
        TrimResult = "";
        TrimStatus = TrimPrecise ? "精确截取（重编码）…" : "快速截取（无损复制）…";

        var total = jobs.Count;
        var done = 0;
        var lines = new List<string>();

        try
        {
            for (var i = 0; i < total; i++)
            {
                var (from, to) = jobs[i];
                var index = i;

                var progress = new Progress<FfmpegProgress>(p =>
                {
                    // 总体进度按段数加权，单段内部按百分比
                    TrimPercent = (index + p.Percent / 100.0) / total * 100;
                    TrimStatus = total == 1
                        ? $"{p.Stage} {TrimSegment.Fmt(p.Position)} / {TrimSegment.Fmt(p.Duration)}"
                        : $"[{index + 1}/{total}] {p.Stage} {TrimSegment.Fmt(p.Position)} / {TrimSegment.Fmt(p.Duration)}";
                });

                var result = await VideoTrimmer.TrimAsync(_ffmpeg, _ffprobe, TrimInput, targets[i],
                    from, to, TrimPrecise, progress, ct);

                if (result.Success) done++;

                var body = result.Success ? result.Summary : "失败：" + Tail(result.Output);
                lines.Add(total == 1
                    ? body
                    : $"{i + 1}. {TrimSegment.Fmt(from)} → {TrimSegment.Fmt(to)}　{body}");
            }

            TrimPercent = 100;
            TrimStatus = done == total ? $"完成（{total} 段）" : $"完成 {done}/{total} 段，有失败";
            TrimResult = string.Join("\n", lines);
        }
        catch (OperationCanceledException)
        {
            TrimStatus = $"已取消（已完成 {done}/{total} 段）";
            TrimResult = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            TrimStatus = "出错：" + ex.Message;
        }
        finally
        {
            IsTrimBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>只有一段时直接写 TrimOutput；多段则以它为基名加 _01/_02，扩展名不变。</summary>
    private List<string> BuildOutputs(int count)
    {
        var result = new List<string>();
        if (count <= 0 || string.IsNullOrWhiteSpace(TrimOutput)) return result;

        if (count == 1)
        {
            result.Add(TrimOutput);
            return result;
        }

        var full = Path.GetFullPath(TrimOutput);
        var dir = Path.GetDirectoryName(full) ?? "";
        var name = Path.GetFileNameWithoutExtension(full);
        var ext = Path.GetExtension(full);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";

        for (var i = 1; i <= count; i++)
            result.Add(Path.Combine(dir, $"{name}_{i:00}{ext}"));

        return result;
    }

    // ================= 合并 =================

    private string _mergeCheckText = "添加至少两个视频后自动校验参数是否一致";
    private bool _mergeCanCopy;
    private TimeSpan _mergeTotal;
    private string _mergeOutput = "";
    private double _mergePercent;
    private string _mergeStatus = "";
    private string _mergeResult = "";
    private bool _isMergeBusy;
    private MergeFileItem? _selectedMergeFile;

    public ObservableCollection<MergeFileItem> MergeFiles { get; } = new();

    public MergeFileItem? SelectedMergeFile
    {
        get => _selectedMergeFile;
        set { if (Set(ref _selectedMergeFile, value)) RefreshCommands(); }
    }

    public string MergeCheckText { get => _mergeCheckText; private set => Set(ref _mergeCheckText, value); }

    /// <summary>参数一致、可无损拼接。页面据此把校验提示条切成绿/红两种配色。</summary>
    public bool MergeCanCopy { get => _mergeCanCopy; private set => Set(ref _mergeCanCopy, value); }
    public TimeSpan MergeTotal => _mergeTotal;
    public string MergeOutput { get => _mergeOutput; set { if (Set(ref _mergeOutput, value)) RefreshCommands(); } }
    public double MergePercent { get => _mergePercent; private set => Set(ref _mergePercent, value); }
    public string MergeStatus { get => _mergeStatus; private set => Set(ref _mergeStatus, value); }
    public string MergeResult { get => _mergeResult; private set => Set(ref _mergeResult, value); }
    public bool IsMergeBusy { get => _isMergeBusy; private set { if (Set(ref _isMergeBusy, value)) RefreshCommands(); } }

    private ICommand? _addMergeFilesCommand, _removeMergeFileCommand, _mergeUpCommand, _mergeDownCommand,
        _mergeCommand, _cancelMergeCommand, _recheckMergeCommand;

    public ICommand AddMergeFilesCommand => _addMergeFilesCommand ??= new AsyncRelayCommand(_ => AddMergeFilesAsync(), _ => !IsMergeBusy);
    public ICommand RemoveMergeFileCommand => _removeMergeFileCommand ??= new AsyncRelayCommand(_ => RemoveMergeFileAsync(), _ => !IsMergeBusy && SelectedMergeFile is not null);
    public ICommand MergeUpCommand => _mergeUpCommand ??= new AsyncRelayCommand(_ => MoveMergeFileAsync(-1), _ => !IsMergeBusy && SelectedMergeFile is not null);
    public ICommand MergeDownCommand => _mergeDownCommand ??= new AsyncRelayCommand(_ => MoveMergeFileAsync(1), _ => !IsMergeBusy && SelectedMergeFile is not null);
    public ICommand MergeCommand => _mergeCommand ??= new AsyncRelayCommand(_ => MergeAsync(), _ => CanMerge());
    public ICommand CancelMergeCommand => _cancelMergeCommand ??= new RelayCommand(_ => _cts?.Cancel(), _ => IsMergeBusy);
    public ICommand RecheckMergeCommand => _recheckMergeCommand ??= new AsyncRelayCommand(_ => CheckMergeAsync(), _ => !IsMergeBusy);

    private bool CanMerge()
        => Ready && !IsMergeBusy && _mergeCanCopy && MergeFiles.Count >= 2 && !string.IsNullOrWhiteSpace(MergeOutput);

    private async Task AddMergeFilesAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要合并的视频（可多选）",
            Multiselect = true,
            Filter = "视频文件|*.mp4;*.mkv;*.flv;*.mov;*.ts;*.webm;*.avi|所有文件|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        foreach (var f in dlg.FileNames)
        {
            if (MergeFiles.Any(x => string.Equals(x.FilePath, f, StringComparison.OrdinalIgnoreCase))) continue;
            MergeFiles.Add(new MergeFileItem(f));
        }

        if (string.IsNullOrWhiteSpace(MergeOutput) && MergeFiles.Count > 0)
        {
            var dir = Path.GetDirectoryName(MergeFiles[0].FilePath) ?? "";
            MergeOutput = Path.Combine(dir, "合并结果.mp4");
        }

        await CheckMergeAsync();
    }

    private async Task RemoveMergeFileAsync()
    {
        if (SelectedMergeFile is null) return;
        MergeFiles.Remove(SelectedMergeFile);
        SelectedMergeFile = MergeFiles.FirstOrDefault();
        await CheckMergeAsync();
    }

    private async Task MoveMergeFileAsync(int delta)
    {
        var item = SelectedMergeFile;
        if (item is null) return;
        var i = MergeFiles.IndexOf(item);
        var j = i + delta;
        if (j < 0 || j >= MergeFiles.Count) return;
        MergeFiles.Move(i, j);
        SelectedMergeFile = item;
        await CheckMergeAsync();
    }

    public async Task CheckMergeAsync()
    {
        MergeResult = "";
        MergePercent = 0;
        MergeStatus = "";

        if (MergeFiles.Count == 0)
        {
            MergeCanCopy = false;
            _mergeTotal = TimeSpan.Zero;
            MergeCheckText = "添加至少两个视频后自动校验参数是否一致";
            RefreshCommands();
            return;
        }

        if (_ffprobe is null)
        {
            MergeCanCopy = false;
            MergeCheckText = "缺少 ffprobe.exe，无法校验参数（需与 ffmpeg 放在同一目录）";
            RefreshCommands();
            return;
        }

        foreach (var item in MergeFiles)
        {
            var p = await FfmpegRunner.ProbeAsync(_ffprobe, item.FilePath);
            item.Info = p is null ? "无法读取" : p.Describe();
        }

        var check = await VideoMerger.CheckAsync(_ffprobe, MergeFiles.Select(x => x.FilePath).ToList());
        MergeCanCopy = check.CanCopy;
        _mergeTotal = check.TotalDuration;
        MergeCheckText = check.Message;
        RefreshCommands();
    }

    private async Task MergeAsync()
    {
        if (!_mergeCanCopy)
        {
            MergeStatus = "当前文件参数不一致，无法无损合并";
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsMergeBusy = true;
        MergePercent = 0;
        MergeResult = "";
        MergeStatus = "合并中…";

        try
        {
            var progress = new Progress<FfmpegProgress>(p =>
            {
                MergePercent = p.Percent;
                MergeStatus = $"{p.Stage} {p.Position:hh\\:mm\\:ss} / {p.Duration:hh\\:mm\\:ss}";
            });

            var inputs = MergeFiles.Select(x => x.FilePath).ToList();
            var run = await VideoMerger.MergeAsync(_ffmpeg, inputs, MergeOutput, _mergeTotal, progress, _cts.Token);

            MergePercent = run.Success ? 100 : MergePercent;
            MergeStatus = run.Success ? "完成" : "失败，详见 ffmpeg 输出";
            if (run.Success && _ffprobe is not null)
            {
                var p = await FfmpegRunner.ProbeAsync(_ffprobe, MergeOutput);
                MergeResult = p is null ? "合并完成" :
                    $"合并完成：{inputs.Count} 个文件 → {p.Duration:hh\\:mm\\:ss}（{p.Describe()}）";
            }
            else if (!run.Success)
            {
                MergeResult = Tail(run.Output);
            }
        }
        catch (OperationCanceledException)
        {
            MergeStatus = "已取消";
        }
        catch (Exception ex)
        {
            MergeStatus = "出错：" + ex.Message;
        }
        finally
        {
            IsMergeBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ================= 音视分离 =================

    private string _sepInput = "";
    private string _sepSourceInfo = "";
    private TimeSpan _sepDuration;
    private bool _sepHasAudio;
    private int _sepAudioFormat;
    private string _sepAudioOutput = "";
    private string _sepVideoOutput = "";
    private double _sepPercent;
    private string _sepStatus = "选择视频后点「开始分离」，会同时生成音频和无声视频";
    private string _sepResult = "";
    private bool _isSepBusy;

    public string SepInput
    {
        get => _sepInput;
        set
        {
            if (!Set(ref _sepInput, value)) return;
            if (File.Exists(value))
            {
                SuggestSepOutputs();
                _ = LoadSepSourceAsync();
            }
            else
            {
                SepSourceInfo = "";
                _sepDuration = TimeSpan.Zero;
            }
            RefreshCommands();
        }
    }

    public string SepSourceInfo { get => _sepSourceInfo; private set => Set(ref _sepSourceInfo, value); }
    public double SepPercent { get => _sepPercent; private set => Set(ref _sepPercent, value); }
    public string SepStatus { get => _sepStatus; private set => Set(ref _sepStatus, value); }
    public string SepResult { get => _sepResult; private set => Set(ref _sepResult, value); }
    public bool IsSepBusy { get => _isSepBusy; private set { if (Set(ref _isSepBusy, value)) RefreshCommands(); } }

    /// <summary>音频格式：0=mp3（默认）1=m4a 原样复制音轨。</summary>
    public int SepAudioFormat
    {
        get => _sepAudioFormat;
        set
        {
            if (!Set(ref _sepAudioFormat, value)) return;
            if (File.Exists(SepInput)) SuggestSepOutputs();
        }
    }

    public string SepAudioOutput
    {
        get => _sepAudioOutput;
        set { if (Set(ref _sepAudioOutput, value)) RefreshCommands(); }
    }

    public string SepVideoOutput
    {
        get => _sepVideoOutput;
        set { if (Set(ref _sepVideoOutput, value)) RefreshCommands(); }
    }

    private AudioMode CurrentAudioMode => _sepAudioFormat == 1 ? AudioMode.ExtractM4a : AudioMode.ExtractMp3;

    private async Task LoadSepSourceAsync()
    {
        if (_ffprobe is null)
        {
            SepSourceInfo = "缺少 ffprobe，无法读取时长";
            return;
        }

        var probe = await FfmpegRunner.ProbeAsync(_ffprobe, SepInput);
        if (probe is null)
        {
            SepSourceInfo = "读取失败（可能不是视频文件）";
            return;
        }

        _sepDuration = probe.Duration;
        _sepHasAudio = probe.HasAudio;
        SepSourceInfo = $"时长 {probe.Duration:hh\\:mm\\:ss\\.f} · {probe.Describe()}";

        SepStatus = probe.HasAudio
            ? "就绪：会生成音频文件 + 无声视频文件"
            : "该文件没有音轨，只能生成无声视频";

        RefreshCommands();
    }

    private void SuggestSepOutputs()
    {
        if (string.IsNullOrWhiteSpace(SepInput) || !File.Exists(SepInput)) return;

        var (video, audio) = AudioSeparator.SuggestSplitOutputs(SepInput, CurrentAudioMode);
        SepVideoOutput = video;
        SepAudioOutput = audio;
    }

    private ICommand? _browseSepInputCommand, _sepCommand, _cancelSepCommand,
        _openTrimFolderCommand, _openMergeFolderCommand, _openSepFolderCommand;

    public ICommand BrowseSepInputCommand => _browseSepInputCommand ??= new RelayCommand(_ => BrowseSepInput());
    public ICommand SepCommand => _sepCommand ??= new AsyncRelayCommand(_ => SepAsync(), _ => CanSep());
    public ICommand CancelSepCommand => _cancelSepCommand ??= new RelayCommand(_ => _cts?.Cancel(), _ => IsSepBusy);
    public ICommand OpenTrimFolderCommand => _openTrimFolderCommand ??= new RelayCommand(_ => OpenOutputFolder(TrimOutput));
    public ICommand OpenMergeFolderCommand => _openMergeFolderCommand ??= new RelayCommand(_ => OpenOutputFolder(MergeOutput));
    public ICommand OpenSepFolderCommand => _openSepFolderCommand ??= new RelayCommand(_ => OpenOutputFolder(SepVideoOutput));

    private bool CanSep()
        => Ready && !IsSepBusy && File.Exists(SepInput)
           && !string.IsNullOrWhiteSpace(SepVideoOutput)
           && (!_sepHasAudio || !string.IsNullOrWhiteSpace(SepAudioOutput));

    private void BrowseSepInput()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择视频文件",
            Filter = "视频文件|*.mp4;*.mkv;*.flv;*.mov;*.ts;*.webm;*.avi|所有文件|*.*"
        };
        if (dlg.ShowDialog() == true) SepInput = dlg.FileName;
    }

    private async Task SepAsync()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsSepBusy = true;
        SepPercent = 0;
        SepResult = "";
        SepStatus = "处理中…";

        try
        {
            var progress = new Progress<FfmpegProgress>(p =>
            {
                SepPercent = p.Percent;
                SepStatus = $"{p.Stage}　{p.Position:mm\\:ss} / {p.Duration:mm\\:ss}";
            });

            var duration = _sepDuration;
            if (duration <= TimeSpan.Zero && _ffprobe is not null)
                duration = (await FfmpegRunner.ProbeAsync(_ffprobe, SepInput))?.Duration ?? TimeSpan.Zero;

            var result = await AudioSeparator.SplitAsync(_ffmpeg, SepInput,
                SepVideoOutput, SepAudioOutput, CurrentAudioMode, duration, _sepHasAudio, progress, ct);

            SepPercent = result.Success ? 100 : SepPercent;
            SepStatus = result.Success ? "完成" : result.Message;

            if (result.Success)
            {
                var lines = new List<string> { result.Message };
                if (result.AudioDone) lines.Add("音频：" + result.AudioOutput);
                if (result.VideoDone) lines.Add("视频（已去音轨）：" + result.VideoOutput);
                SepResult = string.Join("\n", lines);
            }
            else
            {
                SepResult = Tail(result.Output);
            }
        }
        catch (OperationCanceledException)
        {
            SepStatus = "已取消";
        }
        catch (Exception ex)
        {
            SepStatus = "出错：" + ex.Message;
        }
        finally
        {
            IsSepBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ================= 公共 =================

    private void RefreshCommands()
    {
        (TrimCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelTrimCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UndoCutCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearCutsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MergeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelMergeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveMergeFileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (MergeUpCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (MergeDownCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddMergeFilesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RecheckMergeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SepCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelSepCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void CancelAll()
    {
        try { _cts?.Cancel(); } catch { /* 忽略 */ }
    }

    /// <summary>
    /// 打开输出位置。传文件路径或目录都行：
    /// 文件已存在就定位选中它，否则直接打开所在目录（注意不能对目录用 /select，
    /// 那样 Explorer 会打开它的父目录，看起来就像没打开）。
    /// </summary>
    public void OpenOutputFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            string dir;
            var isFile = false;

            if (Directory.Exists(path))
            {
                dir = path;
            }
            else
            {
                dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
                isFile = File.Exists(path);
            }

            if (string.IsNullOrWhiteSpace(dir)) return;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var psi = isFile
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"")
                : new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"");

            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // 兜底：交给系统默认方式打开目录
            try
            {
                var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { /* 忽略 */ }
        }
    }

    private static string Tail(string log)
    {
        if (string.IsNullOrWhiteSpace(log)) return "(无输出)";
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? log : lines[^1].Trim();
    }
}
