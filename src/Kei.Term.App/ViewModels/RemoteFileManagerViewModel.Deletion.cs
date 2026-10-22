using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.ViewModels;

public partial class RemoteFileManagerViewModel
{
    // 从确认框开始占用，避免连按 Delete 再开一轮。视图卸下后同一 VM 会重挂，这里不跟视图生命周期取消。
    private bool _deleteGate;
    private CancellationTokenSource? _deleteCts;
    private readonly object _deleteProgressLock = new();
    private int _deleteGeneration;
    private int _deleteEpoch;
    private RemoteFileDeletionProgress? _deleteSnapshot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelDeleteCommand))]
    private bool _isDeleting;

    [ObservableProperty]
    private bool _isDeleteScanning;

    [ObservableProperty]
    private double _deleteProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeleteStatus))]
    private string? _deleteStatusMessage;

    public bool HasDeleteStatus => !string.IsNullOrWhiteSpace(DeleteStatusMessage);

    [RelayCommand]
    public Task DeleteItemAsync(RemoteFileItem? singleItem)
    {
        List<RemoteFileItem> targets = CollectDeleteTargets(singleItem);
        if (targets.Count == 0) return Task.CompletedTask;

        TaskCompletionSource completion;
        int generation;
        lock (_operationLock)
        {
            if (_disposed || _deleteGate) return Task.CompletedTask;
            _deleteGate = true;
            generation = Interlocked.Increment(ref _deleteGeneration);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // 确认框还没返回也要登记，Dispose 才能排空这一轮，而不是先拆掉文件通道。
            _operations.Add(completion.Task);
            _ = ObserveOperationAsync(completion.Task);
        }

        _ = RunDeleteAsync(targets, completion, generation);
        return completion.Task;
    }

    [RelayCommand(CanExecute = nameof(CanCancelDelete))]
    private void CancelDelete()
    {
        CancellationTokenSource? cts;
        lock (_operationLock) cts = _deleteCts;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool CanCancelDelete() => IsDeleting;

    private async Task RunDeleteAsync(List<RemoteFileItem> targets, TaskCompletionSource completion, int generation)
    {
        try
        {
            await DeleteConfirmedAsync(targets, generation);
        }
        catch (Exception ex)
        {
            PublishDeleteFailure(generation, targets, ex);
            if (!_disposed) await RefreshAfterDeleteAsync();
        }
        finally
        {
            lock (_operationLock) _deleteGate = false;
            completion.TrySetResult();
        }
    }

    private async Task DeleteConfirmedAsync(List<RemoteFileItem> targets, int generation)
    {
        string prompt = FormatDeleteConfirmation(targets);
        if (!await (_interaction ?? NullInteractionService.Instance).ConfirmDeleteAsync(prompt)) return;

        CancellationTokenSource? cts = null;
        try
        {
            lock (_operationLock)
            {
                if (_disposed) return;
                cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                _deleteCts = cts;
            }

            lock (_deleteProgressLock) _deleteSnapshot = null;
            PublishDeleteStarted(generation);
            RemoteFileDeletionService service = new(_fileSystem);
            await service.DeleteAsync(targets, new DeleteProgressSink(this, generation), cts.Token);
            RemoteFileDeletionProgress? snapshot = SealDeleteProgress();
            int completed = snapshot?.CompletedCount ?? targets.Count;
            PublishDeleteTerminal(generation, Strings.Format("FileManager.Delete.Completed", completed), 100);
            if (!_disposed) await RefreshAfterDeleteAsync();
        }
        catch (OperationCanceledException) when (cts is { IsCancellationRequested: true })
        {
            RemoteFileDeletionProgress? snapshot = SealDeleteProgress();
            int completed = snapshot?.CompletedCount ?? 0;
            int total = snapshot is { TotalCount: > 0 } ? snapshot.TotalCount : targets.Count;
            PublishDeleteTerminal(generation, Strings.Format("FileManager.Delete.Cancelled", completed, total), Percent(snapshot));
            if (!_disposed) await RefreshAfterDeleteAsync();
        }
        catch (Exception ex)
        {
            PublishDeleteFailure(generation, targets, ex);
            if (!_disposed) await RefreshAfterDeleteAsync();
        }
        finally
        {
            lock (_operationLock)
            {
                if (ReferenceEquals(_deleteCts, cts)) _deleteCts = null;
            }

            cts?.Dispose();
        }
    }

    private void PublishDeleteFailure(int generation, List<RemoteFileItem> targets, Exception ex)
    {
        _logger.LogError(ex, "删除失败");
        RemoteFileDeletionProgress? snapshot = SealDeleteProgress();
        string path = string.IsNullOrEmpty(snapshot?.CurrentPath) ? targets[0].FullPath : snapshot.CurrentPath;
        int completed = snapshot?.CompletedCount ?? 0;
        int total = snapshot is { TotalCount: > 0 } ? snapshot.TotalCount : targets.Count;
        PublishDeleteTerminal(generation, Strings.Format("FileManager.Delete.Failed", path, completed, total, ex.Message), Percent(snapshot));
    }

    // 用户取消或删除失败时，删除令牌已经取消。列目录必须换一支未取消的令牌，否则列表会在写入前被丢掉。
    private Task RefreshAfterDeleteAsync() => RefreshDirectoryAsync(CancellationToken.None);

    private void PublishDeleteStarted(int generation)
    {
        PostToUiThread(() =>
        {
            if (generation != Volatile.Read(ref _deleteGeneration)) return;
            IsDeleting = true;
            IsDeleteScanning = false;
            DeleteProgress = 0;
            DeleteStatusMessage = Strings.Get("FileManager.Delete.Working");
        });
    }

    private void PublishDeleteTerminal(int generation, string message, double progress)
    {
        int epoch = Volatile.Read(ref _deleteEpoch);
        // 终态排到队尾。已经排队的进度先被丢掉，后写入的取消或失败结果不会被盖掉。
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != Volatile.Read(ref _deleteGeneration) || epoch != Volatile.Read(ref _deleteEpoch)) return;
            IsDeleting = false;
            IsDeleteScanning = false;
            DeleteProgress = Math.Clamp(progress, 0, 100);
            DeleteStatusMessage = message;
        });
    }

    private void ReportDeleteProgress(int generation, RemoteFileDeletionProgress value)
    {
        int epoch = Volatile.Read(ref _deleteEpoch);
        lock (_deleteProgressLock)
        {
            if (!CanApplyDeleteUi(generation, epoch)) return;
            _deleteSnapshot = value;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!CanApplyDeleteUi(generation, epoch)) return;
            IsDeleteScanning = value.IsScanning;
            DeleteProgress = Percent(value);
            if (!CanApplyDeleteUi(generation, epoch)) return;
            DeleteStatusMessage = value.IsScanning
                ? Strings.Format("FileManager.Delete.Scanning", value.CurrentPath)
                : Strings.Format("FileManager.Delete.Progress", value.CurrentPath, value.CompletedCount, value.TotalCount);
        });
    }

    private bool CanApplyDeleteUi(int generation, int epoch)
        => generation == Volatile.Read(ref _deleteGeneration) && epoch == Volatile.Read(ref _deleteEpoch);

    private RemoteFileDeletionProgress? SealDeleteProgress()
    {
        lock (_deleteProgressLock)
        {
            RemoteFileDeletionProgress? snapshot = _deleteSnapshot;
            Interlocked.Increment(ref _deleteEpoch);
            return snapshot;
        }
    }

    private static double Percent(RemoteFileDeletionProgress? value)
    {
        if (value == null || value.IsScanning || value.TotalCount <= 0) return 0;
        return Math.Clamp(value.CompletedCount * 100d / value.TotalCount, 0, 100);
    }

    internal static string FormatDeleteConfirmation(IReadOnlyList<RemoteFileItem> targets)
    {
        return targets.Count switch
        {
            1 => targets[0].Name,
            2 => Strings.Format("FileManager.DeleteConfirm.Two", targets[0].Name, targets[1].Name, targets.Count),
            3 => Strings.Format("FileManager.DeleteConfirm.Three", targets[0].Name, targets[1].Name, targets[2].Name, targets.Count),
            _ => Strings.Format("FileManager.DeleteConfirm.Many", targets[0].Name, targets[1].Name, targets[2].Name, targets.Count),
        };
    }

    private List<RemoteFileItem> CollectDeleteTargets(RemoteFileItem? singleItem)
    {
        if (SelectedItems.Count > 0)
        {
            return SelectedItems.Where(item => item.Name != "..").ToList();
        }

        if (singleItem is { Name: not ".." }) return [singleItem];
        return [];
    }

    private sealed class DeleteProgressSink(RemoteFileManagerViewModel owner, int generation) : IProgress<RemoteFileDeletionProgress>
    {
        public void Report(RemoteFileDeletionProgress value) => owner.ReportDeleteProgress(generation, value);
    }
}
