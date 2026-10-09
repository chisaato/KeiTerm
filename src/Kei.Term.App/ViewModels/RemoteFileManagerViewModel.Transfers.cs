namespace Kei.Term.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Kei.Term.App.Services;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;

public partial class RemoteFileManagerViewModel
{
    [RelayCommand]
    public async Task DownloadToDesktopAsync(RemoteFileItem? singleItem)
    {
        var targets = (SelectedItems.Count > 0)
            ? SelectedItems.Where(x => !x.IsDirectory && x.Name != "..").ToList()
            : (singleItem != null && !singleItem.IsDirectory && singleItem.Name != ".." ? [singleItem] : []);

        if (targets.Count == 0) return;

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
        {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        ShowFileActivities(0);

        foreach (var item in targets)
        {
            string localTarget = Path.Combine(desktop, item.Name);
            var taskVm = new FileTransferTaskItemViewModel(item.Name, localTarget, item.FullPath, FileTransferDirection.Download);
            taskVm.RemoveRequested = t => TransferTasks.Remove(t);
            TransferTasks.Add(taskVm);

            StartOperation(() => ExecuteDownloadTaskAsync(taskVm, item, localTarget));
        }
    }

    private async Task ExecuteDownloadTaskAsync(FileTransferTaskItemViewModel taskVm, RemoteFileItem item, string localTarget)
    {
        using CancellationTokenSource transferCts = CancellationTokenSource.CreateLinkedTokenSource(taskVm.Cts.Token, _lifetimeCts.Token);
        CancellationToken ct = transferCts.Token;
        taskVm.State = FileTransferState.Transferring;
        taskVm.StatusText = "下载中...";
        IsTransferring = true;

        try
        {
            await using var remoteStream = await _fileSystem.OpenReadAsync(item.FullPath, ct);
            await using var localStream = new FileStream(localTarget, FileMode.Create, FileAccess.Write);

            long totalBytes = item.Size;
            long transferred = 0;
            byte[] buffer = new byte[64 * 1024];
            int read;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while ((read = await remoteStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await localStream.WriteAsync(buffer.AsMemory(0, read), ct);
                transferred += read;
                if (totalBytes > 0)
                {
                    taskVm.Progress = Math.Min(100.0, (double)transferred / totalBytes * 100.0);
                    double elapsedSec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    taskVm.SpeedMBs = (transferred / (1024.0 * 1024.0)) / elapsedSec;
                    taskVm.StatusText = $"{taskVm.Progress:F1}% ({taskVm.SpeedMBs:F1} MB/s)";
                }
            }

            taskVm.Progress = 100;
            taskVm.State = FileTransferState.Completed;
            taskVm.StatusText = "完成";
            TransferStatusMessage = $"已下载: {item.Name}";
        }
        catch (OperationCanceledException)
        {
            taskVm.State = FileTransferState.Cancelled;
            taskVm.StatusText = "已取消";
            if (File.Exists(localTarget))
            {
                try { File.Delete(localTarget); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载任务失败: {Name}", item.Name);
            taskVm.State = FileTransferState.Failed;
            taskVm.StatusText = $"失败: {ex.Message}";
        }
        finally
        {
            UpdateOverallTransferringState();
        }
    }

    [RelayCommand]
    public async Task UploadLocalFilesAsync(string[] filePaths)
    {
        if (filePaths == null || filePaths.Length == 0) return;

        ShowFileActivities(0);

        foreach (var localPath in filePaths)
        {
            if (!File.Exists(localPath)) continue;

            string fileName = Path.GetFileName(localPath);
            string remotePath = CurrentPath.TrimEnd('/') + "/" + fileName;

            var taskVm = new FileTransferTaskItemViewModel(fileName, localPath, remotePath, FileTransferDirection.Upload);
            taskVm.RemoveRequested = t => TransferTasks.Remove(t);
            TransferTasks.Add(taskVm);

            StartOperation(() => ExecuteUploadTaskAsync(taskVm, localPath, remotePath, fileName));
        }
    }

    private async Task ExecuteUploadTaskAsync(FileTransferTaskItemViewModel taskVm, string localPath, string remotePath, string fileName)
    {
        using CancellationTokenSource transferCts = CancellationTokenSource.CreateLinkedTokenSource(taskVm.Cts.Token, _lifetimeCts.Token);
        CancellationToken ct = transferCts.Token;
        taskVm.State = FileTransferState.Transferring;
        taskVm.StatusText = "上传中...";
        IsTransferring = true;

        try
        {
            var fileInfo = new FileInfo(localPath);
            long totalBytes = fileInfo.Length;
            long transferred = 0;

            await using var localStream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
            await using var remoteStream = await _fileSystem.OpenWriteAsync(remotePath, ct);

            byte[] buffer = new byte[64 * 1024];
            int read;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while ((read = await localStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await remoteStream.WriteAsync(buffer.AsMemory(0, read), ct);
                transferred += read;
                if (totalBytes > 0)
                {
                    taskVm.Progress = Math.Min(100.0, (double)transferred / totalBytes * 100.0);
                    double elapsedSec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    taskVm.SpeedMBs = (transferred / (1024.0 * 1024.0)) / elapsedSec;
                    taskVm.StatusText = $"{taskVm.Progress:F1}% ({taskVm.SpeedMBs:F1} MB/s)";
                }
            }

            await _fileSystem.CommitWriteAsync(remoteStream, ct);

            taskVm.Progress = 100;
            taskVm.State = FileTransferState.Completed;
            taskVm.StatusText = "完成";
            TransferStatusMessage = $"已上传: {fileName}";
            await RefreshDirectoryAsync();
        }
        catch (OperationCanceledException)
        {
            taskVm.State = FileTransferState.Cancelled;
            taskVm.StatusText = "已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上传任务失败: {LocalPath} -> {RemotePath}", localPath, remotePath);
            taskVm.State = FileTransferState.Failed;
            taskVm.StatusText = $"失败: {ex.Message}";
        }
        finally
        {
            UpdateOverallTransferringState();
        }
    }

    private void UpdateOverallTransferringState()
    {
        bool anyActive = TransferTasks.Any(t => t.State is FileTransferState.Pending or FileTransferState.Transferring);
        IsTransferring = anyActive;
        if (!anyActive)
        {
            TransferProgress = 100;
            _ = Task.Delay(2000).ContinueWith(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && !TransferTasks.Any(t => t.State is FileTransferState.Pending or FileTransferState.Transferring))
                    {
                        TransferProgress = 0;
                    }
                });
            });
        }
    }

}
