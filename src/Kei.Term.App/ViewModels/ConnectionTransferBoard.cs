using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Transfers;
using Kei.Term.Ssh.Services;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.ViewModels;

// 连接上的传输队列在界面上的投影。侧栏和目录标签共用同一个 Tasks 集合。
public sealed class ConnectionTransferBoard : IAsyncDisposable
{
    private readonly TransferQueue _queue;
    private readonly Dictionary<Guid, FileTransferTaskItemViewModel> _views = [];
    private readonly object _projection = new();
    private readonly bool _ownsEndpoint;

    private ConnectionTransferBoard(TransferQueue queue, ObservableCollection<FileTransferTaskItemViewModel> tasks, bool ownsEndpoint)
    {
        _queue = queue;
        Tasks = tasks;
        _ownsEndpoint = ownsEndpoint;
        _queue.Changed += OnChanged;
    }

    public ObservableCollection<FileTransferTaskItemViewModel> Tasks { get; }

    public TransferQueue Queue => _queue;

    public static ConnectionTransferBoard Create(
        ITransferEndpoint endpoint,
        Func<FileTransferSettings> settings,
        IInteractionService interaction,
        ITransferFailureClassifier? classifier = null,
        ILogger? logger = null,
        bool ownsEndpoint = true,
        ObservableCollection<FileTransferTaskItemViewModel>? tasks = null,
        Guid? sessionId = null,
        ISessionMetadataRepository? metadata = null,
        SessionOverrides? overrides = null)
    {
        // 每个连接一份探测提供者：shell 从端点拿，设置每次现读，会话行没有时提供者自己改走内存。
        TransferCapabilityProvider capabilities = new(
            sessionId,
            metadata,
            settings,
            endpoint.AcquireShellAsync);
        TransferQueue queue = new(
            endpoint,
            settings,
            classifier ?? SshTransferFailureClassifier.Instance,
            logger: logger,
            promptConflict: (name, source, target, ct) => OnUi(() => interaction.PromptTransferConflictAsync(name, source, target, ct)),
            promptLargeFile: (name, size, ct) => OnUi(() => interaction.ConfirmLargeScpTransferAsync(name, size, ct)),
            capabilities: capabilities,
            // 与连接时取得的其他会话覆盖一致：持有连接配置快照，任务开始时读取其开关。
            verifyHashOverride: () => overrides?.VerifyHashEnabled,
            archiveDirectoriesOverride: () => overrides?.ArchiveDirectoriesEnabled);
        return new ConnectionTransferBoard(queue, tasks ?? [], ownsEndpoint);
    }

    // 没有专用端点时用浏览文件系统，且不在关闭时释放它。
    public static ConnectionTransferBoard CreateFallback(
        IRemoteFileSystem browse,
        Func<FileTransferSettings> settings,
        IInteractionService interaction,
        ObservableCollection<FileTransferTaskItemViewModel>? tasks = null,
        FileTransferProtocol protocol = FileTransferProtocol.Sftp,
        ILogger? logger = null)
    {
        return Create(
            new FileSystemTransferEndpoint(browse, protocol, owns: false),
            settings,
            interaction,
            DotNetTransferFailureClassifier.Instance,
            logger,
            ownsEndpoint: false,
            tasks);
    }

    public void EnqueueDownloads(IReadOnlyList<RemoteFileItem> items, string localDirectory)
    {
        if (items.Count == 0) return;
        Directory.CreateDirectory(localDirectory);
        _queue.EnqueueMany(items.Select(item => new TransferSubmission(
            TransferDirection.Download,
            item.Name,
            localDirectory,
            item.FullPath,
            item.IsDirectory,
            item.LinkTargetSize ?? item.Size,
            item.LinkTargetModified ?? item.LastModified)).ToArray());
    }

    public void EnqueueUploads(IReadOnlyList<string> paths, string remoteDirectory)
    {
        TransferSubmission[] submissions = paths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Select(path =>
            {
                bool directory = Directory.Exists(path);
                string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                long size = directory ? 0 : new FileInfo(path).Length;
                DateTimeOffset modified = directory ? default : new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                return new TransferSubmission(TransferDirection.Upload, name, path, remoteDirectory, directory, size, modified);
            })
            .ToArray();
        if (submissions.Length == 0) return;
        // Changed 会在返回前把投影做完。不能再在调用线程补一次 Sync，否则和已投递的 UI 投影并发插入。
        _queue.EnqueueMany(submissions);
    }

    public void ClearFinished() => _queue.ClearFinished();

    public void RetryAllFailed() => _queue.RetryAllFailed();

