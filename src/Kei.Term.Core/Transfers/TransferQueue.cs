namespace Kei.Term.Core.Transfers;

using System.Threading.Channels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public enum QueuedTransferState
{
    Queued,
    Transferring,
    AwaitingConflict,
    RetryWait,
    Completed,
    Skipped,
    Failed,
    Cancelled,
    Packing,
    Verifying,
    Unpacking,
}

// 上传：LocalPath 是源，RemotePath 是目标父目录。下载：LocalPath 是目标父目录，RemotePath 是源。
public sealed record TransferSubmission(
    TransferDirection Direction,
    string Name,
    string LocalPath,
    string RemotePath,
    bool IsDirectory,
    long Size = 0,
    DateTimeOffset ModifiedUtc = default);

public sealed class QueuedTransfer
{
    private readonly List<Child> _children = [];
    private bool _expanded;

    internal QueuedTransfer(Guid batchId, TransferSubmission submission, CancellationTokenSource cancel)
    {
        Id = Guid.NewGuid();
        BatchId = batchId;
        Submission = submission;
        CancelSource = cancel;
        Direction = submission.Direction;
        Name = submission.Name;
        LocalPath = submission.LocalPath;
        RemotePath = submission.RemotePath;
        IsDirectory = submission.IsDirectory;
        TotalBytes = submission.IsDirectory ? 0 : Math.Max(0, submission.Size);
    }

    public Guid Id { get; }
    public Guid BatchId { get; }
    public TransferDirection Direction { get; }
    public string Name { get; }
    public string LocalPath { get; }
    public string RemotePath { get; }
    public bool IsDirectory { get; }
    public QueuedTransferState State { get; private set; } = QueuedTransferState.Queued;

    // 这次任务启用了校验，但没有可用算法（或没有 shell）。校验关闭、或已经比对过，则为 false。
    public bool IsUnverified { get; private set; }
    public long TransferredBytes { get; private set; }
    public long TotalBytes { get; private set; }
    public string? ErrorMessage { get; private set; }
    public TransferFailureKind? FailureKind { get; private set; }

    internal TransferSubmission Submission { get; }
    internal CancellationTokenSource CancelSource { get; private set; }
    internal int Generation { get; private set; }

    public event Action? Updated;

    public bool IsActive => State is QueuedTransferState.Queued
        or QueuedTransferState.Transferring
        or QueuedTransferState.Packing
        or QueuedTransferState.Verifying
        or QueuedTransferState.Unpacking
        or QueuedTransferState.AwaitingConflict
        or QueuedTransferState.RetryWait;

    internal void PrepareRetry(CancellationToken shutdown)
    {
        try { CancelSource.Cancel(); }
        catch (ObjectDisposedException) { }
        Generation++;
        _children.Clear();
        _expanded = false;
        CancelSource = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        IsUnverified = false;
        Publish(QueuedTransferState.Queued, 0, IsDirectory ? 0 : Math.Max(0, Submission.Size), null, null);
    }

    internal void RequestCancel()
    {
        try { CancelSource.Cancel(); }
        catch (ObjectDisposedException) { }
        if (IsActive) Publish(QueuedTransferState.Cancelled, TransferredBytes, TotalBytes, ErrorMessage, null);
    }

    internal int AddChild()
    {
        _expanded = true;
        _children.Add(new Child());
        return _children.Count - 1;
    }

    internal void SetTotal(long total) => Publish(State, TransferredBytes, total, ErrorMessage, FailureKind);

    // 启用校验但挑不到算法时记下。目录任务里任一文件如此，整个任务都算未校验。
    internal void NoteUnverified(int generation)
    {
        if (generation != Generation || IsUnverified) return;
        IsUnverified = true;
        Updated?.Invoke();
    }

    internal void Report(int generation, int childIndex, QueuedTransferState state, long? bytes, string? error, TransferFailureKind? kind)
    {
        if (generation != Generation) return;
        if (!_expanded || childIndex < 0)
        {
            Publish(state, bytes ?? TransferredBytes, TotalBytes, error, kind);
            return;
        }

        Child child = _children[childIndex];
        child.State = state;
        if (bytes is long value) child.Bytes = value;
        Publish(Aggregate(), _children.Sum(item => item.Bytes), TotalBytes, error, kind);
    }

    internal void ReportBytes(int generation, int childIndex, long bytes)
    {
        if (generation != Generation) return;
        if (!_expanded || childIndex < 0)
        {
            Publish(State, bytes, TotalBytes, ErrorMessage, FailureKind);
            return;
        }

        _children[childIndex].Bytes = bytes;
        Publish(State, _children.Sum(item => item.Bytes), TotalBytes, ErrorMessage, FailureKind);
    }

    private void Publish(QueuedTransferState state, long transferred, long total, string? error, TransferFailureKind? kind)
    {
        State = state;
        TransferredBytes = transferred;
        TotalBytes = total;
        ErrorMessage = error;
        FailureKind = kind;
        Updated?.Invoke();
    }

