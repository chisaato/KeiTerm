namespace Kei.Term.App.ViewModels;

using System;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    Cancelled
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
    private double _progress;

    [ObservableProperty]
    private string _statusText = "等待中...";

    [ObservableProperty]
    private double _speedMBs;

    public CancellationTokenSource Cts { get; } = new();

    public Action<FileTransferTaskItemViewModel>? RemoveRequested { get; set; }

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

    [RelayCommand]
    public void Cancel()
    {
        if (State is FileTransferState.Pending or FileTransferState.Transferring)
        {
            Cts.Cancel();
            State = FileTransferState.Cancelled;
            StatusText = "已取消";
        }
        else
        {
            RemoveRequested?.Invoke(this);
        }
    }
}
