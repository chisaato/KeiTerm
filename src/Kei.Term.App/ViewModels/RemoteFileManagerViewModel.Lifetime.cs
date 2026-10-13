using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.ViewModels;

public partial class RemoteFileManagerViewModel
{
    private readonly Lock _operationLock = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly HashSet<Task> _operations = [];
    private readonly SemaphoreSlim _writeBackGate = new(1, 1);
    private volatile bool _disposed;
    private Task? _disposeTask;

    private void StartOperation(Func<Task> start)
    {
        lock (_operationLock)
        {
            if (_disposed) return;
            Task operation = start();
            _operations.Add(operation);
            _ = ObserveOperationAsync(operation);
        }
    }

    private async Task ObserveOperationAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "文件操作未完成");
        }
        finally
        {
            lock (_operationLock) _operations.Remove(operation);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_operationLock)
        {
            if (_disposeTask != null) return new ValueTask(_disposeTask);
            _disposed = true;
            _disposeTask = DisposeCoreAsync(_operations.ToArray());
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task[] pending)
    {
        DisposeSizeDisplay();
        DisposeFileActivities();
        _fileTracker.FileChanged -= OnTrackedFileChanged;
        _fileTracker.FileUntracked -= OnFileUntracked;
        _lifetimeCts.Cancel();
        try
        {
            // 停止事件源，并等待已开始的传输退出后才释放文件通道。
            await _editorLauncher.DisposeAsync().ConfigureAwait(false);
            await _fileTracker.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await _fileSystem.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _writeBackGate.Dispose();
                    _lifetimeCts.Dispose();
                    GC.SuppressFinalize(this);
                }
            }
        }
    }
}
