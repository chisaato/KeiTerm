namespace Kei.Term.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.Core.Models;
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

    public CancellationTokenSource Cts { get; } = new();

    public Action<FileTransferTaskItemViewModel>? RemoveRequested { get; set; }

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
            task.Direction == TransferDirection.Download ? task.LocalPath : task.LocalPath,
            task.Direction == TransferDirection.Download ? task.RemotePath : task.RemotePath,
            task.Direction == TransferDirection.Upload ? FileTransferDirection.Upload : FileTransferDirection.Download)
    {
        _queueTaskId = task.Id;
    }

    public void Bind(TransferQueue queue) => _queue = queue;

    public void NoteProgress(long transferred, long total)
    {
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
}