    public async Task CloseAsync()
    {
        _queue.Changed -= OnChanged;
        await _queue.CloseAsync();
    }

    public ValueTask DisposeAsync() => _ownsEndpoint ? new ValueTask(CloseAsync()) : ValueTask.CompletedTask;

    private void OnChanged() => RunOnUi(Sync);

    private void Sync()
    {
        lock (_projection)
        {
            HashSet<Guid> live = [];
            foreach (QueuedTransfer task in _queue.Tasks)
            {
                live.Add(task.Id);
                if (_views.TryGetValue(task.Id, out FileTransferTaskItemViewModel? existing))
                {
                    Apply(existing, task);
                    continue;
                }

                FileTransferTaskItemViewModel view = new(task);
                view.Bind(_queue);
                view.RemoveRequested = item =>
                {
                    lock (_projection)
                    {
                        Tasks.Remove(item);
                        _views.Remove(task.Id);
                    }
                };
                _views[task.Id] = view;
                Tasks.Add(view);
                task.Updated += () => OnUi(() => Apply(view, task));
                Apply(view, task);
            }

            foreach ((Guid id, FileTransferTaskItemViewModel view) in _views.Where(pair => !live.Contains(pair.Key)).ToArray())
            {
                Tasks.Remove(view);
                _views.Remove(id);
            }
        }
    }

    private static void Apply(FileTransferTaskItemViewModel view, QueuedTransfer task)
    {
        view.State = Map(task.State);
        view.StatusText = Status(task);
        view.ErrorMessage = task.ErrorMessage;
        view.IsUnverified = task.IsUnverified;
        view.NoteProgress(task.TransferredBytes, task.TotalBytes);
    }

    private static FileTransferState Map(QueuedTransferState state) => state switch
    {
        QueuedTransferState.Transferring => FileTransferState.Transferring,
        QueuedTransferState.Packing => FileTransferState.Packing,
        QueuedTransferState.Verifying => FileTransferState.Verifying,
        QueuedTransferState.Unpacking => FileTransferState.Unpacking,
        QueuedTransferState.AwaitingConflict => FileTransferState.AwaitingConflict,
        QueuedTransferState.RetryWait => FileTransferState.RetryWait,
        QueuedTransferState.Completed => FileTransferState.Completed,
        QueuedTransferState.Skipped => FileTransferState.Skipped,
        QueuedTransferState.Failed => FileTransferState.Failed,
        QueuedTransferState.Cancelled => FileTransferState.Cancelled,
        _ => FileTransferState.Pending,
    };

    private static string Status(QueuedTransfer task) => task.State switch
    {
        QueuedTransferState.Transferring => Strings.Get("FileActivity.Status.Transferring"),
        QueuedTransferState.Packing => Strings.Get("FileActivity.Status.Packing"),
        QueuedTransferState.Verifying => Strings.Get("FileActivity.Status.Verifying"),
        QueuedTransferState.Unpacking => Strings.Get("FileActivity.Status.Unpacking"),
        QueuedTransferState.AwaitingConflict => Strings.Get("FileActivity.Status.AwaitingConflict"),
        QueuedTransferState.RetryWait => Strings.Get("FileActivity.Status.RetryWait"),
        QueuedTransferState.Completed => Strings.Get("FileActivity.Status.Completed"),
        QueuedTransferState.Skipped => task.ErrorMessage ?? Strings.Get("FileActivity.Status.Skipped"),
        QueuedTransferState.Failed => string.IsNullOrWhiteSpace(task.ErrorMessage)
            ? Strings.Format("FileActivity.Status.Failed", string.Empty).TrimEnd(':', ' ')
            : Strings.Format("FileActivity.Status.Failed", task.ErrorMessage),
        QueuedTransferState.Cancelled => Strings.Get("FileActivity.Status.Cancelled"),
        _ => Strings.Get("FileActivity.Status.Pending"),
    };

    // 进度更新可以晚一拍。结构变更必须等 UI 线程做完，否则调用方返回时集合还是空的，或和后台 Sync 各插一条。
    public static void RunOnUi(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
                return;
            }

            Dispatcher.UIThread.Invoke(action);
        }
        catch (InvalidOperationException)
        {
            action();
        }
    }

    public static void OnUi(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.Post(action);
        }
        catch (InvalidOperationException)
        {
            action();
        }
    }

    private static Task<T> OnUi<T>(Func<Task<T>> action)
    {
        try
        {
            return Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);
        }
        catch (InvalidOperationException)
        {
            return action();
        }
    }
}
