using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.ViewModels;

public partial class RemoteFileManagerViewModel
{
    private CancellationTokenSource? _directoryRefreshCancellation;
    private string? _loadedDirectoryPath;
    private Task _navigation = Task.CompletedTask;

    private static string ParentDirectory(string path)
    {
        int separator = path.TrimEnd('/').LastIndexOf('/');
        return separator > 0 ? path[..separator] : "/";
    }

    internal bool CanOperateOnLoadedItems()
        => !IsLoading && (_loadedDirectoryPath == null || _loadedDirectoryPath == CurrentPath);

    private void NotifyItemActionCanExecuteChanged()
    {
        DeleteItemCommand.NotifyCanExecuteChanged();
        RenameItemCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        DownloadToCommand.NotifyCanExecuteChanged();
        EnterOrOpenFileCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    public Task NavigateToAddressAsync()
    {
        string target = string.IsNullOrEmpty(AddressBarPath) ? "/" : AddressBarPath;
        return NavigateToPathAsync(target);
    }

    public Task NavigateToPathAsync(string targetPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(targetPath)) return Task.CompletedTask;
        lock (_operationLock)
        {
            if (_disposed) return Task.CompletedTask;
            _directoryRefreshCancellation?.Cancel();
            CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            _directoryRefreshCancellation = cancellation;
            Task operation = RefreshDirectoryCoreAsync(targetPath, cancellation, commitPathOnSuccess: true);
            _navigation = operation;
            _operations.Add(operation);
            _ = ObserveOperationAsync(operation);
            return operation;
        }
    }

    [RelayCommand]
    public Task RefreshDirectoryAsync(CancellationToken ct = default)
    {
        lock (_operationLock)
        {
            // 切换目录正在列出目标路径，旧目录的上传或回写完成不能中断它。
            if (_disposed) return Task.CompletedTask;
            if (!_navigation.IsCompleted) return _navigation.WaitAsync(ct);
            _directoryRefreshCancellation?.Cancel();
            CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            _directoryRefreshCancellation = cancellation;
            Task operation = RefreshDirectoryCoreAsync(CurrentPath, cancellation, commitPathOnSuccess: false);
            // 关闭时也排空列目录请求，保证文件通道不会在请求执行中被释放。
            _operations.Add(operation);
            _ = ObserveOperationAsync(operation);
            return operation;
        }
    }

    private async Task RefreshDirectoryCoreAsync(string path, CancellationTokenSource cancellation, bool commitPathOnSuccess)
    {
        CancellationToken ct = cancellation.Token;
        try
        {
            IsLoading = true;
            var list = await _fileSystem.ListDirectoryAsync(path, ct);
            ct.ThrowIfCancellationRequested();
            if (_disposed) return;
            if (!commitPathOnSuccess && CurrentPath != path) return;

            if (commitPathOnSuccess)
            {
                CurrentPath = path;
                AddressBarPath = path;
            }

            Items.Clear();
            if (path != "/")
            {
                Items.Add(new RemoteFileItem("..", ParentDirectory(path), true, 0, DateTimeOffset.MinValue, "drwxr-xr-x"));
            }

            var sorted = list.Where(item => ShowHiddenFiles || !item.Name.StartsWith('.'))
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
            foreach (RemoteFileItem item in sorted) Items.Add(item);
            _loadedDirectoryPath = path;
            ClearOperationError();

            if (DirectoryTreeRoots.Count > 0)
            {
                DirectoryTreeRoots[0].IsExpanded = true;
                RemoteDirectoryNodeViewModel? target = await DirectoryTreeRoots[0].EnsurePathLoadedAsync(path, ct);
                ct.ThrowIfCancellationRequested();
                if (_disposed || CurrentPath != path || target == null) return;
                _isInternalDirectoryNavigating = true;
                try
                {
                    target.IsExpanded = true;
                    SelectedDirectoryNode = target;
                }
                finally { _isInternalDirectoryNavigating = false; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || _disposed) return;
            if (!commitPathOnSuccess && CurrentPath != path) return;
            _logger.LogError(ex, "刷新目录失败: {Path}", path);
            ReportOperationError(
                DescribeFileException(
                    commitPathOnSuccess
                        ? Strings.Format("FileManager.Error.OpenDirectoryFailed", path)
                        : Strings.Get("FileManager.Error.RefreshFailed"),
                    ex),
                retryPath: path);
        }
        finally
        {
            if (ReferenceEquals(_directoryRefreshCancellation, cancellation))
            {
                _directoryRefreshCancellation = null;
                IsLoading = false;
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    public async Task RefreshDirectoryTreeAsync(CancellationToken ct = default)
    {
        if (DirectoryTreeRoots.Count == 0 || _disposed) return;
        try
        {
            var root = DirectoryTreeRoots[0];
            await root.ReloadAsync(recursiveExpanded: true, ct);
            ct.ThrowIfCancellationRequested();
            if (_disposed) return;

            root.IsExpanded = true;
            RemoteDirectoryNodeViewModel? target = await root.EnsurePathLoadedAsync(CurrentPath, ct);
            ct.ThrowIfCancellationRequested();
            if (_disposed || target == null) return;

            _isInternalDirectoryNavigating = true;
            try
            {
                target.IsExpanded = true;
                SelectedDirectoryNode = target;
            }
            finally
            {
                _isInternalDirectoryNavigating = false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新目录树失败");
            ReportOperationError(DescribeFileException(Strings.Get("FileManager.Error.RefreshTreeFailed"), ex));
        }
    }
}
