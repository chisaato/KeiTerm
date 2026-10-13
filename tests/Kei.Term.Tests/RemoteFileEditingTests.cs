using System.Text;
using System.Runtime.Versioning;
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
            await using FileEditorLauncher launcher = new(tracker);
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

    [PlatformFact(TestPlatform.MacOS)]
    public Task WaitEditorClose_FlushesLastSaveAndRemovesMonitoringOnlyAfterClose() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = NewDirectory();
        BufferedFileSystem remote = new() { PauseCommit = true };
        RemoteFileManagerViewModel? manager = null;
        try
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            string executable = await CreateWaitEditorAsync(directory, "printf 'last save' > \"$file\"\nexit 0");
            LocalFileTracker tracker = new(directory, FileWatcherMode.Polling, 30, 200);
            manager = new(Guid.NewGuid(), remote, tracker);
            RemoteFileItem item = new("edit with spaces.txt", "/etc/edit with spaces.txt", false, 8, DateTimeOffset.UtcNow, "-rw-------");
            manager.SelectedItem = item;
            await manager.OpenWithEditorAsync(Editor(executable));
            await WaitUntilAsync(() => File.Exists(executable + ".ready"));
            string localPath = await File.ReadAllTextAsync(executable + ".ready");
            Assert.Equal(item.FullPath, Assert.Single(manager.ActiveTrackedFiles));
            Assert.True(tracker.IsTracking(localPath));
            Assert.Equal("original", remote.Contents);

            await File.WriteAllTextAsync(executable + ".close", "");
            await remote.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => manager.ActiveTrackedFiles.Count == 0);
            Assert.False(tracker.IsTracking(localPath));
            Assert.Equal("last save", await File.ReadAllTextAsync(localPath));
            remote.AllowCommit.TrySetResult();
            await WaitUntilAsync(() => remote.Contents == "last save");
        }
        finally
        {
            remote.AllowCommit.TrySetResult();
            if (manager != null) await manager.DisposeAsync();
            Directory.Delete(directory, true);
        }
    });

    [PlatformFact(TestPlatform.MacOS)]
    public Task FailedWaitProcess_KeepsMonitoringAndCache() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = NewDirectory();
        try
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            string executable = await CreateWaitEditorAsync(directory, "exit 9");
            LocalFileTracker tracker = new(directory, FileWatcherMode.Polling, 30, 200);
            await using RemoteFileManagerViewModel manager = new(Guid.NewGuid(), new BufferedFileSystem(), tracker);
            manager.SelectedItem = new("edit.txt", "/edit.txt", false, 8, DateTimeOffset.UtcNow, "-rw-------");
            await manager.OpenWithEditorAsync(Editor(executable));
            await WaitUntilAsync(() => File.Exists(executable + ".ready"));
            string file = await File.ReadAllTextAsync(executable + ".ready");
            await File.WriteAllTextAsync(executable + ".close", "");
            await WaitUntilAsync(() => File.Exists(executable + ".done"));
            // 后续保存仍通过真实 tracker 和 VM 回写，证明失败等待没有取消监视。
            await File.WriteAllTextAsync(file, "after wait failure");
            await tracker.CheckForChangesAsync(file);
            Assert.True(tracker.IsTracking(file));
            Assert.Single(manager.ActiveTrackedFiles);
            Assert.Equal("after wait failure", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(directory, true); }
    });

    [Fact]
    public async Task ReopeningTrackedFile_PreservesUnsyncedLocalChanges()
    {
        string directory = NewDirectory();
        try
        {
            await using LocalFileTracker tracker = new(directory, FileWatcherMode.Polling, 30, 200);
            await using FileEditorLauncher launcher = new(tracker);
            Guid session = Guid.NewGuid();
            RemoteFileItem item = new("edit.txt", "/edit.txt", false, 8, DateTimeOffset.UtcNow, "-rw-------");
            ExternalEditor editor = Editor(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\"");
            BufferedFileSystem remote = new();
            await launcher.OpenAndTrackAsync(session, item, remote, editor);
            string file = tracker.GetLocalCachePath(session, item.FullPath);
            await File.WriteAllTextAsync(file, "not yet uploaded");
            await launcher.OpenAndTrackAsync(session, item, remote, editor);
            Assert.Equal("not yet uploaded", await File.ReadAllTextAsync(file));
            Assert.Equal("original", remote.Contents);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FailedEditorLaunch_RollsBackMonitoringAndPreservesCache()
    {
        string directory = NewDirectory();
        try
        {
            await using LocalFileTracker tracker = new(directory, FileWatcherMode.Polling, 30, 200);
            await using FileEditorLauncher launcher = new(tracker);
            Guid session = Guid.NewGuid();
            RemoteFileItem item = new("edit.txt", "/edit.txt", false, 8, DateTimeOffset.UtcNow, "-rw-------");
            await Assert.ThrowsAnyAsync<Exception>(() => launcher.OpenAndTrackAsync(session, item, new BufferedFileSystem(),
                Editor(Path.Combine(directory, "missing-editor"))));
            string file = tracker.GetLocalCachePath(session, item.FullPath);
            Assert.False(tracker.IsTracking(file));
            Assert.Equal("original", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public Task SameNameFiles_InDifferentDirectoriesTrackAndStopIndependently() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = NewDirectory();
        try
        {
            LocalFileTracker tracker = new(directory, FileWatcherMode.Polling, 30, 200);
            await using RemoteFileManagerViewModel manager = new(Guid.NewGuid(), new BufferedFileSystem(), tracker);
            ExternalEditor editor = Editor(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\"");
            foreach (string path in new[] { "/a/config.txt", "/b/config.txt" })
            {
                manager.SelectedItem = new("config.txt", path, false, 8, DateTimeOffset.UtcNow, "-rw-------");
                await manager.OpenWithEditorAsync(editor);
            }
            Assert.Equal(new[] { "/a/config.txt", "/b/config.txt" }, manager.ActiveTrackedFiles);
            await manager.StopTrackingFileAsync("/a/config.txt");
            Assert.Equal("/b/config.txt", Assert.Single(manager.ActiveTrackedFiles));
        }
        finally { Directory.Delete(directory, true); }
    });

    private static ExternalEditor Editor(string executable, string arguments = "\"{path}\"") => new()
    {
        Name = "test editor",
        ArgumentsTemplate = arguments,
        Paths = [new(Guid.NewGuid(), Guid.Empty, "any", executable)]
    };

    [Fact]
    public Task ManualStop_WithMissingCacheStillRemovesMonitoring() => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = NewDirectory();
        try
        {
            Guid session = Guid.NewGuid();
            LocalFileTracker tracker = new(directory, FileWatcherMode.Polling, 30, 200);
            await using RemoteFileManagerViewModel manager = new(session, new BufferedFileSystem(), tracker);
            manager.SelectedItem = new("edit.txt", "/edit.txt", false, 8, DateTimeOffset.UtcNow, "-rw-------");
            await manager.OpenWithEditorAsync(Editor(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\""));
            string file = tracker.GetLocalCachePath(session, "/edit.txt");
            File.Delete(file);

            await manager.StopTrackingFileAsync("/edit.txt");
            Assert.False(tracker.IsTracking(file));
            Assert.Empty(manager.ActiveTrackedFiles);
        }
        finally { Directory.Delete(directory, true); }
    });

    [SupportedOSPlatform("macos")]
    private static async Task<string> CreateWaitEditorAsync(string directory, string afterClose)
    {
        // 真正的等待进程：必须收到 --wait，关闭信号到达后才保存并退出。
        string executable = Path.Combine(directory, "code");
        string script = "#!/bin/sh\n[ \"$1\" = --wait ] || exit 2\nfile=\"\"\nfor arg in \"$@\"; do file=\"$arg\"; done\nprintf '%s' \"$file\" > \"$0.ready\"\nwhile [ ! -f \"$0.close\" ]; do sleep 0.05; done\ntouch \"$0.done\"\n" + afterClose + "\n";
        await File.WriteAllTextAsync(executable, script);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return executable;
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
            manager.ActiveTrackedFiles.Add("/edit.txt");
            manager.SelectedActivityTab = 0;
            Assert.False(manager.HasTrackedActivity);
            tracker.NotifyChange(localPath, "/edit.txt");
            await remote.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(manager.HasTrackedActivity);
            long activityRevision = manager.TrackedActivityRevision;
            Assert.Equal(0, manager.SelectedActivityTab);
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
            Assert.Equal(activityRevision, manager.TrackedActivityRevision);
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
        public bool IsTracking(string localFilePath) => false;
        public Task CheckForChangesAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
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
