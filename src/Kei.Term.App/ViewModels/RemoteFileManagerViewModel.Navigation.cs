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
    private string? _pendingNavigationPath;

    internal bool CanOperateOnLoadedItems()
        => !IsLoading && _pendingNavigationPath == null && (_loadedDirectoryPath == null || _loadedDirectoryPath == CurrentPath);

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
        string target = string.IsNullOrWhiteSpace(AddressBarPath) ? "/" : AddressBarPath.Trim();
        return NavigateToPathAsync(target);
    }

    public Task NavigateToPathAsync(string targetPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return Task.CompletedTask;
        string normalized = targetPath.Trim();
        lock (_operationLock)
        {
            if (_disposed) return Task.CompletedTask;
            _directoryRefreshCancellation?.Cancel();
            CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            _directoryRefreshCancellation = cancellation;
            _pendingNavigationPath = normalized;
            Task operation = RefreshDirectoryCoreAsync(normalized, cancellation, commitPathOnSuccess: true);
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
            if (_disposed) return Task.CompletedTask;
            _directoryRefreshCancellation?.Cancel();
            CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            _directoryRefreshCancellation = cancellation;
            _pendingNavigationPath = null;
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
                _pendingNavigationPath = null;
                CurrentPath = path;
                AddressBarPath = path;
            }

            Items.Clear();
            if (path != "/")
            {
                int separator = path.TrimEnd('/').LastIndexOf('/');
                string parent = separator > 0 ? path[..separator] : "/";
                Items.Add(new RemoteFileItem("..", parent, true, 0, DateTimeOffset.MinValue, "drwxr-xr-x"));
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
                _pendingNavigationPath = null;
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
