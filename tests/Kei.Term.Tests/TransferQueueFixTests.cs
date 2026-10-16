using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;

namespace Kei.Term.Tests;

public class TransferQueueFixTests
{
    [Fact]
    public async Task SameFinalName_DoesNotWriteTheSamePartialConcurrently()
    {
        GateFileSystem remote = new();
        await using TransferQueue queue = Queue(remote, concurrency: 2);
        string left = TempDir();
        string right = TempDir();
        try
        {
            queue.Enqueue(Upload(WriteFile(left, "a.txt", "one"), "/remote"));
            queue.Enqueue(Upload(WriteFile(right, "a.txt", "two"), "/remote"));
            await WaitUntilAsync(() => remote.Blocked == 1);
            await Task.Delay(40);
            Assert.Equal(1, remote.Blocked);
            Assert.Equal(["a.txt.keipart"], remote.Started.ToArray());
            remote.Release(2);
            await WaitUntilAsync(() => queue.Tasks.All(task => task.State is QueuedTransferState.Completed or QueuedTransferState.Skipped or QueuedTransferState.Failed));
            Assert.Equal(1, remote.MaxBlocked);
        }
        finally
        {
            Directory.Delete(left, true);
            Directory.Delete(right, true);
        }
    }

    [Fact]
    public async Task FileAndItsPartialName_DoNotTransferTogether()
    {
        GateFileSystem remote = new();
        await using TransferQueue queue = Queue(remote, concurrency: 2);
        string directory = TempDir();
        try
        {
            queue.Enqueue(Upload(WriteFile(directory, "a", "one"), "/remote"));
            queue.Enqueue(Upload(WriteFile(directory, "a.keipart", "two"), "/remote"));
            await WaitUntilAsync(() => remote.Blocked == 1);
            await Task.Delay(40);
            Assert.Equal(1, remote.MaxBlocked);
            remote.Release(2);
            await WaitUntilAsync(() => queue.Tasks.Count(task => task.State == QueuedTransferState.Completed) == 2);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RemoteNames_StayCaseSensitive()
    {
        GateFileSystem remote = new();
        await using TransferQueue queue = Queue(remote, concurrency: 2, localIgnoreCase: true);
        string directory = TempDir();
        try
        {
            queue.Enqueue(Upload(WriteFile(directory, "A", "one"), "/remote"));
            queue.Enqueue(Upload(WriteFile(directory, "a", "two"), "/remote"));
            await WaitUntilAsync(() => remote.Blocked == 2);
            Assert.Equal(2, remote.MaxBlocked);
            remote.Release(2);
            await WaitUntilAsync(() => queue.Tasks.All(task => task.State == QueuedTransferState.Completed));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CaseInsensitiveLocalNames_DoNotDownloadTogether()
    {
        GateFileSystem remote = new();
        remote.Files["/remote/A.txt"] = "A"u8.ToArray();
        remote.Files["/remote/a.txt"] = "a"u8.ToArray();
        await using TransferQueue queue = Queue(remote, concurrency: 2, localIgnoreCase: true);
        string directory = TempDir();
        try
        {
            queue.Enqueue(Download("/remote/A.txt", "A.txt", directory, 1));
            queue.Enqueue(Download("/remote/a.txt", "a.txt", directory, 1));
            await WaitUntilAsync(() => remote.Reads == 1);
            await Task.Delay(40);
            Assert.Equal(1, remote.MaxReads);
            remote.Release(2);
            await WaitUntilAsync(() => queue.Tasks.All(task => !task.IsActive));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Retry_RefreshesSourceFacts_AndRestartsWhenSourceChanged()
    {
        ReplacingFileSystem remote = new();
        ManualTime time = new();
        await using TransferQueue queue = Queue(remote, time: time, retryCount: 1, resume: true);
        string directory = TempDir();
        try
        {
            string file = WriteFile(directory, "a.txt", "AAAA");
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            remote.OnFirstCommit = () =>
            {
                File.WriteAllText(file, "BBBBBB");
                File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));
            };
            QueuedTransfer task = queue.Enqueue(Upload(file, "/remote"));
            await WaitUntilAsync(() => task.State == QueuedTransferState.RetryWait);
            time.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => task.State == QueuedTransferState.Completed);
            Assert.Equal("BBBBBB", remote.Text("/remote/a.txt"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExistingDirectory_IsMerged_FileAtThatPathFails()
    {
        MergeFileSystem remote = new();
        remote.Directories.Add("/remote/proj");
        remote.Directories.Add("/remote/proj/empty");
        await using TransferQueue queue = Queue(remote);
        string directory = TempDir();
        try
        {
            string root = Path.Combine(directory, "proj");
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "hi");
            QueuedTransfer merged = queue.Enqueue(new TransferSubmission(TransferDirection.Upload, "proj", root, "/remote", true));
            await WaitUntilAsync(() => merged.State == QueuedTransferState.Completed);
            Assert.DoesNotContain("/remote/proj", remote.CreateAttempts);
            Assert.Equal("hi", remote.Text("/remote/proj/a.txt"));

            remote.Files["/remote/notdir"] = "x"u8.ToArray();
            string blocked = Path.Combine(directory, "notdir");
            Directory.CreateDirectory(blocked);
            await File.WriteAllTextAsync(Path.Combine(blocked, "b.txt"), "no");
            QueuedTransfer failed = queue.Enqueue(new TransferSubmission(TransferDirection.Upload, "notdir", blocked, "/remote", true));
            await WaitUntilAsync(() => failed.State == QueuedTransferState.Failed);
            Assert.Contains("不是目录", failed.ErrorMessage);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DirectoryRetry_DropsOldChildrenAndSucceeds()
    {
        FlakySecondFile remote = new();
        await using TransferQueue queue = Queue(remote, concurrency: 2);
        string directory = TempDir();
        try
        {
            string root = Path.Combine(directory, "proj");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "A");
            await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "B");
            QueuedTransfer task = queue.Enqueue(new TransferSubmission(TransferDirection.Upload, "proj", root, "/remote", true));
            await WaitUntilAsync(() => task.State == QueuedTransferState.Failed);
            Assert.Equal(1, task.TransferredBytes);
            queue.Retry(task.Id);
            await WaitUntilAsync(() => task.State == QueuedTransferState.Completed);
            Assert.Equal(2, task.TransferredBytes);
            Assert.Equal(2, task.TotalBytes);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Close_DisposesEndpointBeforeBlockedIoReturns()
    {
        BlockingUntilDispose remote = new();
        FileSystemTransferEndpoint endpoint = new(remote, FileTransferProtocol.Sftp, owns: true);
        await using TransferQueue queue = new(endpoint, () => new FileTransferSettings { Concurrency = 1, AutoRetryEnabled = false });
        string directory = TempDir();
        try
        {
            queue.Enqueue(Upload(WriteFile(directory, "a.txt", "data"), "/remote"));
            await WaitUntilAsync(() => remote.Blocked);
            Task closing = queue.CloseAsync();
            await closing;
            Assert.True(remote.DisposedBeforeReturn);
            Assert.True(remote.WriteReturned);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HugeRetryDelay_CanBeCreatedAndCancelledOnSystemTimeProvider()
    {
        FlakyOpen remote = new();
        await using TransferQueue queue = new(
            new FileSystemTransferEndpoint(remote, FileTransferProtocol.Sftp, owns: false),
            () => new FileTransferSettings
            {
                AutoRetryEnabled = true,
                RetryCount = 3,
                BackoffMode = RetryBackoffMode.Fixed,
                InitialRetryIntervalSeconds = int.MaxValue,
                MaxRetryIntervalSeconds = int.MaxValue,
            },
            time: TimeProvider.System);
        string directory = TempDir();
        try
        {
            QueuedTransfer task = queue.Enqueue(Upload(WriteFile(directory, "a.txt", "z"), "/remote"));
            await WaitUntilAsync(() => task.State == QueuedTransferState.RetryWait);
            await queue.CloseAsync();
            Assert.Equal(QueuedTransferState.Cancelled, task.State);
            Assert.DoesNotContain("ArgumentOutOfRange", task.ErrorMessage ?? "");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FailedClaim_CapturesTheWaitThatReleaseCompletes()
    {
        TransferQueue.DestinationClaimGate gate = new();
        string[] keys = ["U:/remote/a", "U:/remote/a.keipart"];
        object holder = new();
        object waiter = new();
        TransferQueue.DestinationClaimGate.Entry held = gate.TryEnter(keys, holder, null);
        TransferQueue.DestinationClaimGate.Entry blocked = gate.TryEnter(keys, waiter, null);
        Assert.Null(blocked.Lease);
        Assert.NotNull(blocked.Wait);
        held.Lease!.Dispose();
        Assert.True(blocked.Wait!.IsCompleted);
        await blocked.Wait.WaitAsync(TimeSpan.FromMilliseconds(1));
        TransferQueue.DestinationClaimGate.Entry after = gate.TryEnter(keys, waiter, null);
        Assert.NotNull(after.Lease);
        Assert.False(after.HistoryValid);
        after.Lease!.Dispose();
        TransferQueue.DestinationClaimGate.Entry stale = gate.TryEnter(keys, holder, held.Token);
        Assert.False(stale.HistoryValid);
    }

    [Fact]
    public async Task InterruptedRetry_DoesNotResumeForeignPartial()
    {
        PartialScript remote = new();
        ManualTime time = new();
        await using TransferQueue queue = Queue(remote, concurrency: 1, time: time, retryCount: 1, resume: true);
        string directory = TempDir();
        try
        {
            string a = WriteFile(directory, "a.txt", "AAAAAA");
            string b = WriteFile(directory, "b.txt", "BBBBBB");
            remote.FailAfterBytes["/remote/a.txt.keipart"] = 2;
            remote.Plan.Enqueue(PartialScript.FailKind.Transient);
            remote.Plan.Enqueue(PartialScript.FailKind.Permanent);
            QueuedTransfer first = queue.Enqueue(Upload(a, "/remote"));
            await WaitUntilAsync(() => first.State == QueuedTransferState.RetryWait);
            QueuedTransfer second = queue.Enqueue(SameDestination(b, "a.txt", "/remote"));
            await WaitUntilAsync(() => second.State == QueuedTransferState.Failed);
            Assert.Equal("BB", remote.Text("/remote/a.txt.keipart"));
            time.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => first.State == QueuedTransferState.Completed);
            Assert.Equal("AAAAAA", remote.Text("/remote/a.txt"));
            Assert.Contains(("/remote/a.txt.keipart", 0L), remote.Offsets);
            Assert.DoesNotContain(("/remote/a.txt.keipart", 2L), remote.Offsets);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SoloRetry_ResumesFromPartialOffset()
    {
        PartialScript remote = new();
        ManualTime time = new();
        await using TransferQueue queue = Queue(remote, concurrency: 1, time: time, retryCount: 1, resume: true);
        string directory = TempDir();
        try
        {
            string file = WriteFile(directory, "a.txt", "AAAAAA");
            remote.FailAfterBytes["/remote/a.txt.keipart"] = 2;
            remote.Transient["/remote/a.txt.keipart"] = 1;
            QueuedTransfer task = queue.Enqueue(Upload(file, "/remote"));
            await WaitUntilAsync(() => task.State == QueuedTransferState.RetryWait);
            time.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => task.State == QueuedTransferState.Completed);
            Assert.Equal("AAAAAA", remote.Text("/remote/a.txt"));
            Assert.Equal([("/remote/a.txt.keipart", 0L), ("/remote/a.txt.keipart", 2L)], remote.Offsets);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OverlappingPartialName_InvalidatesResumeHistory()
    {
        PartialScript remote = new();
        ManualTime time = new();
        await using TransferQueue queue = Queue(remote, concurrency: 1, time: time, retryCount: 1, resume: true);
        string directory = TempDir();
        try
        {
            string a = WriteFile(directory, "a", "AAAAAA");
            string partialName = WriteFile(directory, "a.keipart", "CCCCCC");
            remote.FailAfterBytes["/remote/a.keipart"] = 2;
            remote.Transient["/remote/a.keipart"] = 1;
            remote.Permanent.Add("/remote/a.keipart.keipart");
            remote.FailAfterBytes["/remote/a.keipart.keipart"] = 2;
            QueuedTransfer first = queue.Enqueue(Upload(a, "/remote"));
            await WaitUntilAsync(() => first.State == QueuedTransferState.RetryWait);
            QueuedTransfer second = queue.Enqueue(Upload(partialName, "/remote"));
            await WaitUntilAsync(() => second.State == QueuedTransferState.Failed);
            time.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => first.State == QueuedTransferState.Completed);
            Assert.Equal("AAAAAA", remote.Text("/remote/a"));
            Assert.DoesNotContain(("/remote/a.keipart", 2L), remote.Offsets);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SourceChangeRetry_TimesOutBeforePartialReset_DoesNotResumeOldBytes()
    {
        PartialScript remote = new();
        ManualTime time = new();
        await using TransferQueue queue = Queue(remote, concurrency: 1, time: time, retryCount: 2, resume: true);
        string directory = TempDir();
        try
        {
            string file = WriteFile(directory, "a.txt", "AAAAAA");
            SetUtc(file, 2026, 1, 1);
            remote.FailAfterBytes["/remote/a.txt.keipart"] = 2;
            remote.Transient["/remote/a.txt.keipart"] = 1;
            QueuedTransfer task = queue.Enqueue(Upload(file, "/remote"));
            await WaitUntilAsync(() => task.State == QueuedTransferState.RetryWait);
            Assert.Equal("AA", remote.Text("/remote/a.txt.keipart"));
            File.WriteAllText(file, "BBBBBB");
            SetUtc(file, 2026, 2, 1);
            remote.FailParentChecks = 1;
            time.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => task.State == QueuedTransferState.RetryWait && task.ErrorMessage == "parent");
            Assert.Equal("AA", remote.Text("/remote/a.txt.keipart"));
            time.Advance(TimeSpan.FromSeconds(4));
            await WaitUntilAsync(() => task.State == QueuedTransferState.Completed);
            Assert.Equal("BBBBBB", remote.Text("/remote/a.txt"));
            Assert.Equal(
                [("/remote/a.txt.keipart", 0L), ("/remote/a.txt.keipart", 0L)],
                remote.Offsets);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PathCheckFailure_DoesNotConfirmHistory()
    {
        PartialScript remote = new();
        ManualTime time = new();
        await using TransferQueue queue = Queue(remote, concurrency: 1, time: time, retryCount: 1, resume: true);
        string directory = TempDir();
        try
        {
            string file = WriteFile(directory, "a.txt", "AAAAAA");
            remote.FailParentChecks = 1;
            QueuedTransfer task = queue.Enqueue(Upload(file, "/remote"));
            await WaitUntilAsync(() => task.State == QueuedTransferState.RetryWait);
            Assert.Empty(remote.Offsets);
            remote.Plant("/remote/a.txt.keipart", "XX");
            time.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => task.State == QueuedTransferState.Completed);
            Assert.Equal("AAAAAA", remote.Text("/remote/a.txt"));
            Assert.Equal([("/remote/a.txt.keipart", 0L)], remote.Offsets);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task QueueSnapshot_HistoryBindsExecutorLiveSource()
    {
        PartialScript remote = new();
        ManualTime time = new();
        await using TransferQueue queue = Queue(remote, concurrency: 1, time: time, retryCount: 1, resume: true);
        string directory = TempDir();
        try
        {
            string file = WriteFile(directory, "a.txt", "AAAAAA");
            SetUtc(file, 2026, 1, 1);
            remote.FailAfterBytes["/remote/a.txt.keipart"] = 2;
            remote.Transient["/remote/a.txt.keipart"] = 1;
            remote.OnParentCheck = () =>
            {
                if (File.ReadAllText(file) == "AAAAAA")
                {
                    File.WriteAllText(file, "BBBBBB");
                    SetUtc(file, 2026, 3, 1);
                }
            };
            QueuedTransfer task = queue.Enqueue(Upload(file, "/remote"));
            await WaitUntilAsync(() => task.State == QueuedTransferState.RetryWait);
            Assert.Equal("BB", remote.Text("/remote/a.txt.keipart"));
            time.Advance(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => task.State == QueuedTransferState.Completed);
            Assert.Equal("BBBBBB", remote.Text("/remote/a.txt"));
            Assert.Equal(
                [("/remote/a.txt.keipart", 0L), ("/remote/a.txt.keipart", 2L)],
                remote.Offsets);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FileSystemEndpoint_ConnectsOnceAndDisposeIsIdempotent()
    {
        ConnectTrackingFileSystem remote = new();
        FileSystemTransferEndpoint endpoint = new(remote, FileTransferProtocol.Scp, owns: true);
        Assert.Equal(0, remote.Connects);
        await Task.WhenAll(endpoint.AcquireAsync(), endpoint.AcquireAsync());
        Assert.Equal(1, remote.Connects);
        await endpoint.DisposeAsync();
        await endpoint.DisposeAsync();
        Assert.Equal(1, remote.Disposes);
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => endpoint.AcquireAsync());
    }

    private static TransferQueue Queue(
        IRemoteFileSystem remote,
        int concurrency = 2,
        TimeProvider? time = null,
        int retryCount = 0,
        bool resume = false,
        bool? localIgnoreCase = null)
        => new(
            new FileSystemTransferEndpoint(remote, FileTransferProtocol.Sftp, owns: false),
            () => new FileTransferSettings
            {
                Concurrency = concurrency,
                AutoRetryEnabled = retryCount > 0,
                RetryCount = retryCount,
                ResumeEnabled = resume,
                BackoffMode = RetryBackoffMode.Exponential,
                InitialRetryIntervalSeconds = 2,
                MaxRetryIntervalSeconds = 60,
                DefaultConflictPolicy = TransferConflictPolicy.Overwrite,
            },
            time: time,
            localDestinationIgnoreCase: localIgnoreCase);

    private static TransferSubmission Upload(string path, string remoteDirectory)
    {
        FileInfo info = new(path);
        return new(TransferDirection.Upload, info.Name, path, remoteDirectory, false, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
    }

    private static TransferSubmission SameDestination(string path, string name, string remoteDirectory)
    {
        FileInfo info = new(path);
        return new(TransferDirection.Upload, name, path, remoteDirectory, false, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
    }

    private static TransferSubmission Download(string remotePath, string name, string localDirectory, long size)
        => new(TransferDirection.Download, name, localDirectory, remotePath, false, size, DateTimeOffset.UnixEpoch);

    private static void SetUtc(string path, int year, int month, int day)
        => File.SetLastWriteTimeUtc(path, new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc));

    private static string WriteFile(string directory, string name, string contents)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string TempDir()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-queue-fix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(15, timeout.Token);
    }

    private class MemoryRemote : IRemoteFileSystem
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public bool IsConnected { get; set; } = true;
        public string WorkingDirectory => "/";
        public bool SupportsResume => true;
        public virtual Task ConnectAsync(CancellationToken ct = default) { IsConnected = true; return Task.CompletedTask; }
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RemoteFileItem>>([]);
        public virtual Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(Files.GetValueOrDefault(path) ?? []));
        public virtual Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
        {
            MemoryStream stream = new();
            lock (Files) Open[stream] = path;
            return Task.FromResult<Stream>(stream);
        }
        protected Dictionary<Stream, string> Open { get; } = [];
        public virtual Task<Stream> OpenWriteAsync(string path, long offset, CancellationToken ct = default)
            => OpenWriteAsync(path, ct);
        public virtual Task CommitWriteAsync(Stream stream, CancellationToken ct = default)
        {
            if (stream is MemoryStream memory && Open.TryGetValue(stream, out string? path)) Files[path] = memory.ToArray();
            return Task.CompletedTask;
        }
        public virtual Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) { Files.Remove(path); return Task.CompletedTask; }
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => RenameOverwriteAsync(oldPath, newPath, ct);
        public virtual Task RenameOverwriteAsync(string oldPath, string newPath, CancellationToken ct = default)
        {
            if (Files.Remove(oldPath, out byte[]? bytes)) Files[newPath] = bytes;
            return Task.CompletedTask;
        }
        public virtual Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;
        public virtual Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
            => Task.FromResult(Files.TryGetValue(path, out byte[]? bytes) ? Item(path, false, bytes.Length) : null);
        public virtual Task<RemoteFileItem?> GetAttributesAsync(string path, CancellationToken ct = default)
            => GetItemAsync(path, ct);
        public Task SetLastWriteTimeUtcAsync(string path, DateTime lastWriteTimeUtc, CancellationToken ct = default) => Task.CompletedTask;
        public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public string Text(string path) => Files.TryGetValue(path, out byte[]? bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : "";
        protected static RemoteFileItem Item(string path, bool directory, long size)
            => new(Path.GetFileName(path), path, directory, size, DateTimeOffset.UnixEpoch, directory ? "drwxr-xr-x" : "-rw-r--r--");
    }

    private sealed class GateFileSystem : MemoryRemote
    {
        private readonly SemaphoreSlim _gate = new(0);
        public List<string> Started { get; } = [];
        public int Blocked;
        public int MaxBlocked;
        public int Reads;
        public int MaxReads;
        public void Release(int count = 1) { for (int i = 0; i < count; i++) _gate.Release(); }
        public override async Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
        {
            lock (Started) Started.Add(Path.GetFileName(path) ?? path);
            int blocked = Interlocked.Increment(ref Blocked);
            MaxBlocked = Math.Max(MaxBlocked, blocked);
            try { await _gate.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref Blocked); }
            return await base.OpenWriteAsync(path, ct);
        }
        public override async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        {
            int reads = Interlocked.Increment(ref Reads);
            MaxReads = Math.Max(MaxReads, reads);
            try { await _gate.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref Reads); }
            return await base.OpenReadAsync(path, ct);
        }
    }

    private sealed class ReplacingFileSystem : MemoryRemote
    {
        public Action? OnFirstCommit { get; set; }
        private int _commits;
        public override Task CommitWriteAsync(Stream stream, CancellationToken ct = default)
        {
            base.CommitWriteAsync(stream, ct);
            if (Interlocked.Increment(ref _commits) == 1)
            {
                OnFirstCommit?.Invoke();
                throw new TimeoutException("reset");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class MergeFileSystem : MemoryRemote
    {
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
        public List<string> CreateAttempts { get; } = [];
        public override Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            CreateAttempts.Add(path);
            if (Directories.Contains(path) || Files.ContainsKey(path)) throw new IOException("already exists");
            Directories.Add(path);
            return Task.CompletedTask;
        }
        public override Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
        {
            if (Files.ContainsKey(path)) return Task.FromResult<RemoteFileItem?>(Item(path, false, Files[path].Length));
            if (Directories.Contains(path)) return Task.FromResult<RemoteFileItem?>(Item(path, true, 0));
            return Task.FromResult<RemoteFileItem?>(null);
        }
    }

    private sealed class FlakySecondFile : MemoryRemote
    {
        private int _bWrites;
        public override Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
        {
            if (path.EndsWith("/b.txt.keipart", StringComparison.Ordinal) && Interlocked.Increment(ref _bWrites) == 1)
            {
                throw new UnauthorizedAccessException("denied");
            }

            return base.OpenWriteAsync(path, ct);
        }
    }

    private sealed class BlockingUntilDispose : MemoryRemote
    {
        private readonly TaskCompletionSource _dispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Blocked { get; private set; }
        public bool Disposed { get; private set; }
        public bool WriteReturned { get; private set; }
        public bool DisposedBeforeReturn { get; private set; }
        public override async Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
        {
            Blocked = true;
            await _dispose.Task;
            DisposedBeforeReturn = Disposed;
            WriteReturned = true;
            throw new IOException("closed");
        }
        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            _dispose.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PartialScript : MemoryRemote, IRemoteFileSystem
    {
        public enum FailKind { Transient, Permanent }
        public Dictionary<string, int> FailAfterBytes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Transient { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Permanent { get; } = new(StringComparer.Ordinal);
        public Queue<FailKind> Plan { get; } = new();
        public List<(string Path, long Offset)> Offsets { get; } = [];
        public int FailParentChecks { get; set; }
        public Action? OnParentCheck { get; set; }
        private readonly Dictionary<string, List<byte>> _store = new(StringComparer.Ordinal);

        public void Plant(string path, string text) => _store[path] = System.Text.Encoding.UTF8.GetBytes(text).ToList();

        public override Task<RemoteFileItem?> GetAttributesAsync(string path, CancellationToken ct = default)
        {
            if (path == "/remote")
            {
                OnParentCheck?.Invoke();
                if (FailParentChecks > 0)
                {
                    FailParentChecks--;
                    throw new TimeoutException("parent");
                }
            }

            return base.GetAttributesAsync(path, ct);
        }

        public override Task<Stream> OpenWriteAsync(string path, long offset, CancellationToken ct = default)
        {
            lock (Offsets) Offsets.Add((path, offset));
            if (!_store.TryGetValue(path, out List<byte>? bytes))
            {
                bytes = [];
                _store[path] = bytes;
            }

            if (offset == 0) bytes.Clear();
            return Task.FromResult<Stream>(new ScriptStream(this, path, bytes));
        }

        public override Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
            => OpenWriteAsync(path, 0, ct);

        public override Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
        {
            _store.Remove(path);
            return base.DeleteAsync(path, isDirectory, ct);
        }

        public override Task RenameOverwriteAsync(string oldPath, string newPath, CancellationToken ct = default)
        {
            if (_store.Remove(oldPath, out List<byte>? bytes)) Files[newPath] = bytes.ToArray();
            return Task.CompletedTask;
        }

        public override Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
        {
            if (Files.TryGetValue(path, out byte[]? final)) return Task.FromResult<RemoteFileItem?>(Item(path, false, final.Length));
            if (_store.TryGetValue(path, out List<byte>? partial) && partial.Count > 0)
            {
                return Task.FromResult<RemoteFileItem?>(Item(path, false, partial.Count));
            }

            return Task.FromResult<RemoteFileItem?>(null);
        }

        public new string Text(string path)
        {
            if (Files.TryGetValue(path, out byte[]? final)) return System.Text.Encoding.UTF8.GetString(final);
            if (_store.TryGetValue(path, out List<byte>? partial)) return System.Text.Encoding.UTF8.GetString(partial.ToArray());
            return "";
        }

        private sealed class ScriptStream(PartialScript owner, string path, List<byte> bytes) : Stream
        {
            private int _written;
            public override bool CanWrite => true;
            public override void Write(byte[] buffer, int offset, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    bytes.Add(buffer[offset + i]);
                    _written++;
                    if (owner.FailAfterBytes.TryGetValue(path, out int limit) && _written >= limit && TakeFailure() is Exception failure)
                    {
                        throw failure;
                    }
                }
            }

            private Exception? TakeFailure()
            {
                if (owner.Plan.Count > 0)
                {
                    return owner.Plan.Dequeue() == FailKind.Permanent
                        ? new UnauthorizedAccessException("denied")
                        : new TimeoutException("reset");
                }

                if (owner.Permanent.Contains(path)) return new UnauthorizedAccessException("denied");
                if (owner.Transient.TryGetValue(path, out int left) && left > 0)
                {
                    owner.Transient[path] = left - 1;
                    return new TimeoutException("reset");
                }

                return null;
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }

            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override long Length => bytes.Count;
            public override long Position { get; set; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class FlakyOpen : MemoryRemote
    {
        public override Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
            => Task.FromException<Stream>(new TimeoutException("reset"));
    }

    private sealed class ConnectTrackingFileSystem : MemoryRemote
    {
        public int Connects;
        public int Disposes;
        public override async Task ConnectAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Connects);
            await Task.Delay(50, ct);
            IsConnected = true;
        }
        public ConnectTrackingFileSystem() { IsConnected = false; }
        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposes);
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly object _gate = new();
        private TimeSpan _clock;
        private readonly List<ManualTimer> _timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(callback, state, _clock + dueTime);
            lock (_gate) _timers.Add(timer);
            return timer;
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
            public bool Disposed { get; private set; }
            public void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
