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
    public async Task EnterOrOpenFileAsync(RemoteFileItem item)
    {
        SelectedItem = item;
        if (item.IsDirectory)
        {
            CurrentPath = item.FullPath;
            await RefreshDirectoryAsync();
        }
        else
        {
            // 双击文件：下载到本地缓存并在外部编辑器中打开追踪
            TransferStatusMessage = $"正在打开: {item.Name}...";
            try
            {
                await _editorLauncher.OpenAndTrackAsync(_sessionId, item, _fileSystem, ct: _lifetimeCts.Token);
                ShowTrackedFile(item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "打开远程文件失败: {Path}", item.FullPath);
                TransferStatusMessage = $"打开失败: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    public async Task OpenWithEditorAsync(ExternalEditor editor)
    {
        if (SelectedItem == null || SelectedItem.IsDirectory) return;
        var item = SelectedItem;

        TransferStatusMessage = $"正在用 {editor.Name} 打开: {item.Name}...";
        try
        {
            await _editorLauncher.OpenAndTrackAsync(_sessionId, item, _fileSystem, overrideEditor: editor, ct: _lifetimeCts.Token);
            ShowTrackedFile(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "使用编辑器 {Editor} 打开远程文件失败: {Path}", editor.Name, item.FullPath);
            TransferStatusMessage = $"打开失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task OpenWithSystemDefaultAsync(RemoteFileItem? item)
    {
        item ??= SelectedItem;
        if (item == null || item.IsDirectory) return;
        TransferStatusMessage = $"正在用系统默认程序打开: {item.Name}...";
        try
        {
            await _editorLauncher.OpenAndTrackAsync(_sessionId, item, _fileSystem, ct: _lifetimeCts.Token, useSystemDefault: true);
            ShowTrackedFile(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "系统默认程序打开远程文件失败: {Path}", item.FullPath);
            TransferStatusMessage = $"打开失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task OpenWithCustomExecutableAsync(RemoteFileItem? item)
    {
        item ??= SelectedItem;
        if (item == null || item.IsDirectory) return;

        if (PickExecutableFileDialogAsync == null)
        {
            _logger.LogWarning("PickExecutableFileDialogAsync 未挂载，无法弹出文件选择器");
            return;
        }

        var execPath = await PickExecutableFileDialogAsync();
        if (string.IsNullOrWhiteSpace(execPath)) return;

        TransferStatusMessage = $"正在用 {Path.GetFileName(execPath)} 打开: {item.Name}...";
        try
        {
            await _editorLauncher.OpenAndTrackAsync(_sessionId, item, _fileSystem, directExecutablePath: execPath, ct: _lifetimeCts.Token);
            ShowTrackedFile(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "指定程序打开远程文件失败: {Path}", item.FullPath);
            TransferStatusMessage = $"打开失败: {ex.Message}";
        }
    }

    private void ShowTrackedFile(RemoteFileItem item)
    {
        string localPath = _fileTracker.GetLocalCachePath(_sessionId, item.FullPath);
        // 等待进程可能很快退出，不能重新加入已经停止的监视项。
        if (!_fileTracker.IsTracking(localPath)) return;
        _trackedFileLocalMap[item.FullPath] = localPath;
        if (!ActiveTrackedFiles.Contains(item.FullPath)) ActiveTrackedFiles.Add(item.FullPath);
        ShowFileActivities(1);
        UpdateTrackedStatusMessage();
    }

    private void UpdateTrackedStatusMessage()
    {
        if (ActiveTrackedFiles.Count == 0)
        {
            TransferStatusMessage = "就绪";
        }
        else
        {
            TransferStatusMessage = $"后台监视中 ({ActiveTrackedFiles.Count} 个文件)";
        }
    }

    [RelayCommand]
    public async Task StopTrackingFileAsync(string remotePath)
    {
        _logger.LogInformation("用户主动请求停止监视文件: {RemotePath}", remotePath);
        if (!_trackedFileLocalMap.TryGetValue(remotePath, out string? localPath)) return;
        try
        {
            try { await _fileTracker.CheckForChangesAsync(localPath, _lifetimeCts.Token); }
            catch (IOException ex)
            {
                // 显式停止必须仍然可用；读不到最后一次保存时保留缓存供恢复。
                _logger.LogWarning(ex, "手动停止前无法检查最后一次保存，保留缓存: {LocalPath}", localPath);
            }
            await _fileTracker.UnregisterTrackedFileAsync(localPath, _lifetimeCts.Token);
            _trackedFileLocalMap.Remove(remotePath);
            ActiveTrackedFiles.Remove(remotePath);
            UpdateTrackedStatusMessage();
            // 保留缓存：编辑器可能仍开着，或最后一次回写仍在排队。
            _logger.LogInformation("监视已停止，保留本地缓存: {LocalPath}", localPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "注销监视文件失败，保留监视与缓存: {RemotePath}", remotePath);
            TransferStatusMessage = $"停止监视失败: {ex.Message}";
        }
    }

    private void OnTrackedFileChanged(object? sender, LocalFileChangedEventArgs e)
    {
        _logger.LogInformation("Queued file writeback: Remote={RemotePath}, DetectedAt={DetectedAt}", e.RemotePath, e.DetectedAt);
        Dispatcher.UIThread.Post(() => StartOperation(() => WriteTrackedFileAsync(e)));
    }

    private async Task WriteTrackedFileAsync(LocalFileChangedEventArgs e)
    {
        CancellationToken ct = _lifetimeCts.Token;
        bool entered = false;
        string fileName = Path.GetFileName(e.RemotePath);
        try
        {
            // 保存事件可能密集到达；串行提交避免两个覆盖写入互相截断。
            await _writeBackGate.WaitAsync(ct);
            entered = true;
            TrackedActivityRevision++;
            TransferStatusMessage = $"检测到修改，正在回写: {fileName}...";
            _logger.LogInformation("File writeback started: Remote={RemotePath}, Local={LocalPath}", e.RemotePath, e.LocalFilePath);
            await using (FileStream localStream = new(e.LocalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (Stream remoteStream = await _fileSystem.OpenWriteAsync(e.RemotePath, ct))
            {
                await localStream.CopyToAsync(remoteStream, ct);
                await _fileSystem.CommitWriteAsync(remoteStream, ct);
            }
            _logger.LogInformation("File writeback committed: Remote={RemotePath}", e.RemotePath);

            TransferStatusMessage = $"回写成功: {fileName} ({DateTime.Now:HH:mm:ss})";
            await RefreshDirectoryAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 保留本地缓存；取消提交不应删除尚未上传的修改。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自动回写远程文件失败: {Path}", e.RemotePath);
            TransferStatusMessage = $"同步回写失败: {ex.Message}";
        }
        finally
        {
            if (entered) _writeBackGate.Release();
        }
    }
}