    private QueuedTransferState Aggregate()
    {
        if (_children.Count == 0) return State;
        if (_children.Any(child => child.State == QueuedTransferState.Packing)) return QueuedTransferState.Packing;
        if (_children.Any(child => child.State == QueuedTransferState.Verifying)) return QueuedTransferState.Verifying;
        if (_children.Any(child => child.State == QueuedTransferState.Unpacking)) return QueuedTransferState.Unpacking;
        if (_children.Any(child => child.State == QueuedTransferState.Transferring)) return QueuedTransferState.Transferring;
        if (_children.Any(child => child.State == QueuedTransferState.AwaitingConflict)) return QueuedTransferState.AwaitingConflict;
        if (_children.Any(child => child.State == QueuedTransferState.RetryWait)) return QueuedTransferState.RetryWait;
        if (_children.Any(child => child.State == QueuedTransferState.Queued)) return QueuedTransferState.Queued;
        if (_children.Any(child => child.State == QueuedTransferState.Failed)) return QueuedTransferState.Failed;
        if (_children.All(child => child.State == QueuedTransferState.Skipped)) return QueuedTransferState.Skipped;
        if (_children.Any(child => child.State == QueuedTransferState.Cancelled)
            && _children.All(child => child.State is QueuedTransferState.Cancelled or QueuedTransferState.Skipped))
        {
            return QueuedTransferState.Cancelled;
        }

        return _children.Any(child => child.State == QueuedTransferState.Completed)
            ? QueuedTransferState.Completed
            : QueuedTransferState.Cancelled;
    }

    private sealed class Child
    {
        public QueuedTransferState State { get; set; } = QueuedTransferState.Queued;
        public long Bytes { get; set; }
    }
}

public sealed partial class TransferQueue : IAsyncDisposable
{
    private readonly ITransferEndpoint _endpoint;
    private readonly Func<FileTransferSettings> _settings;
    private readonly ITransferFailureClassifier _classifier;
    private readonly FileTransferExecutor _executor;
    private readonly TimeProvider _time;
    private readonly Func<string, TransferFileFacts, TransferFileFacts, CancellationToken, Task<TransferConflictChoice?>>? _promptConflict;
    private readonly Func<string, long, CancellationToken, Task<bool>>? _promptLargeFile;
    private readonly ILogger _logger;
    private readonly Func<bool?>? _verifyHashOverride;
    private readonly Func<bool?>? _archiveDirectoriesOverride;
    private readonly Func<CancellationToken, Task>? _afterCopy;
    private readonly SemaphoreSlim _slots;
    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<QueuedTransfer> _tasks = [];
    private readonly List<Task> _inflight = [];
    private readonly Dictionary<Guid, TransferConflictPolicy> _batchPolicy = [];
    private readonly DestinationClaimGate _claims = new();
    private readonly object _gate = new();
    private readonly bool _localDestinationIgnoreCase;
    private readonly Task _pump;
    private int _closed;
    private Task? _closing;

    public TransferQueue(
        ITransferEndpoint endpoint,
        Func<FileTransferSettings> settings,
        ITransferFailureClassifier? classifier = null,
        FileTransferExecutor? executor = null,
        TimeProvider? time = null,
        Func<string, TransferFileFacts, TransferFileFacts, CancellationToken, Task<TransferConflictChoice?>>? promptConflict = null,
        Func<string, long, CancellationToken, Task<bool>>? promptLargeFile = null,
        ILogger? logger = null,
        bool? localDestinationIgnoreCase = null,
        ITransferCapabilityProvider? capabilities = null,
        Func<bool?>? verifyHashOverride = null,
        Func<CancellationToken, Task>? afterCopy = null,
        Func<bool?>? archiveDirectoriesOverride = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(settings);
        _endpoint = endpoint;
        _settings = settings;
        Capabilities = capabilities;
        _verifyHashOverride = verifyHashOverride;
        _archiveDirectoriesOverride = archiveDirectoriesOverride;
        _afterCopy = afterCopy;
        _classifier = classifier ?? DotNetTransferFailureClassifier.Instance;
        _executor = executor ?? new FileTransferExecutor(logger);
        _time = time ?? TimeProvider.System;
        _promptConflict = promptConflict;
        _promptLargeFile = promptLargeFile;
        _logger = logger ?? NullLogger.Instance;
        _localDestinationIgnoreCase = localDestinationIgnoreCase ?? !OperatingSystem.IsLinux();
        MaxConcurrency = endpoint.Protocol == FileTransferProtocol.Scp
            ? 1
            : FileTransferSettings.ClampConcurrency(settings().EffectiveConcurrency);
        _slots = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        _pump = Task.Run(PumpAsync);
    }

    public int MaxConcurrency { get; }

