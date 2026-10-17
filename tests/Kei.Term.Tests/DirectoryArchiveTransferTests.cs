using System.Diagnostics;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;

namespace Kei.Term.Tests;

public sealed class DirectoryArchiveTransferTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("keiterm-archive-execution-test-").FullName;

    [Theory]
    [InlineData(TransferConflictPolicy.Overwrite)]
    [InlineData(TransferConflictPolicy.Skip)]
    [InlineData(TransferConflictPolicy.AutoRename)]
    [InlineData(TransferConflictPolicy.Ask)]
    public async Task Queue_ResolvesDirectoryAsOneConflict(TransferConflictPolicy policy)
    {
        if (!OperatingSystem.IsLinux()) return;
        (string source, string parent) = await CreateTreeAsync();
        Directory.CreateDirectory(Path.Combine(parent, "tree"));
        await File.WriteAllTextAsync(Path.Combine(parent, "tree", "file"), "old");
        await File.WriteAllTextAsync(Path.Combine(parent, "tree", "extra"), "keep");
        FileTransferSettings settings = Settings();
        settings.DefaultConflictPolicy = policy;
        LocalEndpoint endpoint = new();
        int prompts = 0;
        await using TransferQueue queue = new(endpoint, () => settings, capabilities: new TarCapabilities(),
            promptConflict: (_, _, _, _) =>
            {
                prompts++;
                return Task.FromResult<TransferConflictChoice?>(new(TransferConflictPolicy.Overwrite, false));
            });
        QueuedTransfer task = queue.Enqueue(new(TransferDirection.Upload, "tree", source, parent, true));
        await FinishAsync(task);
        Assert.Equal(policy == TransferConflictPolicy.Skip ? QueuedTransferState.Skipped : QueuedTransferState.Completed, task.State);
        Assert.Equal(policy == TransferConflictPolicy.Ask ? 1 : 0, prompts);
        string result = Path.Combine(parent, policy == TransferConflictPolicy.AutoRename ? "tree (1)" : "tree");
        Assert.Equal(policy == TransferConflictPolicy.Skip ? "old" : "payload", await File.ReadAllTextAsync(Path.Combine(result, "file")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(parent, "tree", "extra")));
        Assert.All(endpoint.Shell.TemporaryDirectories, directory => Assert.False(Directory.Exists(directory)));
    }

    [Theory]
    [InlineData(false, TransferDirection.Upload)]
    [InlineData(true, TransferDirection.Upload)]
    [InlineData(false, TransferDirection.Download)]
    [InlineData(true, TransferDirection.Download)]
    public async Task FailureOrCancellation_DeletesLocalAndRemoteTemporaryFiles(bool cancel, TransferDirection direction)
    {
        if (!OperatingSystem.IsLinux()) return;
        (string source, string parent) = await CreateTreeAsync();
        string localTemp = Path.Combine(_root, "local-temporary");
        LocalEndpoint endpoint = new();
        DirectoryArchiveTransfer archive = new(new FileTransferExecutor(), () => Directory.CreateDirectory(localTemp).FullName);
        using CancellationTokenSource cancellation = new();
        TransferSubmission submission = direction == TransferDirection.Upload
            ? new(direction, "tree", source, parent, true) : new(direction, "tree", parent, source, true);
        Task execute = archive.ExecuteAsync(submission,
            Path.Combine(parent, "tree"), false, TransferArchiveFormat.Tar, endpoint.FileSystem,
            endpoint.Shell, PosixToolset.Instance, Settings(), null, _ => { }, _ => { },
            afterCopy: _ =>
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw new TimeoutException("模拟复制后连接超时");
            }, ct: cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execute);
        else await Assert.ThrowsAsync<TimeoutException>(() => execute);
        Assert.False(Directory.Exists(localTemp));
        Assert.NotEmpty(endpoint.Shell.TemporaryDirectories);
        Assert.All(endpoint.Shell.TemporaryDirectories, directory => Assert.False(Directory.Exists(directory)));
        Assert.False(Directory.Exists(Path.Combine(parent, "tree")));
    }

    [Fact]
    public async Task UnpackingFailure_CleansBothRemoteWorkspaces()
    {
        if (!OperatingSystem.IsLinux()) return;
        (string source, string parent) = await CreateTreeAsync();
        Directory.CreateSymbolicLink(Path.Combine(parent, "tree"), source);
        string localTemp = Path.Combine(_root, "local-temporary");
        LocalEndpoint endpoint = new();
        DirectoryArchiveTransfer archive = new(new FileTransferExecutor(), () => Directory.CreateDirectory(localTemp).FullName);
        await Assert.ThrowsAsync<TransferPathRejectedException>(() => archive.ExecuteAsync(
            new(TransferDirection.Upload, "tree", source, parent, true), Path.Combine(parent, "tree"), false,
            TransferArchiveFormat.Tar, endpoint.FileSystem, endpoint.Shell, PosixToolset.Instance, Settings(), null, _ => { }, _ => { }));
        Assert.False(Directory.Exists(localTemp));
        Assert.Equal(2, endpoint.Shell.TemporaryDirectories.Count);
        Assert.All(endpoint.Shell.TemporaryDirectories, directory => Assert.False(Directory.Exists(directory)));
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(source, "file")));
    }

    [Fact]
    public async Task TransientFailure_RetriesEntireArchive_AndPublishesPackingVerifyingUnpacking()
    {
        if (!OperatingSystem.IsLinux()) return;
        (string source, string parent) = await CreateTreeAsync();
        FileTransferSettings settings = Settings();
        settings.VerifyHashEnabled = true;
        settings.AutoRetryEnabled = true;
        settings.RetryCount = 1;
        settings.InitialRetryIntervalSeconds = 0;
        settings.MaxRetryIntervalSeconds = 0;
        LocalEndpoint endpoint = new();
        int copies = 0;
        await using TransferQueue queue = new(endpoint, () => settings, capabilities: new TarCapabilities(), afterCopy: _ =>
        {
            if (Interlocked.Increment(ref copies) == 1)
            {
                // 第二次重新打包必须看到源变化，不能拿旧归档或半解包结果退回逐文件。
                File.WriteAllText(Path.Combine(source, "file"), "changed-on-retry");
                throw new TimeoutException("暂时失败");
            }
            return Task.CompletedTask;
        });
        QueuedTransfer task = queue.Enqueue(new(TransferDirection.Upload, "tree", source, parent, true));
        HashSet<QueuedTransferState> states = [];
        task.Updated += () => { lock (states) states.Add(task.State); };
        await FinishAsync(task);
        Assert.Equal(QueuedTransferState.Completed, task.State);
        Assert.Equal(2, copies);
        Assert.Equal("changed-on-retry", await File.ReadAllTextAsync(Path.Combine(parent, "tree", "file")));
        Assert.False(task.IsUnverified);
        lock (states)
        {
            Assert.Contains(QueuedTransferState.Packing, states);
            Assert.Contains(QueuedTransferState.Verifying, states);
            Assert.Contains(QueuedTransferState.Unpacking, states);
        }
        Assert.All(endpoint.Shell.TemporaryDirectories, directory => Assert.False(Directory.Exists(directory)));
    }

    [Fact]
    public async Task OlderOnly_FallsBack_WithoutPacking_AndArchiveOverrideCanDisableGlobal()
    {
        if (!OperatingSystem.IsLinux()) return;
        (string source, string parent) = await CreateTreeAsync();
        FileTransferSettings settings = Settings();
        settings.DefaultConflictPolicy = TransferConflictPolicy.OverwriteIfOlder;
        LocalEndpoint endpoint = new();
        await using TransferQueue queue = new(endpoint, () => settings, capabilities: new TarCapabilities());
        QueuedTransfer task = queue.Enqueue(new(TransferDirection.Upload, "tree", source, parent, true));
        await FinishAsync(task);
        Assert.Equal(QueuedTransferState.Completed, task.State);
        Assert.Equal(14, task.TotalBytes);
        Assert.Empty(endpoint.Shell.TemporaryDirectories);
        settings.DefaultConflictPolicy = TransferConflictPolicy.Overwrite;
        await using TransferQueue disabled = new(endpoint, () => settings, capabilities: new TarCapabilities(), archiveDirectoriesOverride: () => false);
        QueuedTransfer individual = disabled.Enqueue(new(TransferDirection.Upload, "individual", source, parent, true));
        await FinishAsync(individual);
        Assert.Equal(QueuedTransferState.Completed, individual.State);
        Assert.Equal(14, individual.TotalBytes);
        Assert.Empty(endpoint.Shell.TemporaryDirectories);
    }

    private async Task<(string Source, string Parent)> CreateTreeAsync()
    {
        string source = Path.Combine(_root, "source");
        string parent = Path.Combine(_root, "parent");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(parent);
        await File.WriteAllTextAsync(Path.Combine(source, "file"), "payload");
        await File.WriteAllTextAsync(Path.Combine(source, "second"), "payload");
        return (source, parent);
    }

    private static FileTransferSettings Settings() => new()
    {
        ArchiveDirectoriesEnabled = true, VerifyHashEnabled = false, ArchiveFormatOrder = [TransferArchiveFormat.Tar],
        DefaultConflictPolicy = TransferConflictPolicy.Overwrite, AutoRetryEnabled = false,
    };

    private static async Task FinishAsync(QueuedTransfer task)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (task.IsActive) await Task.Delay(10, timeout.Token);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class TarCapabilities : ITransferCapabilityProvider
    {
        public Task<TransferProbeResult> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TransferProbeResult(RemotePlatform.Linux, [TransferHashAlgorithm.Sha256], [TransferArchiveFormat.Tar], DateTime.UtcNow));
    }

    // 用真实本地文件和 /bin/sh 执行生产工具集命令，不回显 mock 常量来替代打包/解包结果。
    private sealed class LocalEndpoint : ITransferEndpoint
    {
        public LocalFiles FileSystem { get; } = new();
        public ProcessShell Shell { get; } = new();
        public FileTransferProtocol Protocol => FileTransferProtocol.Sftp;
        public Task<IRemoteTransferLease> AcquireAsync(CancellationToken ct = default) => Task.FromResult<IRemoteTransferLease>(new Lease(FileSystem));
        public Task<IRemoteShell?> AcquireShellAsync(CancellationToken ct = default) => Task.FromResult<IRemoteShell?>(Shell);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Lease(IRemoteFileSystem fileSystem) : IRemoteTransferLease
        {
            public IRemoteFileSystem FileSystem => fileSystem;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ProcessShell : IRemoteShell
    {
        public List<string> TemporaryDirectories { get; } = [];
        public async Task<RemoteCommandResult> RunAsync(string command, CancellationToken cancellationToken = default)
        {
            ProcessStartInfo start = new("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string text = await output;
            string possibleDirectory = text.TrimEnd('\r', '\n');
            if (possibleDirectory.StartsWith('/') && Directory.Exists(possibleDirectory)) TemporaryDirectories.Add(possibleDirectory);
            return new(text, await error, process.ExitCode);
        }
    }

    private sealed class LocalFiles : IRemoteFileSystem
    {
        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public bool SupportsResume => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public async Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
        {
            List<RemoteFileItem> result = [];
            foreach (string child in Directory.GetFileSystemEntries(path)) result.Add((await GetItemAsync(child, ct))!);
            return result;
        }
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Task.FromResult<Stream>(File.OpenRead(path));
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => Task.FromResult<Stream>(File.Create(path));
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
        {
            if (isDirectory) Directory.Delete(path, true); else File.Delete(path);
            return Task.CompletedTask;
        }
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => RenameOverwriteAsync(oldPath, newPath, ct);
        public Task RenameOverwriteAsync(string oldPath, string newPath, CancellationToken ct = default)
        {
            File.Move(oldPath, newPath, true);
            return Task.CompletedTask;
        }
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            Directory.CreateDirectory(path);
            return Task.CompletedTask;
        }
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists && info.LinkTarget == null) return Task.FromResult<RemoteFileItem?>(null);
            bool link = info.LinkTarget != null;
            FileSystemInfo? target = link ? info.ResolveLinkTarget(true) : info;
            bool directory = !link && info is DirectoryInfo;
            return Task.FromResult<RemoteFileItem?>(new RemoteFileItem(info.Name, path, directory,
                info is FileInfo file && file.Exists ? file.Length : 0, new DateTimeOffset(info.LastWriteTimeUtc),
                link ? "lrwxrwxrwx" : directory ? "drwxrwxrwx" : "-rw-rw-rw-", IsSymbolicLink: link,
                LinkTargetIsDirectory: link ? target is DirectoryInfo : null,
                LinkTargetSize: link && target is FileInfo targetFile && targetFile.Exists ? targetFile.Length : null,
                IsBrokenSymbolicLink: link && target?.Exists != true));
        }
        public Task SetLastWriteTimeUtcAsync(string path, DateTime lastWriteTimeUtc, CancellationToken ct = default)
        {
            File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
