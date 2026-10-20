namespace Kei.Term.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;

public enum FileTransferDirection
{
    Upload,
    Download
}

public enum FileTransferState
{
    Pending,
    Transferring,
    Completed,
    Failed,
    Cancelled,
    AwaitingConflict,
    RetryWait,
    Skipped,
    Packing,
    Verifying,
    Unpacking,
}

public partial class FileTransferTaskItemViewModel : ViewModelBase
{
    public Guid Id { get; } = Guid.NewGuid();
    public string FileName { get; }
    public string LocalPath { get; }
    public string RemotePath { get; }
    public FileTransferDirection Direction { get; }

    [ObservableProperty]
    private FileTransferState _state = FileTransferState.Pending;

    [ObservableProperty]
    private bool _isUnverified;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _statusText = "等待中...";

    [ObservableProperty]
    private double _speedMBs;

    [ObservableProperty]
    private long _transferredBytes;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private FileSizeDisplayMode _sizeDisplayMode = FileSizeDisplayMode.Iec;

    // 正在搬运数据的状态；只有这些状态显示即时进度。
    private bool IsActive => State is FileTransferState.Transferring or FileTransferState.Packing
        or FileTransferState.Verifying or FileTransferState.Unpacking;

    // 进度条/百分比：进行中，或失败/取消时留有部分进度。完成、排队等状态留空以减少噪音。
    public bool ShowsProgress => IsActive || (State is FileTransferState.Failed or FileTransferState.Cancelled && Progress > 0);

    // 格式化后的传输大小文字：进行中/失败为"已传 / 总"，完成为总大小，其余有总大小则显示总大小。
    public string FormattedSizeText
    {
        get
        {
            if (TotalBytes <= 0 && TransferredBytes <= 0) return "—";
            if (State == FileTransferState.Completed)
            {
                return FileSizeFormatter.Format(TotalBytes > 0 ? TotalBytes : TransferredBytes, SizeDisplayMode);
            }

            if (TotalBytes <= 0) return FileSizeFormatter.Format(TransferredBytes, SizeDisplayMode);
            string total = FileSizeFormatter.Format(TotalBytes, SizeDisplayMode);
            if (!ShowsProgress) return total;
            return $"{FileSizeFormatter.Format(TransferredBytes, SizeDisplayMode)} / {total}";
        }
    }

    // 百分比格式化文本（例如："36%"）；向下取整，保证只有真正完成才会显示 100%
    public string FormattedProgressText => ShowsProgress ? $"{(int)Math.Floor(Math.Clamp(Progress, 0, 100))}%" : "";

    // 速度格式化文本（例如："1.5 MB/s"）；仅传输中显示即时速度
    public string FormattedSpeedText => State == FileTransferState.Transferring && SpeedMBs > 0.01 ? $"{SpeedMBs:F1} MB/s" : "";

