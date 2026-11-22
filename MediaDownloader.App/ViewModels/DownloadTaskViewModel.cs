using MediaDownloader.Core;
using System.Windows.Input;

namespace MediaDownloader.App.ViewModels;

public sealed class DownloadTaskViewModel : ObservableObject
{
    private readonly Func<Task> _startAsync;
    private readonly Action? _removeFromList;
    private long _videoBytes, _audioBytes, _videoTotal, _audioTotal;
    private double _speed;
    private DownloadState _state = DownloadState.Pending;
    private string _message = "";
    private CancellationTokenSource? _cts;

    public DownloadTaskViewModel(string title, string qualityName, string outputPath, Func<Task> startAsync,
        Action? removeFromList = null)
    {
        Title = title;
        QualityName = qualityName;
        OutputPath = outputPath;
        _startAsync = startAsync;
        _removeFromList = removeFromList;
    }

    public string Title { get; }
    public string QualityName { get; }
    public string OutputPath { get; }

    public DownloadState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StartButtonText));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(OpenVisible));
            OnPropertyChanged(nameof(PauseVisible));
            (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsCompleted => State == DownloadState.Completed;
    public bool PauseVisible => State == DownloadState.Running;
    public bool OpenVisible => State == DownloadState.Completed;
    public string StartButtonText => State == DownloadState.Completed ? "重新下载" : "继续";

    public string StateText => State switch
    {
        DownloadState.Pending => "排队中",
        DownloadState.Running => "下载中",
        DownloadState.Paused => "已暂停",
        DownloadState.Merging => "合并中",
        DownloadState.Completed => "已完成",
        DownloadState.Failed => "失败",
        DownloadState.Canceled => "已取消",
        _ => ""
    };

    public string Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    public long TotalBytes => _videoTotal + _audioTotal;

    public double Percent
    {
        get
        {
            var total = TotalBytes;
            if (total <= 0) return 0;
            return Math.Clamp((_videoBytes + _audioBytes) * 100.0 / total, 0, 100);
        }
    }

    public string SizeText
    {
        get
        {
            var total = TotalBytes;
            var got = _videoBytes + _audioBytes;
            return $"{Format(got)} / {(total > 0 ? Format(total) : "未知")}";
        }
    }

    public string SpeedText => _speed switch
    {
        >= 1024 * 1024 => $"{_speed / 1024 / 1024:F1} MB/s",
        >= 1024 => $"{_speed / 1024:F0} KB/s",
        > 0 => $"{_speed:F0} B/s",
        _ => "-"
    };

    private ICommand? _startCommand, _pauseCommand, _cancelCommand, _openCommand;

    /// <summary>只有暂停/失败/取消/完成状态才允许（重新）启动。</summary>
    private bool CanStart => State is DownloadState.Paused or DownloadState.Failed
        or DownloadState.Canceled or DownloadState.Completed;

    public ICommand StartCommand => _startCommand ??= new AsyncRelayCommand(_ => StartAsync(), _ => CanStart);
    public ICommand PauseCommand => _pauseCommand ??= new RelayCommand(_ => Pause(), _ => State == DownloadState.Running);
    public ICommand CancelCommand => _cancelCommand ??= new RelayCommand(_ => Cancel());
    public ICommand OpenCommand => _openCommand ??= new RelayCommand(_ => OpenOutput(), _ => State == DownloadState.Completed);

    /// <summary>在资源管理器里定位到成品文件。</summary>
    private void OpenOutput()
    {
        if (!System.IO.File.Exists(OutputPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"/select,\"{OutputPath}\"") { UseShellExecute = true });
        }
        catch { /* 资源管理器打不开就静默忽略 */ }
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        State = DownloadState.Running;
        Message = "";
        try { await _startAsync(); }
        catch (OperationCanceledException) { State = DownloadState.Paused; }
        catch (Exception ex) { State = DownloadState.Failed; Message = ex.Message; }
    }

    public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    public void Pause() => _cts?.Cancel();

    /// <summary>进行中=取消并移出列表；已完成=仅移出列表（文件保留）。</summary>
    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { /* 忽略 */ }
        if (State != DownloadState.Completed) State = DownloadState.Canceled;
        _removeFromList?.Invoke();
    }

    public void ReportVideo(Core.DownloadProgress p)
    {
        _videoBytes = p.ReceivedBytes;
        if (p.TotalBytes > 0) _videoTotal = p.TotalBytes;
        _speed = p.BytesPerSecond;
        Refresh();
    }

    public void ReportAudio(Core.DownloadProgress p)
    {
        _audioBytes = p.ReceivedBytes;
        if (p.TotalBytes > 0) _audioTotal = p.TotalBytes;
        Refresh();
    }

    /// <summary>YouTube 走的 yt-dlp 进度（和 DownloadEngine 的 DownloadProgress 不同源）。</summary>
    public void ReportYoutube(Core.YoutubeProgress p)
    {
        _videoBytes = p.ReceivedBytes;
        if (p.TotalBytes > 0) _videoTotal = p.TotalBytes;
        _speed = p.BytesPerSecond;
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SpeedText));
    }

    private static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B"
    };
}
