using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.Core.Models;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.ViewModels;

public partial class RemoteFileManagerViewModel
{
    private CancellationTokenSource? _directoryRefreshCancellation;
    private string? _loadedDirectoryPath;

    [RelayCommand]
    public Task RefreshDirectoryAsync(CancellationToken ct = default)
    {
        lock (_operationLock)
        {
            if (_disposed) return Task.CompletedTask;
            _directoryRefreshCancellation?.Cancel();
            CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            _directoryRefreshCancellation = cancellation;
            Task operation = RefreshDirectoryCoreAsync(CurrentPath, cancellation);
            // 关闭时也排空列目录请求，保证文件通道不会在请求执行中被释放。
            _operations.Add(operation);
            _ = ObserveOperationAsync(operation);
            return operation;
        }
    }

    private async Task RefreshDirectoryCoreAsync(string path, CancellationTokenSource cancellation)
    {
        CancellationToken ct = cancellation.Token;
        try
        {
            IsLoading = true;
            var list = await _fileSystem.ListDirectoryAsync(path, ct);
            ct.ThrowIfCancellationRequested();
            if (_disposed || CurrentPath != path) return;

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
            if (ct.IsCancellationRequested || _disposed || CurrentPath != path) return;
            _logger.LogError(ex, "刷新目录失败: {Path}", path);
            TransferStatusMessage = $"刷新失败: {ex.Message}";
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
}