    // 本连接的能力探测。null 表示调用方没接（测试队列）。生产路径由 ConnectionTransferBoard 注入。
    public ITransferCapabilityProvider? Capabilities { get; }

    // 仅供测试观察：已进入槽位等待、尚未拿到槽的任务数。调度本身不读它。
    private readonly object _slotWatch = new();
    private int _slotWaiters;
    private TaskCompletionSource _slotPulse = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action? Changed;

    public IReadOnlyList<QueuedTransfer> Tasks
    {
        get { lock (_gate) return _tasks.ToArray(); }
    }

    public QueuedTransfer Enqueue(TransferSubmission submission) => EnqueueMany([submission]).Single();

    public IReadOnlyList<QueuedTransfer> EnqueueMany(IReadOnlyList<TransferSubmission> submissions)
    {
        ArgumentNullException.ThrowIfNull(submissions);
        ObjectDisposedException.ThrowIf(_closed != 0, this);
        Guid batchId = Guid.NewGuid();
        List<QueuedTransfer> created = [];
        foreach (TransferSubmission submission in submissions)
        {
            QueuedTransfer task = new(batchId, submission, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
            lock (_gate) _tasks.Add(task);
            created.Add(task);
            Write(new RootWork(task));
        }

        Changed?.Invoke();
        return created;
    }

    public void Cancel(Guid taskId) => Find(taskId)?.RequestCancel();

    public void Retry(Guid taskId)
    {
        QueuedTransfer? task = Find(taskId);
        if (task == null || _closed != 0 || task.State != QueuedTransferState.Failed) return;
        task.PrepareRetry(_shutdown.Token);
        Write(new RootWork(task));
        Changed?.Invoke();
    }

    public void RetryAllFailed()
    {
        foreach (QueuedTransfer task in Tasks.Where(item => item.State == QueuedTransferState.Failed).ToArray())
        {
            Retry(task.Id);
        }
    }

    public void ClearFinished()
    {
        lock (_gate)
        {
            _tasks.RemoveAll(task => task.State is QueuedTransferState.Completed
                or QueuedTransferState.Skipped
                or QueuedTransferState.Failed
                or QueuedTransferState.Cancelled);
        }

        Changed?.Invoke();
    }

    public Task CloseAsync()
    {
        lock (_gate) return _closing ??= CloseCoreAsync();
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task CloseCoreAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        _shutdown.Cancel();
        _channel.Writer.TryComplete();
        foreach (QueuedTransfer task in Tasks) task.RequestCancel();
        // 先关掉专用端点，打断仍堵在同步 IO 上的传输，再排空。
        try { await _endpoint.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "关闭传输端点失败"); }

        try { await _pump.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        Task[] inflight;
        lock (_gate) inflight = _inflight.ToArray();
        try { await Task.WhenAll(inflight).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "排空传输失败"); }
        catch (OperationCanceledException) { }

        foreach (QueuedTransfer task in Tasks.Where(item => item.IsActive))
        {
            task.Report(task.Generation, -1, QueuedTransferState.Cancelled, null, null, null);
        }

        Changed?.Invoke();
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (Work work in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                if (_closed != 0)
                {
                    Report(work, QueuedTransferState.Cancelled, null, null, null);
                    continue;
                }

                Task running = RunAsync(work);
                lock (_gate) _inflight.Add(running);
                _ = ForgetAsync(running);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ForgetAsync(Task running)
    {
        try { await running.ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "传输调度项未完成"); }
        finally { lock (_gate) _inflight.Remove(running); }
    }

    internal Task WhenSlotWaitersAsync(int count)
    {
        lock (_slotWatch)
        {
            if (_slotWaiters >= count) return Task.CompletedTask;
            Task pulse = _slotPulse.Task;
            return WaitForSlotWaitersAsync(count, pulse);
        }
    }

    private async Task WaitForSlotWaitersAsync(int count, Task pulse)
    {
        await pulse.ConfigureAwait(false);
        await WhenSlotWaitersAsync(count).ConfigureAwait(false);
    }

    private async Task AcquireSlotAsync(CancellationToken ct)
    {
        Task wait = _slots.WaitAsync(ct);
        bool queued = !wait.IsCompleted;
        if (queued) ChangeSlotWaiters(1);
        try
        {
            await wait.ConfigureAwait(false);
        }
        finally
        {
            if (queued) ChangeSlotWaiters(-1);
        }
    }

    private void ChangeSlotWaiters(int delta)
    {
        TaskCompletionSource previous;
        lock (_slotWatch)
        {
            _slotWaiters += delta;
            previous = _slotPulse;
            _slotPulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }

    private async Task RunAsync(Work work)
    {
        if (work.Generation != work.Task.Generation || work.Token.IsCancellationRequested || _closed != 0)
        {
            Report(work, QueuedTransferState.Cancelled, null, null, null);
            return;
        }

        bool acquired = false;
        try
        {
            await AcquireSlotAsync(work.Token).ConfigureAwait(false);
            acquired = true;
            if (_closed != 0)
            {
                Report(work, QueuedTransferState.Cancelled, null, null, null);
                return;
            }

            Work current = work is RootWork && !work.Task.IsDirectory ? new FileWork(work.Task) : work;
            if (current is RootWork)
            {
                current.Task.Report(current.Generation, -1, QueuedTransferState.Transferring, null, null, null);
                if (await TryArchiveAsync(current, ReleaseSlot, ReacquireSlotAsync).ConfigureAwait(false)) return;
                await ExpandAsync(current).ConfigureAwait(false);
                return;
            }

            await TransferFileAsync(current, ReleaseSlot, ReacquireSlotAsync).ConfigureAwait(false);

            void ReleaseSlot()
            {
                _slots.Release();
                acquired = false;
            }

            async Task ReacquireSlotAsync()
            {
                await AcquireSlotAsync(current.Token).ConfigureAwait(false);
                acquired = true;
            }
        }
        catch (OperationCanceledException)
        {
            Report(work, QueuedTransferState.Cancelled, null, null, null);
        }
        catch (Exception ex)
        {
            Report(work, QueuedTransferState.Failed, null, ex.Message, Classify(ex));
        }
        finally
        {
            if (acquired) _slots.Release();
        }
    }

    private async Task ExpandAsync(Work work)
    {
        QueuedTransfer task = work.Task;
        CancellationToken ct = work.Token;
        if (work.Generation != task.Generation) return;
        await using IRemoteTransferLease lease = await _endpoint.AcquireAsync(ct).ConfigureAwait(false);
        IRemoteFileSystem remote = lease.FileSystem;
        bool upload = task.Direction == TransferDirection.Upload;
        string destinationRoot = upload
            ? JoinRemote(task.RemotePath, task.Name)
            : Path.Combine(task.LocalPath, task.Name);
        if (upload) await EnsureRemoteDirectoryAsync(remote, destinationRoot, ct).ConfigureAwait(false);
        else EnsureLocalDirectory(destinationRoot);

        IReadOnlyList<SourceTreeEntry> entries = upload
            ? await SourceTreeReader.ReadLocalAsync(task.LocalPath, ct).ConfigureAwait(false)
            : await SourceTreeReader.ReadRemoteAsync(remote, task.RemotePath, ct).ConfigureAwait(false);
        IReadOnlyList<TransferPlanOp> plan = DirectoryTransferPlanner.Plan(entries);
        long total = 0;
        List<Work> files = [];
        foreach (TransferPlanOp op in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (work.Generation != task.Generation) return;
            if (op is TransferPlanOp.CreateDirectory directory)
            {
                string path = ChildPath(destinationRoot, directory.RelativePath, upload);
                if (upload) await EnsureRemoteDirectoryAsync(remote, path, ct).ConfigureAwait(false);
                else EnsureLocalDirectory(path);
                continue;
            }

            if (op is not TransferPlanOp.TransferFile file) continue;
            total += Math.Max(0, file.Size);
            int index = task.AddChild();
            string parent = ParentRelative(file.RelativePath);
            string destinationDirectory = parent.Length == 0
                ? destinationRoot
                : ChildPath(destinationRoot, parent, upload);
            string source = upload
                ? Path.Combine(task.LocalPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar))
                : JoinRemote(task.RemotePath, file.RelativePath);
            files.Add(new FileWork(task, index, source, destinationDirectory, Leaf(file.RelativePath), file.Size, file.ModifiedUtc));
        }

        if (work.Generation != task.Generation) return;
        task.SetTotal(total);
        if (files.Count == 0)
        {
            bool onlySkips = plan.Count > 0 && plan.All(op => op is TransferPlanOp.Skip or TransferPlanOp.CreateDirectory)
                && plan.Any(op => op is TransferPlanOp.Skip);
            task.Report(task.Generation, -1, onlySkips ? QueuedTransferState.Skipped : QueuedTransferState.Completed, null, onlySkips ? "已跳过" : null, null);
            return;
        }

        foreach (Work file in files) Write(file);
    }

    private async Task TransferFileAsync(Work work, Action release, Func<Task> reacquire)
    {
        FileWork file = (FileWork)work;
        try
        {
            Report(file, QueuedTransferState.Transferring, null, null, null);
            FileTransferSettings settings = Snapshot();
            await using IRemoteTransferLease lease = await _endpoint.AcquireAsync(file.Token).ConfigureAwait(false);
            TransferConflictPolicy? decided = null;
            while (true)
            {
                file.Token.ThrowIfCancellationRequested();
                TransferFileFacts source = await ReadSourceAsync(lease.FileSystem, file, file.Token).ConfigureAwait(false);
                if (file.ChildIndex < 0) file.Task.SetTotal(source.Size);
                string fileName = file.FileName;
                TransferFileFacts? target = await ReadTargetAsync(lease.FileSystem, file.DestinationDirectory, fileName, file.Task.Direction, file.Token).ConfigureAwait(false);
                TransferConflictPolicy policy = decided ?? PolicyFor(file.Task.BatchId, settings);
                ConflictResolution resolution = target is null
                    ? ConflictResolution.Overwrite
                    : ConflictRules.Resolve(policy, source, target.Value);
                if (resolution == ConflictResolution.NeedsPrompt)
                {
                    release();
                    Report(file, QueuedTransferState.AwaitingConflict, null, null, null);
                    TransferConflictChoice? choice = _promptConflict == null
                        ? null
                        : await _promptConflict(fileName, source, target!.Value, file.Token).ConfigureAwait(false);
                    await reacquire().ConfigureAwait(false);
                    if (choice == null)
                    {
                        Report(file, QueuedTransferState.Cancelled, null, null, null);
                        return;
                    }

                    if (choice.Value.ApplyToRemaining)
                    {
                        lock (_gate) _batchPolicy[file.Task.BatchId] = choice.Value.Policy;
                    }

                    decided = choice.Value.Policy;
                    Report(file, QueuedTransferState.Transferring, null, null, null);
                    continue;
                }

                if (resolution == ConflictResolution.Skip)
                {
                    Report(file, QueuedTransferState.Skipped, source.Size, "已跳过", null);
                    return;
                }

                if (resolution == ConflictResolution.Rename)
                {
                    fileName = await NextNameAsync(lease.FileSystem, file.DestinationDirectory, fileName, file.Task.Direction, file.Token).ConfigureAwait(false);
                }

                if (!await AcceptLargeFileAsync(file, source.Size, release, reacquire).ConfigureAwait(false)) return;
                string finalPath = FullName(file.DestinationDirectory, fileName, file.Task.Direction);
                string partialPath = FullName(file.DestinationDirectory, PartialFileNames.ToPartial(fileName), file.Task.Direction);
                string[] claimKeys = ClaimKeys(finalPath, partialPath, file.Task.Direction);
                DestinationClaimGate.Entry entry = _claims.TryEnter(claimKeys, file, file.OwnerToken);
                if (entry.Wait is Task wait)
                {
                    release();
                    await wait.WaitAsync(file.Token).ConfigureAwait(false);
                    await reacquire().ConfigureAwait(false);
                    continue;
                }

                using (entry.Lease)
                {
                    TransferFileFacts? again = await ReadTargetAsync(lease.FileSystem, file.DestinationDirectory, fileName, file.Task.Direction, file.Token).ConfigureAwait(false);
                    if ((target is null && again is not null) || (resolution == ConflictResolution.Rename && again is not null))
                    {
                        continue;
                    }

                    // 历史只代表已经打开过写流的部分文件。世代失效时先作废，避免这次失败后又把它当成自己的。
                    TransferFileFacts? history = entry.HistoryValid ? file.Attempted : null;
                    if (!entry.HistoryValid)
                    {
                        file.Attempted = null;
                    }

                    file.OwnerToken = entry.Token;
                    (ITransferHashVerifier? verifier, bool unverified) = await ResolveVerifierAsync(settings, file.Token).ConfigureAwait(false);
                    if (unverified)
                    {
                        file.Task.NoteUnverified(file.Generation);
                    }

                    var progress = new SyncProgress(bytes => file.Task.ReportBytes(file.Generation, file.ChildIndex, bytes));
                    FileTransferRequest request = new(
                        file.SourcePath,
                        file.DestinationDirectory,
                        fileName,
                        settings,
                        source,
                        history,
                        progress,
                        started => file.Attempted = started.Source,
                        verifier,
                        () => Report(file, QueuedTransferState.Verifying, null, null, null),
                        _afterCopy);
                    if (file.Task.Direction == TransferDirection.Upload)
                    {
                        await _executor.UploadAsync(request, lease.FileSystem, file.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await _executor.DownloadAsync(request, lease.FileSystem, file.Token).ConfigureAwait(false);
                    }

                    Report(file, QueuedTransferState.Completed, source.Size, null, null);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Report(file, QueuedTransferState.Cancelled, null, null, null);
        }
        catch (Exception ex)
        {
            await FailOrRetryAsync(file, ex, release, reacquire).ConfigureAwait(false);
        }
    }

    private async Task FailOrRetryAsync(FileWork file, Exception exception, Action release, Func<Task> reacquire)
    {
        // 部分文件已经删了。留下的源快照会让下一轮把一份新的坏前缀当成可续传历史。
        if (exception is TransferHashMismatchException)
        {
            file.Attempted = null;
        }

        if (_closed != 0 || file.Token.IsCancellationRequested)
        {
            Report(file, QueuedTransferState.Cancelled, null, null, null);
            return;
        }

        TransferFailureKind kind;
        try { kind = _classifier.Classify(exception); }
        catch (ArgumentException)
        {
            Report(file, QueuedTransferState.Cancelled, null, null, null);
            return;
        }

        file.Failures++;
        FileTransferSettings settings = Snapshot();
        if (!RetryBackoff.ShouldRetry(kind, file.Failures, settings))
        {
            Report(file, QueuedTransferState.Failed, null, exception.Message, kind);
            return;
        }

        Report(file, QueuedTransferState.RetryWait, null, exception.Message, kind);
        release();
        try
        {
            await DelayAsync(_time, RetryBackoff.Delay(file.Failures, settings), file.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Report(file, QueuedTransferState.Cancelled, null, null, null);
            return;
        }

        if (_closed != 0 || file.Token.IsCancellationRequested)
        {
            Report(file, QueuedTransferState.Cancelled, null, null, null);
            return;
        }

        Report(file, QueuedTransferState.Queued, null, exception.Message, kind);
        Write(file);
        await reacquire().ConfigureAwait(false);
    }

    private async Task<bool> AcceptLargeFileAsync(FileWork file, long size, Action release, Func<Task> reacquire)
    {
        if (!ScpTransferLimits.NeedsLargeFileWarning(_endpoint.Protocol, size)) return true;
        release();
        bool accepted = false;
        try
        {
            accepted = _promptLargeFile != null
                && await _promptLargeFile(file.FileName, size, file.Token).ConfigureAwait(false);
        }
        finally
        {
            await reacquire().ConfigureAwait(false);
        }

        if (accepted)
        {
            Report(file, QueuedTransferState.Transferring, null, null, null);
            return true;
        }

        Report(file, QueuedTransferState.Skipped, null, "已取消大文件传输", null);
        return false;
    }

    private TransferFailureKind Classify(Exception exception)
    {
        try { return _classifier.Classify(exception); }
        catch (ArgumentException) { return TransferFailureKind.Unknown; }
    }

    private TransferConflictPolicy PolicyFor(Guid batchId, FileTransferSettings settings)
    {
        lock (_gate) return _batchPolicy.TryGetValue(batchId, out TransferConflictPolicy policy) ? policy : settings.DefaultConflictPolicy;
    }

    private async Task<string> NextNameAsync(IRemoteFileSystem remote, string directory, string fileName, TransferDirection direction, CancellationToken ct)
    {
        HashSet<string> present = direction == TransferDirection.Upload
            ? await RemoteNamesAsync(remote, directory, ct).ConfigureAwait(false)
            : Directory.Exists(directory)
                ? Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];
        return AutoRename.NextAvailable(fileName, candidate =>
        {
            if (present.Contains(candidate)) return true;
            string finalPath = FullName(directory, candidate, direction);
            string partialPath = FullName(directory, PartialFileNames.ToPartial(candidate), direction);
            return _claims.Overlaps(ClaimKeys(finalPath, partialPath, direction));
        });
    }

    private static async Task<HashSet<string>> RemoteNamesAsync(IRemoteFileSystem remote, string directory, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<RemoteFileItem> items = await remote.ListDirectoryAsync(directory, ct).ConfigureAwait(false);
            return items.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static async Task<TransferFileFacts?> ReadTargetAsync(
        IRemoteFileSystem remote, string directory, string fileName, TransferDirection direction, CancellationToken ct)
    {
        if (direction == TransferDirection.Upload)
        {
            RemoteFileItem? item = await remote.GetItemAsync(JoinRemote(directory, fileName), ct).ConfigureAwait(false);
            if (item == null) return null;
            if (item.IsDirectory) throw new IOException($"目标是目录：{item.FullPath}");
            return new TransferFileFacts(item.Size, item.LastModified);
        }

        string path = Path.Combine(directory, fileName);
        if (Directory.Exists(path)) throw new IOException($"目标是目录：{path}");
        if (!File.Exists(path)) return null;
        FileInfo info = new(path);
        return new TransferFileFacts(info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
    }

    private FileTransferSettings Snapshot()
    {
        FileTransferSettings current = _settings();
        return new FileTransferSettings
        {
            ResumeEnabled = current.ResumeEnabled,
            AutoRetryEnabled = current.AutoRetryEnabled,
            RetryCount = current.RetryCount,
            BackoffMode = current.BackoffMode,
            InitialRetryIntervalSeconds = current.InitialRetryIntervalSeconds,
            MaxRetryIntervalSeconds = current.MaxRetryIntervalSeconds,
            DefaultConflictPolicy = current.DefaultConflictPolicy,
            Concurrency = current.Concurrency,
            VerifyHashEnabled = current.VerifyHashEnabled,
            HashAlgorithmOrder = current.HashAlgorithmOrder?.ToList() ?? [],
            ArchiveDirectoriesEnabled = current.ArchiveDirectoriesEnabled,
            ArchiveFormatOrder = current.ArchiveFormatOrder?.ToList() ?? [],
        };
    }

    // 会话覆盖优先，null 继承全局开关。顺序为空视为关闭，不记成未校验。
    private bool VerificationRequested(FileTransferSettings settings)
    {
        bool enabled = _verifyHashOverride?.Invoke() ?? settings.VerifyHashEnabled;
        return enabled && settings.EffectiveHashAlgorithmOrder.Count > 0;
    }

    private async Task<(ITransferHashVerifier? Verifier, bool Unverified)> ResolveVerifierAsync(
        FileTransferSettings settings,
        CancellationToken ct)
    {
        if (!VerificationRequested(settings))
        {
            return (null, false);
        }

        // 没有探测提供者时无法知道远端有什么，按未校验完成，不反复尝试。
        if (Capabilities == null)
        {
            return (null, true);
        }

        TransferProbeResult probe = await Capabilities.GetAsync(ct).ConfigureAwait(false);
        TransferHashAlgorithm? algorithm = probe.SelectHash(settings.EffectiveHashAlgorithmOrder);
        IRemoteToolset? tools = RemoteToolsets.For(probe.Platform);
        if (algorithm == null || tools == null)
        {
            return (null, true);
        }

        IRemoteShell? shell = await _endpoint.AcquireShellAsync(ct).ConfigureAwait(false);
        if (shell == null)
        {
            return (null, true);
        }

        return (new TransferHashVerifier(algorithm.Value, shell, tools), false);
    }

    private void Write(Work work)
    {
        if (!_channel.Writer.TryWrite(work)) Report(work, QueuedTransferState.Cancelled, null, null, null);
    }

    private QueuedTransfer? Find(Guid taskId)
    {
        lock (_gate) return _tasks.FirstOrDefault(task => task.Id == taskId);
    }

    // System.Threading.Timer 单次等待不能超过约 49.7 天。长间隔拆段，不改用户配置的秒数。
    private static readonly TimeSpan DelaySegment = TimeSpan.FromHours(24);

    private static async Task DelayAsync(TimeProvider time, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        TimeSpan remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            TimeSpan slice = remaining > DelaySegment ? DelaySegment : remaining;
            await DelaySliceAsync(time, slice, cancellationToken).ConfigureAwait(false);
            remaining -= slice;
        }
    }

    private static async Task DelaySliceAsync(TimeProvider time, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        if (delay <= TimeSpan.Zero) return;
        TaskCompletionSource source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DelayState state = new(source);
        state.Timer = time.CreateTimer(static item => ((DelayState)item!).Source.TrySetResult(), state, delay, Timeout.InfiniteTimeSpan);
        await using CancellationTokenRegistration registration = cancellationToken.Register(static item =>
        {
            DelayState pending = (DelayState)item!;
            pending.Source.TrySetCanceled();
            pending.Timer?.Dispose();
        }, state);
        await source.Task.ConfigureAwait(false);
        state.Timer?.Dispose();
    }

    private sealed class DelayState(TaskCompletionSource source)
    {
        public TaskCompletionSource Source { get; } = source;
        public ITimer? Timer { get; set; }
    }

    private static string FullName(string directory, string fileName, TransferDirection direction)
        => direction == TransferDirection.Upload ? JoinRemote(directory, fileName) : Path.Combine(directory, fileName);

    private static string JoinRemote(string directory, string name)
    {
        string trimmed = directory.TrimEnd('/');
        return trimmed.Length == 0 ? "/" + name : trimmed + "/" + name;
    }

    private static string ChildPath(string root, string relative, bool remote)
        => remote ? JoinRemote(root, relative) : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Leaf(string relative)
    {
        int slash = relative.LastIndexOf('/');
        return slash < 0 ? relative : relative[(slash + 1)..];
    }

    private static string ParentRelative(string relative)
    {
        int slash = relative.LastIndexOf('/');
        return slash < 0 ? string.Empty : relative[..slash];
    }

    private abstract class Work
    {
        protected Work(QueuedTransfer task, int childIndex)
        {
            Task = task;
            ChildIndex = childIndex;
            Generation = task.Generation;
            Token = task.CancelSource.Token;
        }

        public QueuedTransfer Task { get; }
        public int ChildIndex { get; }
        public int Generation { get; }
        public CancellationToken Token { get; }
    }

    private sealed class RootWork(QueuedTransfer task) : Work(task, -1);

    private sealed class FileWork : Work
    {
        public FileWork(QueuedTransfer task, int childIndex, string sourcePath, string destinationDirectory, string fileName, long size, DateTimeOffset modified)
            : base(task, childIndex)
        {
            SourcePath = sourcePath;
            DestinationDirectory = destinationDirectory;
            FileName = fileName;
            Source = new TransferFileFacts(Math.Max(0, size), modified);
        }

        public FileWork(QueuedTransfer task)
            : this(
                task,
                -1,
                task.Direction == TransferDirection.Upload ? task.LocalPath : task.RemotePath,
                task.Direction == TransferDirection.Upload ? task.RemotePath : task.LocalPath,
                task.Name,
                task.Submission.Size,
                task.Submission.ModifiedUtc)
        {
        }

        public string SourcePath { get; }
        public string DestinationDirectory { get; }
        public string FileName { get; }
        public TransferFileFacts Source { get; }
        public TransferFileFacts? Attempted { get; set; }
        public long? OwnerToken { get; set; }
        public int Failures { get; set; }
    }

    private static void Report(Work work, QueuedTransferState state, long? bytes, string? error, TransferFailureKind? kind)
        => work.Task.Report(work.Generation, work.ChildIndex, state, bytes, error, kind);

    private static async Task<TransferFileFacts> ReadSourceAsync(IRemoteFileSystem remote, FileWork file, CancellationToken ct)
    {
        if (file.Task.Direction == TransferDirection.Upload)
        {
            FileInfo info = new(file.SourcePath);
            if (!info.Exists) throw new FileNotFoundException("源文件不存在。", file.SourcePath);
            return new TransferFileFacts(info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
        }

        RemoteFileItem? item = await remote.GetItemAsync(file.SourcePath, ct).ConfigureAwait(false);
        if (item == null || item.IsDirectory) throw new FileNotFoundException("源文件不存在。", file.SourcePath);
        return new TransferFileFacts(item.LinkTargetSize ?? item.Size, item.LinkTargetModified ?? item.LastModified);
    }

    private static async Task EnsureRemoteDirectoryAsync(IRemoteFileSystem remote, string path, CancellationToken ct)
    {
        RemoteFileItem? existing = await remote.GetItemAsync(path, ct).ConfigureAwait(false);
        if (existing != null)
        {
            if (!existing.IsDirectory) throw new IOException($"目标不是目录：{path}");
            return;
        }

        try
        {
            await remote.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RemoteFileItem? after = await remote.GetItemAsync(path, ct).ConfigureAwait(false);
            if (after is { IsDirectory: true }) return;
            if (after != null) throw new IOException($"目标不是目录：{path}", ex);
            throw;
        }
    }

    private static void EnsureLocalDirectory(string path)
    {
        if (File.Exists(path)) throw new IOException($"目标不是目录：{path}");
        Directory.CreateDirectory(path);
    }

    private string[] ClaimKeys(string finalPath, string partialPath, TransferDirection direction)
    {
        string finalKey = NormalizeClaim(finalPath, direction);
        string partialKey = NormalizeClaim(partialPath, direction);
        return finalKey == partialKey ? [finalKey] : [finalKey, partialKey];
    }

    private string NormalizeClaim(string path, TransferDirection direction)
    {
        string tagged = direction == TransferDirection.Upload ? "U:" + path : "D:" + path;
        return direction == TransferDirection.Download && _localDestinationIgnoreCase
            ? tagged.ToUpperInvariant()
            : tagged;
    }

    internal sealed class DestinationClaimGate
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _held = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Owner> _owners = new(StringComparer.Ordinal);
        private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _sequence;

        public readonly struct Entry
        {
            public IDisposable? Lease { get; init; }
            public Task? Wait { get; init; }
            public long Token { get; init; }
            public bool HistoryValid { get; init; }
        }

        public bool Overlaps(IReadOnlyList<string> keys)
        {
            lock (_gate) return keys.Any(_held.Contains);
        }

        // 失败时在同一把锁里捕获当前等待任务。释放若发生在调用方 await 之前，捕获到的就是已被完成的那次。
        public Entry TryEnter(IReadOnlyList<string> keys, object claimant, long? expectedToken)
        {
            lock (_gate)
            {
                if (keys.Any(_held.Contains))
                {
                    return new Entry { Wait = _pulse.Task };
                }

                bool historyValid = expectedToken is long token && keys.All(key =>
                    _owners.TryGetValue(key, out Owner? owner)
                    && owner is not null
                    && ReferenceEquals(owner.Claimant, claimant)
                    && owner.Token == token);
                long next = historyValid ? expectedToken!.Value : ++_sequence;
                foreach (string key in keys)
                {
                    _held.Add(key);
                    _owners[key] = new Owner(claimant, next);
                }

                return new Entry
                {
                    Lease = new Lease(this, keys),
                    Token = next,
                    HistoryValid = historyValid,
                };
            }
        }

        private void Release(IReadOnlyList<string> keys)
        {
            TaskCompletionSource previous;
            lock (_gate)
            {
                foreach (string key in keys) _held.Remove(key);
                previous = _pulse;
                _pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            previous.TrySetResult();
        }

        private sealed class Owner(object claimant, long token)
        {
            public object Claimant { get; } = claimant;
            public long Token { get; } = token;
        }

        private sealed class Lease(DestinationClaimGate owner, IReadOnlyList<string> keys) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                owner.Release(keys);
            }
        }
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
