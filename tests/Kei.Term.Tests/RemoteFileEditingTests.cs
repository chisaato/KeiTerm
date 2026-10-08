using System.Text;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.Tests;

public class RemoteFileEditingTests
{
    [Fact]
    public async Task LauncherExitAndIdleFile_DoNotEndEditingOrDeleteCache()
    {
        string directory = NewDirectory();
        try
        {
            await using LocalFileTracker tracker = new(directory, FileWatcherMode.Polling, 1, 200);
            BufferedFileSystem remote = new();
            FileEditorLauncher launcher = new(tracker);
            ExternalEditor editor = new()
            {
                Name = "short lived launcher",
                ArgumentsTemplate = OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\"",
                Paths = [new(Guid.NewGuid(), Guid.Empty, "any", OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")]
            };
            Guid session = Guid.NewGuid();
            RemoteFileItem item = new("edit.txt", "/edit.txt", false, 8, DateTimeOffset.UtcNow, "-rw-------");
            await launcher.OpenAndTrackAsync(session, item, remote, overrideEditor: editor);
            string localPath = tracker.GetLocalCachePath(session, item.FullPath);

            // 模拟编辑器把文件读入内存后空闲，启动器已退出，文件也没有被持续持有。
            await Task.Delay(TimeSpan.FromSeconds(6.5));
            Assert.Equal("original", await File.ReadAllTextAsync(localPath));
            TaskCompletionSource<LocalFileChangedEventArgs> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            tracker.FileChanged += (_, e) => changed.TrySetResult(e);
            await File.WriteAllTextAsync(localPath, "saved after launcher exit");
            LocalFileChangedEventArgs notification = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(item.FullPath, notification.RemotePath);
            Assert.Equal(localPath, notification.LocalFilePath);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public Task Upload_CompletesOnlyAfterRemoteCommit() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = NewDirectory();
        BufferedFileSystem remote = new() { PauseCommit = true };
        await using RemoteFileManagerViewModel manager = new(Guid.NewGuid(), remote, new ManualTracker());
        try
        {
            string file = Path.Combine(directory, "upload.txt");
            await File.WriteAllTextAsync(file, "replacement");
            await manager.UploadLocalFilesAsync([file]);
            await remote.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            FileTransferTaskItemViewModel transfer = Assert.Single(manager.TransferTasks);
            Assert.Equal(FileTransferState.Transferring, transfer.State);
            Assert.Equal("original", remote.Contents);

            remote.AllowCommit.TrySetResult();
            await WaitUntilAsync(() => transfer.State == FileTransferState.Completed);
            Assert.Equal("replacement", remote.Contents);
        }
        finally
        {
            remote.AllowCommit.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    });

    [Fact]
    public Task FailedCommit_ReportsFailedTransferAndKeepsOldRemoteContents() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = NewDirectory();
        BufferedFileSystem remote = new() { FailCommit = true };
        await using RemoteFileManagerViewModel manager = new(Guid.NewGuid(), remote, new ManualTracker());
        try
        {
            string file = Path.Combine(directory, "upload.txt");
            await File.WriteAllTextAsync(file, "replacement");
            await manager.UploadLocalFilesAsync([file]);
            FileTransferTaskItemViewModel transfer = Assert.Single(manager.TransferTasks);
            await WaitUntilAsync(() => transfer.State == FileTransferState.Failed);
            Assert.Equal("original", remote.Contents);
            Assert.Contains("commit denied", transfer.StatusText);
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public Task Dispose_DrainsActiveWritebackAndUnsubscribesTracker() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = NewDirectory();
        BufferedFileSystem remote = new() { PauseCommit = true };
        ManualTracker tracker = new();
        RemoteFileManagerViewModel manager = new(Guid.NewGuid(), remote, tracker);
        try
        {
            string localPath = Path.Combine(directory, "edit.txt");
            await File.WriteAllTextAsync(localPath, "edited remotely");
            tracker.NotifyChange(localPath, "/edit.txt");
            await remote.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task closing = manager.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.Equal(0, remote.Disposals);
            Assert.Equal(0, tracker.Subscribers);
            remote.AllowCommit.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            await manager.DisposeAsync();
            Assert.Equal(1, tracker.Disposals);
            Assert.Equal(1, remote.Disposals);
            Assert.Equal("edited remotely", remote.Contents);
            Assert.True(File.Exists(localPath));

            tracker.NotifyChange(localPath, "/edit.txt");
            HeadlessAvalonia.Pump();
            Assert.Equal(1, remote.OpenWrites);
        }
        finally
        {
            remote.AllowCommit.TrySetResult();
            await manager.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    });

    [Fact]
    public Task Dispose_StopsOwnedPollingTracker() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = NewDirectory();
        try
        {
            LocalFileTracker tracker = new(directory, FileWatcherMode.Polling, 1, 200);
            RemoteFileManagerViewModel manager = new(Guid.NewGuid(), new BufferedFileSystem(), tracker);
            await manager.DisposeAsync();
            string path = Path.Combine(directory, "after-close.txt");
            await File.WriteAllTextAsync(path, "cache survives close");
            await Assert.ThrowsAsync<ObjectDisposedException>(() => tracker.RegisterTrackedFileAsync(Guid.NewGuid(), "/after-close.txt", path));
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-editing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    // 以显式提交模拟 SCP 的远端可见性；生产 VM 仍执行真实本地文件读取和流复制。
    private sealed class BufferedFileSystem : IRemoteFileSystem
    {
        public string Contents { get; private set; } = "original";
        public bool PauseCommit { get; init; }
        public bool FailCommit { get; init; }
        public int Disposals { get; private set; }
        public int OpenWrites { get; private set; }
        public TaskCompletionSource CommitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConnected => Disposals == 0;
        public string WorkingDirectory => "/";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RemoteFileItem>>([]);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(Contents)));
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
        {
            OpenWrites++;
            return Task.FromResult<Stream>(new MemoryStream());
        }
        public async Task CommitWriteAsync(Stream stream, CancellationToken ct = default)
        {
            CommitStarted.TrySetResult();
            // 模拟已提交到不支持中断的底层传输；释放必须等这段工作结束。
            if (PauseCommit) await AllowCommit.Task;
            if (FailCommit) throw new IOException("commit denied");
            Assert.Equal(0, Disposals);
            Contents = Encoding.UTF8.GetString(((MemoryStream)stream).ToArray());
        }
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => Task.FromResult<RemoteFileItem?>(null);
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class ManualTracker : ILocalFileTracker
    {
        private EventHandler<LocalFileChangedEventArgs>? _changed;
        private EventHandler<string>? _untracked;
        public int Disposals { get; private set; }
        public int Subscribers => (_changed?.GetInvocationList().Length ?? 0) + (_untracked?.GetInvocationList().Length ?? 0);
        public event EventHandler<LocalFileChangedEventArgs>? FileChanged { add => _changed += value; remove => _changed -= value; }
        public event EventHandler<string>? FileUntracked { add => _untracked += value; remove => _untracked -= value; }
        public string GetLocalCachePath(Guid sessionId, string remotePath) => remotePath;
        public Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnregisterTrackedFileAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        public void NotifyChange(string localPath, string remotePath) => _changed?.Invoke(this, new LocalFileChangedEventArgs
        {
            LocalFilePath = localPath,
            RemotePath = remotePath,
            NewHash = [],
            DetectedAt = DateTimeOffset.UtcNow
        });
    }
}