    // 预估剩余时间格式化文本（例如："00:15"）
    public string FormattedRemainingTimeText
    {
        get
        {
            if (State != FileTransferState.Transferring || SpeedMBs <= 0.01 || TotalBytes <= TransferredBytes) return "—";
            double remainingBytes = TotalBytes - TransferredBytes;
            double remainingSeconds = remainingBytes / (SpeedMBs * 1024.0 * 1024.0);
            if (remainingSeconds < 0 || double.IsInfinity(remainingSeconds) || double.IsNaN(remainingSeconds)) return "—";
            TimeSpan time = TimeSpan.FromSeconds(Math.Min(remainingSeconds, 86400 * 7));
            return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}" : $"{time.Minutes:D2}:{time.Seconds:D2}";
        }
    }

    // 完整路径流向提示（例如："C:\local\file.txt → /home/kei/file.txt"）
    public string TransferPathTooltip => Direction == FileTransferDirection.Upload
        ? $"{LocalPath} → {RemotePath}"
        : $"{RemotePath} → {LocalPath}";

    public CancellationTokenSource Cts { get; } = new();

    public Action<FileTransferTaskItemViewModel>? RemoveRequested { get; set; }
    public Func<string, Task>? NavigateDirectoryRequested { get; set; }

    private readonly Queue<(DateTimeOffset Time, long Bytes)> _samples = new();
    private TransferQueue? _queue;
    private Guid? _queueTaskId;

    public FileTransferTaskItemViewModel(
        string fileName,
        string localPath,
        string remotePath,
        FileTransferDirection direction)
    {
        FileName = fileName;
        LocalPath = localPath;
        RemotePath = remotePath;
        Direction = direction;
    }

    public FileTransferTaskItemViewModel(QueuedTransfer task)
        : this(
            task.Name,
            task.LocalPath,
            task.RemotePath,
            task.Direction == TransferDirection.Upload ? FileTransferDirection.Upload : FileTransferDirection.Download)
    {
        _queueTaskId = task.Id;
    }

    public void Bind(TransferQueue queue) => _queue = queue;

    partial void OnSizeDisplayModeChanged(FileSizeDisplayMode value) => OnPropertyChanged(nameof(FormattedSizeText));
    partial void OnTransferredBytesChanged(long value) => OnPropertyChanged(nameof(FormattedSizeText));
    partial void OnTotalBytesChanged(long value) => OnPropertyChanged(nameof(FormattedSizeText));
    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(FormattedProgressText));
        OnPropertyChanged(nameof(ShowsProgress));
    }

    partial void OnStateChanged(FileTransferState value)
    {
        OnPropertyChanged(nameof(ShowsProgress));
        OnPropertyChanged(nameof(FormattedSizeText));
        OnPropertyChanged(nameof(FormattedProgressText));
        OnPropertyChanged(nameof(FormattedSpeedText));
        OnPropertyChanged(nameof(FormattedRemainingTimeText));
    }

    partial void OnSpeedMBsChanged(double value)
    {
        OnPropertyChanged(nameof(FormattedSpeedText));
        OnPropertyChanged(nameof(FormattedRemainingTimeText));
    }

    public void NoteProgress(long transferred, long total)
    {
        TransferredBytes = transferred;
        TotalBytes = total;
        Progress = total <= 0
            ? State is FileTransferState.Completed or FileTransferState.Skipped ? 100 : 0
            : Math.Min(100, transferred * 100.0 / total);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _samples.Enqueue((now, transferred));
        while (_samples.Count > 2 && now - _samples.Peek().Time > TimeSpan.FromSeconds(1))
        {
            _samples.Dequeue();
        }

        (DateTimeOffset time, long bytes) = _samples.Peek();
        double seconds = Math.Max(0.05, (now - time).TotalSeconds);
        SpeedMBs = Math.Max(0, transferred - bytes) / (1024.0 * 1024.0) / seconds;
    }

    [RelayCommand]
    public void Retry()
    {
        if (_queue != null && _queueTaskId is Guid id) _queue.Retry(id);
    }

    [RelayCommand]
    public void Cancel()
    {
        if (State is FileTransferState.Pending or FileTransferState.Transferring
            or FileTransferState.AwaitingConflict or FileTransferState.RetryWait
            or FileTransferState.Packing or FileTransferState.Verifying or FileTransferState.Unpacking)
        {
            if (_queue != null && _queueTaskId is Guid id)
            {
                _queue.Cancel(id);
                return;
            }

            Cts.Cancel();
            State = FileTransferState.Cancelled;
            StatusText = "已取消";
            return;
        }

        RemoveRequested?.Invoke(this);
    }

    [RelayCommand]
    public void Remove() => RemoveRequested?.Invoke(this);

    [RelayCommand]
    public async Task NavigateToRemoteDirectory()
    {
        if (NavigateDirectoryRequested == null || string.IsNullOrWhiteSpace(RemotePath)) return;
        // 若为上传，RemotePath 是目标目录；若为下载，RemotePath 是源文件，需取其父目录
        string targetDirectory = Direction == FileTransferDirection.Upload
            ? RemotePath
            : RemotePath.Contains('/') ? RemotePath[..Math.Max(1, RemotePath.LastIndexOf('/'))] : "/";
        await NavigateDirectoryRequested.Invoke(targetDirectory);
    }
}
