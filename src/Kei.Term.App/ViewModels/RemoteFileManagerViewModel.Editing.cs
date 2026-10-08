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
                await _editorLauncher.OpenAndTrackAsync(_sessionId, item, _fileSystem);
                string localPath = _fileTracker.GetLocalCachePath(_sessionId, item.FullPath);
                _trackedFileLocalMap[item.Name] = localPath;
                if (!ActiveTrackedFiles.Contains(item.Name))
                {
                    ActiveTrackedFiles.Add(item.Name);
                }
                UpdateTrackedStatusMessage();
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
            await _editorLauncher.OpenAndTrackAsync(_sessionId, item, _fileSystem, overrideEditor: editor);
            string localPath = _fileTracker.GetLocalCachePath(_sessionId, item.FullPath);
            _trackedFileLocalMap[item.Name] = localPath;
            if (!ActiveTrackedFiles.Contains(item.Name))
            {
                ActiveTrackedFiles.Add(item.Name);
            }
            UpdateTrackedStatusMessage();
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
            // 通过直接传空编辑器触发 LaunchDefaultEditor
            string localPath = _fileTracker.GetLocalCachePath(_sessionId, item.FullPath);
            if (!File.Exists(localPath))
            {
                await using var remoteStream = await _fileSystem.OpenReadAsync(item.FullPath);
                await using var localStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await remoteStream.CopyToAsync(localStream);
            }
            if (FileEditorLauncher.IsBinaryFile(localPath))
            {
                throw new InvalidOperationException($"文件 \"{item.Name}\" 检测为二进制文件，已阻止外部文本编辑器打开。");
            }
            await _fileTracker.RegisterTrackedFileAsync(_sessionId, item.FullPath, localPath);
            _trackedFileLocalMap[item.Name] = localPath;
            FileEditorLauncher.LaunchDefaultEditor(localPath);

            if (!ActiveTrackedFiles.Contains(item.Name))
            {
                ActiveTrackedFiles.Add(item.Name);
            }
            UpdateTrackedStatusMessage();
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
            await _editorLauncher.OpenAndTrackAsync(_sessionId, item, _fileSystem, directExecutablePath: execPath);
            string localPath = _fileTracker.GetLocalCachePath(_sessionId, item.FullPath);
            _trackedFileLocalMap[item.Name] = localPath;
            if (!ActiveTrackedFiles.Contains(item.Name))
            {
                ActiveTrackedFiles.Add(item.Name);
            }
            UpdateTrackedStatusMessage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "指定程序打开远程文件失败: {Path}", item.FullPath);
            TransferStatusMessage = $"打开失败: {ex.Message}";
        }
    }

    private void UpdateTrackedStatusMessage()
    {
        if (ActiveTrackedFiles.Count == 0)
        {
            TransferStatusMessage = "就绪";
        }
        else if (ActiveTrackedFiles.Count == 1)
        {
            TransferStatusMessage = $"后台监视: {ActiveTrackedFiles[0]}";
        }
        else
        {
            TransferStatusMessage = $"后台监视中 ({ActiveTrackedFiles.Count} 个文件: {string.Join(", ", ActiveTrackedFiles.Take(2))}...)";
        }
    }

    [RelayCommand]
    public async Task StopTrackingFileAsync(string fileName)
    {
        _logger.LogInformation("用户主动请求停止监视文件: {FileName}", fileName);
        ActiveTrackedFiles.Remove(fileName);
        UpdateTrackedStatusMessage();

        // 尝试从映射表或本地缓存中注销 tracker 并删除本地缓存文件
        try
        {
            if (!_trackedFileLocalMap.TryGetValue(fileName, out var localPath))
            {
                string remotePath = CurrentPath.TrimEnd('/') + "/" + fileName;
                localPath = _fileTracker.GetLocalCachePath(_sessionId, remotePath);
            }

            _trackedFileLocalMap.Remove(fileName);
            await _fileTracker.UnregisterTrackedFileAsync(localPath);
            _logger.LogInformation("已从 LocalFileTracker 注销文件: {LocalPath}", localPath);

            if (File.Exists(localPath))
            {
                File.Delete(localPath);
                _logger.LogInformation("已清理本地缓存文件: {LocalPath}", localPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "注销监视文件失败: {FileName}", fileName);
        }
    }

    private void OnTrackedFileChanged(object? sender, LocalFileChangedEventArgs e)
    {
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
            TransferStatusMessage = $"检测到修改，正在回写: {fileName}...";
            await using (FileStream localStream = new(e.LocalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (Stream remoteStream = await _fileSystem.OpenWriteAsync(e.RemotePath, ct))
            {
                await localStream.CopyToAsync(remoteStream, ct);
                await _fileSystem.CommitWriteAsync(remoteStream, ct);
            }

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
