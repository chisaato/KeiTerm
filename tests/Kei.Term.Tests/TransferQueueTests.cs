using System.Text;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;

namespace Kei.Term.Tests;

public class TransferQueueTests
{
    [Fact]
    public async Task Concurrency_StartsThreeOfFiveInOrder()
    {
        BlockingFileSystem remote = new();
        await using TransferQueue queue = Queue(remote, concurrency: 3);
        string directory = TempDir();
        try
        {
            string[] files = Enumerable.Range(1, 5).Select(i => WriteFile(directory, $"f{i}.txt", "x")).ToArray();
            QueuedTransfer[] tasks = queue.EnqueueMany(files.Select(path => Upload(path, "/remote")).ToArray()).ToArray();

            // 到达数在排队进门闩的同一把锁里发布，不再轮询“计数已加、尚未 Wait”的窗口。
            Assert.Equal(["f1.txt.keipart", "f2.txt.keipart", "f3.txt.keipart"], await remote.WhenArrivedAsync(3));
            Assert.Equal(3, queue.MaxConcurrency);
            // 另外两个必须已经堵在槽上，而不是靠轮询碰巧看到计数。
            await queue.WhenSlotWaitersAsync(2);
            Assert.Equal(3, remote.MaxBlocked);

            remote.Release();
            await remote.WhenArrivedAsync(4);
            remote.Release();
            Assert.Equal(
                ["f1.txt.keipart", "f2.txt.keipart", "f3.txt.keipart", "f4.txt.keipart", "f5.txt.keipart"],
                await remote.WhenArrivedAsync(5));
            Assert.Equal(3, remote.MaxBlocked);
            remote.Release(3);
            await WhenTerminalAsync(tasks);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Scp_ConcurrencyIsOne()
    {
        BlockingFileSystem remote = new();
        await using TransferQueue queue = Queue(remote, concurrency: 5, protocol: FileTransferProtocol.Scp);
        string directory = TempDir();
        try
        {
            string[] files = Enumerable.Range(1, 3).Select(i => WriteFile(directory, $"s{i}.txt", "x")).ToArray();
            QueuedTransfer[] tasks = queue.EnqueueMany(files.Select(path => Upload(path, "/remote")).ToArray()).ToArray();
            Assert.Equal(["s1.txt.keipart"], await remote.WhenArrivedAsync(1));
            Task<string[]> second = remote.WhenArrivedAsync(2);
            // 另外两个必须已经堵在槽上。若并发失效，它们会先到达写入口，而不是靠睡 50ms 碰运气。
            Task slots = queue.WhenSlotWaitersAsync(2);
            Assert.Same(slots, await Task.WhenAny(second, slots));
            Assert.False(second.IsCompleted);
            Assert.Equal(1, queue.MaxConcurrency);
            Assert.Equal(1, remote.MaxBlocked);
            remote.Release(3);
            await WhenTerminalAsync(tasks);
            Assert.Equal(1, remote.MaxBlocked);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TransientFailure_RetriesAfterDelay_PermanentDoesNot()
    {
        ManualTime time = new();
        FlakyFileSystem transient = new() { FailuresBeforeSuccess = 2 };
        await using TransferQueue retrying = Queue(transient, time: time, retryCount: 3);
        string directory = TempDir();
        try
        {
            string file = WriteFile(directory, "a.txt", "data");
            QueuedTransfer task = retrying.Enqueue(Upload(file, "/remote"));
            Assert.Equal(TimeSpan.FromSeconds(2), await time.WhenTimerRegisteredAsync(1));
            Assert.Equal(QueuedTransferState.RetryWait, task.State);
            Assert.Equal(1, transient.Attempts);
            time.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(TimeSpan.FromSeconds(4), await time.WhenTimerRegisteredAsync(2));
            Assert.Equal(2, transient.Attempts);
            Assert.Equal(QueuedTransferState.RetryWait, task.State);
            time.Advance(TimeSpan.FromSeconds(4));
            await WhenTerminalAsync([task]).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(3, transient.Attempts);
        }
        finally { Directory.Delete(directory, true); }

        FlakyFileSystem permanent = new() { Permanent = true };
        await using TransferQueue denied = Queue(permanent, time: time, retryCount: 3);
        string other = TempDir();
        try
        {
            QueuedTransfer failed = denied.Enqueue(Upload(WriteFile(other, "b.txt", "data"), "/remote"));
            await WaitUntilAsync(() => failed.State == QueuedTransferState.Failed);
            // CloseAsync 等待工作任务退出，之后没有尚未注册的重试能逃过断言。
            await denied.CloseAsync();
            Assert.Equal(TransferFailureKind.Permanent, failed.FailureKind);
            Assert.Equal(1, permanent.Attempts);
            time.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(0, time.ActiveTimerCount);
            Assert.Equal(1, permanent.Attempts);
        }
        finally { Directory.Delete(other, true); }
    }

    [Fact]
    public async Task Close_CancelsRetryWait_AndDoesNotStartAnotherAttempt()
    {
        ManualTime time = new();
        FlakyFileSystem remote = new() { FailuresBeforeSuccess = 10 };
        await using TransferQueue queue = Queue(remote, time: time, retryCount: 3);
        string directory = TempDir();
        try
        {
            QueuedTransfer task = queue.Enqueue(Upload(WriteFile(directory, "a.txt", "data"), "/remote"));
            await time.WhenTimerRegisteredAsync(1);
            await queue.CloseAsync();
            time.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(0, time.ActiveTimerCount);
            Assert.Equal(QueuedTransferState.Cancelled, task.State);
            Assert.Equal(1, remote.Attempts);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task BatchRememberedConflict_DoesNotApplyToNextBatch()
    {
        ConflictFileSystem remote = new();
        remote.Existing.Add("/remote/a.txt");
        remote.Existing.Add("/remote/b.txt");
        remote.Existing.Add("/remote/c.txt");
        List<string> prompted = [];
        await using TransferQueue queue = Queue(remote, prompt: (name, _, _, _) =>
        {
            prompted.Add(name);
            return Task.FromResult<TransferConflictChoice?>(new(TransferConflictPolicy.Overwrite, name == "a.txt"));
        });
        string directory = TempDir();
        try
        {
            string a = WriteFile(directory, "a.txt", "A");
            string b = WriteFile(directory, "b.txt", "B");
            string c = WriteFile(directory, "c.txt", "C");
            queue.EnqueueMany([Upload(a, "/remote"), Upload(b, "/remote")]);
            queue.Enqueue(Upload(c, "/remote"));
            await WaitUntilAsync(() => queue.Tasks.All(task => task.State == QueuedTransferState.Completed));
            Assert.Equal(["a.txt", "c.txt"], prompted);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DirectoryTask_AggregatesBytes()
    {
        BlockingFileSystem remote = new(initialPermits: 10);
        await using TransferQueue queue = Queue(remote, concurrency: 2);
        string directory = TempDir();
        try
        {
            string root = Path.Combine(directory, "proj");
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "hello");
            await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "world!!");
            QueuedTransfer task = queue.Enqueue(new TransferSubmission(TransferDirection.Upload, "proj", root, "/remote", true));
            await WaitUntilAsync(() => task.State is QueuedTransferState.Completed or QueuedTransferState.Failed);
            Assert.Equal(QueuedTransferState.Completed, task.State);
            Assert.Equal(12, task.TotalBytes);
            Assert.Equal(12, task.TransferredBytes);
            Assert.Contains(remote.Created, path => path.EndsWith("/proj", StringComparison.Ordinal) || path == "/remote/proj");
            Assert.Contains(remote.Created, path => path.Contains("empty", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SidebarAndDirectoryTab_ShareTheSameQueue()
    {
        IdleFileSystem remote = new();
        ConnectionTransferBoard board = ConnectionTransferBoard.CreateFallback(remote, () => new FileTransferSettings(), NullInteractionService.Instance);
        await using RemoteFileManagerViewModel sidebar = new(Guid.NewGuid(), remote, new IdleTracker(), transfers: board);
        RemoteFileManagerViewModel? tab = sidebar.CreateDirectoryTab("/home/kei");
        Assert.NotNull(tab);
        Assert.Same(sidebar.TransferTasks, tab.TransferTasks);

        string directory = TempDir();
        try
        {
            string file = WriteFile(directory, "shared.txt", "same");
            using CancellationTokenSource stop = new();
            Task pump = HeadlessAvalonia.RunAsync(() =>
            {
                while (!stop.IsCancellationRequested) HeadlessAvalonia.Pump(1);
            });
            try
            {
                board.EnqueueUploads([file], "/remote");
                FileTransferTaskItemViewModel task = Assert.Single(sidebar.TransferTasks);
                Assert.Same(task, Assert.Single(tab.TransferTasks));
                Assert.Equal("shared.txt", task.FileName);
            }
            finally
            {
                stop.Cancel();
                await pump;
            }
        }
        finally
        {
            Directory.Delete(directory, true);
            await tab.DisposeAsync();
            await board.CloseAsync();
        }
    }

    private static TransferQueue Queue(
        IRemoteFileSystem remote,
        int concurrency = 3,
        FileTransferProtocol protocol = FileTransferProtocol.Sftp,
        TimeProvider? time = null,
        int retryCount = 0,
        Func<string, TransferFileFacts, TransferFileFacts, CancellationToken, Task<TransferConflictChoice?>>? prompt = null)
        => new(
            new FileSystemTransferEndpoint(remote, protocol, owns: false),
            () => new FileTransferSettings
            {
                Concurrency = concurrency,
                AutoRetryEnabled = retryCount > 0,
                RetryCount = retryCount,
                BackoffMode = RetryBackoffMode.Exponential,
                InitialRetryIntervalSeconds = 2,
                MaxRetryIntervalSeconds = 60,
                DefaultConflictPolicy = TransferConflictPolicy.Ask,
            },
            time: time,
            promptConflict: prompt);

    private static TransferSubmission Upload(string path, string remoteDirectory)
    {
        FileInfo info = new(path);
        return new TransferSubmission(
            TransferDirection.Upload,
            info.Name,
            path,
            remoteDirectory,
            false,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc));
    }

    private static string WriteFile(string directory, string name, string contents)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string TempDir()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task WhenTerminalAsync(IReadOnlyList<QueuedTransfer> tasks)
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int pending = tasks.Count;
        foreach (QueuedTransfer task in tasks)
        {
            int settled = 0;
            void OnUpdated()
            {
                if (task.State is QueuedTransferState.Failed or QueuedTransferState.Cancelled)
                {
                    done.TrySetException(new InvalidOperationException($"{task.Name} {task.State}: {task.ErrorMessage}"));
                    return;
                }

                if (task.State is not (QueuedTransferState.Completed or QueuedTransferState.Skipped)) return;
                if (Interlocked.Exchange(ref settled, 1) != 0) return;
                if (Interlocked.Decrement(ref pending) == 0) done.TrySetResult();
            }

            task.Updated += OnUpdated;
            OnUpdated();
        }

        await done.Task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(15, timeout.Token);
    }

    private sealed class IdleFileSystem : IRemoteFileSystem
    {
        public bool IsConnected => true;
        public string WorkingDirectory => "/home/kei";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RemoteFileItem>>([]);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => Task.FromResult<RemoteFileItem?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdleTracker : ILocalFileTracker
    {
        public event EventHandler<LocalFileChangedEventArgs>? FileChanged { add { } remove { } }
        public event EventHandler<string>? FileUntracked { add { } remove { } }
        public string GetLocalCachePath(Guid sessionId, string remotePath) => remotePath;
        public bool IsTracking(string localFilePath) => false;
        public Task CheckForChangesAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnregisterTrackedFileAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingFileSystem : MemoryFileSystem
    {
        private readonly object _arrival = new();
        private readonly Queue<TaskCompletionSource> _blocked = new();
        private readonly List<string> _started = [];
        private readonly List<(int Count, TaskCompletionSource<string[]> Done)> _pending = [];
        private int _permits;
        private int _waiting;
        private int _maxWaiting;

        public BlockingFileSystem(int initialPermits = 0)
        {
            _permits = initialPermits;
        }

        public int MaxBlocked { get { lock (_arrival) return _maxWaiting; } }

        public Task<string[]> WhenArrivedAsync(int count)
        {
            lock (_arrival)
            {
                if (_started.Count >= count) return Task.FromResult(_started.ToArray());
                TaskCompletionSource<string[]> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add((count, done));
                return done.Task;
            }
        }

        public void Release(int count = 1)
        {
            List<TaskCompletionSource> wake = [];
            lock (_arrival)
            {
                while (count > 0)
                {
                    if (_blocked.Count == 0)
                    {
                        _permits += count;
                        break;
                    }

                    TaskCompletionSource next = _blocked.Dequeue();
                    _waiting--;
                    if (next.Task.IsCompleted) continue;
                    wake.Add(next);
                    count--;
                }
            }

            foreach (TaskCompletionSource item in wake) item.TrySetResult();
        }

        public override async Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
        {
            string name = Path.GetFileName(path) ?? path;
            TaskCompletionSource? mine = null;
            lock (_arrival)
            {
                _started.Add(name);
                if (_permits > 0)
                {
                    _permits--;
                }
                else
                {
                    mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _blocked.Enqueue(mine);
                    _waiting++;
                    _maxWaiting = Math.Max(_maxWaiting, _waiting);
                }

                PublishArrival();
            }

            if (mine is not null)
            {
                using CancellationTokenRegistration registration = ct.Register(() =>
                {
                    bool removed;
                    lock (_arrival)
                    {
                        removed = Remove(mine);
                        if (removed) _waiting--;
                    }

                    if (removed) mine.TrySetCanceled(ct);
                });
                await mine.Task.ConfigureAwait(false);
            }

            return await base.OpenWriteAsync(path, ct).ConfigureAwait(false);
        }

        private void PublishArrival()
        {
            foreach ((int count, TaskCompletionSource<string[]> done) in _pending.ToArray())
            {
                if (_started.Count < count) continue;
                _pending.RemoveAll(item => item.Done == done);
                done.TrySetResult(_started.ToArray());
            }
        }

        private bool Remove(TaskCompletionSource mine)
        {
            if (!_blocked.Contains(mine)) return false;
            TaskCompletionSource[] rest = _blocked.Where(item => item != mine).ToArray();
            _blocked.Clear();
            foreach (TaskCompletionSource item in rest) _blocked.Enqueue(item);
            return true;
        }
    }

    private sealed class FlakyFileSystem : MemoryFileSystem
    {
        public int FailuresBeforeSuccess { get; init; }
        public bool Permanent { get; init; }
        public int Attempts;

        public override Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
        {
            int attempt = Interlocked.Increment(ref Attempts);
            if (Permanent) throw new UnauthorizedAccessException("denied");
            if (attempt <= FailuresBeforeSuccess) throw new TimeoutException("reset");
            return base.OpenWriteAsync(path, ct);
        }
    }

    private sealed class ConflictFileSystem : MemoryFileSystem;

    private class MemoryFileSystem : IRemoteFileSystem
    {
        // 同一把锁覆盖文件表。并发收尾若各写各的，GetItem 会读到空长度或字典损坏，任务失败。
        private readonly object _sync = new();
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<Stream, string> _open = [];
        public HashSet<string> Existing { get; } = new(StringComparer.Ordinal);
        public List<string> Created { get; } = [];
        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public bool SupportsResume => true;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
        {
            string prefix = path.TrimEnd('/') + "/";
            List<RemoteFileItem> items = [];
            lock (_sync)
            {
                foreach (string name in _files.Keys.Concat(Existing).Select(Path.GetFileName).OfType<string>().Distinct())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    items.Add(Item(prefix + name, 1));
                }
            }

            return Task.FromResult<IReadOnlyList<RemoteFileItem>>(items);
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        {
            lock (_sync)
            {
                byte[] bytes = _files.GetValueOrDefault(path) ?? [];
                return Task.FromResult<Stream>(new MemoryStream(bytes.ToArray()));
            }
        }

        public virtual Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
        {
            MemoryStream stream = new();
            lock (_sync) _open[stream] = path;
            return Task.FromResult<Stream>(stream);
        }

        public Task CommitWriteAsync(Stream stream, CancellationToken ct = default)
        {
            if (stream is MemoryStream memory)
            {
                lock (_sync)
                {
                    if (_open.TryGetValue(stream, out string? path)) _files[path] = memory.ToArray();
                }
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
        {
            lock (_sync) _files.Remove(path);
            return Task.CompletedTask;
        }

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => RenameOverwriteAsync(oldPath, newPath, ct);

        public Task RenameOverwriteAsync(string oldPath, string newPath, CancellationToken ct = default)
        {
            lock (_sync)
            {
                if (_files.Remove(oldPath, out byte[]? bytes)) _files[newPath] = bytes;
            }

            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            lock (_sync) Created.Add(path);
            return Task.CompletedTask;
        }

        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
        {
            lock (_sync)
            {
                if (_files.TryGetValue(path, out byte[]? bytes)) return Task.FromResult<RemoteFileItem?>(Item(path, bytes.Length));
                if (Existing.Contains(path)) return Task.FromResult<RemoteFileItem?>(Item(path, 4));
                return Task.FromResult<RemoteFileItem?>(null);
            }
        }

        public Task SetLastWriteTimeUtcAsync(string path, DateTime lastWriteTimeUtc, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static RemoteFileItem Item(string path, long size)
            => new(Path.GetFileName(path), path, false, size, DateTimeOffset.UnixEpoch, "-rw-r--r--");
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly object _gate = new();
        private TimeSpan _clock;
        private readonly List<ManualTimer> _timers = [];
        private readonly List<TimeSpan> _registrations = [];
        private readonly List<(int Count, TaskCompletionSource<TimeSpan> Done)> _pending = [];

        public int ActiveTimerCount
        {
            get { lock (_gate) return _timers.Count(timer => !timer.Disposed); }
        }

        public Task<TimeSpan> WhenTimerRegisteredAsync(int count)
        {
            lock (_gate)
            {
                if (_registrations.Count >= count) return Task.FromResult(_registrations[count - 1]);
                TaskCompletionSource<TimeSpan> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add((count, done));
                return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                // 时钟读取、注册与发布信号必须原子化，不能只锁列表写入。
                ManualTimer timer = new(callback, state, _clock + dueTime);
                _timers.Add(timer);
                _registrations.Add(dueTime);
                foreach ((int count, TaskCompletionSource<TimeSpan> done) in _pending.ToArray())
                {
                    if (_registrations.Count < count) continue;
                    _pending.RemoveAll(item => item.Done == done);
                    done.TrySetResult(_registrations[count - 1]);
                }

                return timer;
            }
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _clock += by;
                due = _timers.Where(timer => !timer.Disposed && timer.Due <= _clock).ToList();
            }

            foreach (ManualTimer timer in due) timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            public TimeSpan Due { get; } = due;
            private int _disposed;
            public bool Disposed => Volatile.Read(ref _disposed) != 0;
            public void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
