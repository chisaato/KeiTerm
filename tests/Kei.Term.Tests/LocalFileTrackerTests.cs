using System.IO;
using System.Text;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class LocalFileTrackerTests
{
    [Theory]
    [InlineData("/a", "/a ")]
    [InlineData(@"/a\b", "/a/b")]
    public async Task DistinctPosixNamesHaveIndependentEditableCopies(string first, string second)
    {
        string directory = Directory.CreateTempSubdirectory("keiterm_posix_names_").FullName;
        try
        {
            await using var tracker = new LocalFileTracker(directory, FileWatcherMode.Polling, 60);
            Guid session = Guid.NewGuid();
            string a = tracker.GetLocalCachePath(session, first);
            string b = tracker.GetLocalCachePath(session, second);
            await File.WriteAllTextAsync(a, "first");
            await File.WriteAllTextAsync(b, "second");
            Assert.Equal("first", await File.ReadAllTextAsync(a));
            Assert.Equal("second", await File.ReadAllTextAsync(b));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCleanupPreservesBothStagedEditsAndAnyNewSave(bool editorSavedAgain)
    {
        string directory = Directory.CreateTempSubdirectory("keiterm_cache_recovery_").FullName;
        try
        {
            await using var tracker = new LocalFileTracker(directory, FileWatcherMode.Polling, 60);
            Guid session = Guid.NewGuid();
            string cached = tracker.GetLocalCachePath(session, "/remote.txt");
            await File.WriteAllTextAsync(cached, "downloaded");
            await tracker.RegisterTrackedFileAsync(session, "/remote.txt", cached);
            await tracker.DisposeAsync();

            // 构造进程退出时已移动、尚未验证的文件，包含编辑器刚原子替换的修改。
            var folder = new FileInfo(cached).Directory!;
            var recovery = Directory.CreateDirectory(Path.Combine(folder.Parent!.FullName, "Recovered edits-" + folder.Name));
            string staged = Path.Combine(recovery.FullName, Path.GetFileName(cached));
            await File.WriteAllTextAsync(cached, "first uncommitted edit");
            File.Move(cached, staged);
            if (editorSavedAgain) await File.WriteAllTextAsync(cached, "second uncommitted edit");

            Assert.Equal(0, LocalFileTracker.ClearClosedCache(directory));
            Assert.Equal("first uncommitted edit", await File.ReadAllTextAsync(editorSavedAgain ? staged : cached));
            if (editorSavedAgain) Assert.Equal("second uncommitted edit", await File.ReadAllTextAsync(cached));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CleanupRemovesOnlyClosedUnmodifiedDownloads()
    {
        string directory = Directory.CreateTempSubdirectory("keiterm_cache_").FullName;
        try
        {
            string unrelated = Path.Combine(directory, "notes.txt");
            await File.WriteAllTextAsync(unrelated, "personal file");
            await using LocalFileTracker tracker = new(directory, FileWatcherMode.Polling, 60);
            Guid session = Guid.NewGuid();
            string clean = tracker.GetLocalCachePath(session, "/original");
            string pending = tracker.GetLocalCachePath(session, "/pending");
            await File.WriteAllTextAsync(clean, "original");
            await File.WriteAllTextAsync(pending, "download");
            await tracker.RegisterTrackedFileAsync(session, "/original", clean);
            await tracker.RegisterTrackedFileAsync(session, "/pending", pending);
            string committed = tracker.GetLocalCachePath(session, "/committed");
            await File.WriteAllTextAsync(committed, "download");
            await tracker.RegisterTrackedFileAsync(session, "/committed", committed);
            await File.WriteAllTextAsync(committed, "published edit");
            tracker.RecordCommittedContent(committed, System.IO.Hashing.XxHash128.Hash(await File.ReadAllBytesAsync(committed)));
            Assert.Equal(0, LocalFileTracker.ClearClosedCache(directory));
            await File.WriteAllTextAsync(pending, "unsent changes");
            await tracker.UnregisterTrackedFileAsync(pending);
            await tracker.DisposeAsync();

            string damaged = Directory.CreateDirectory(Path.Combine(directory, "KeiTerm-editor-" + Guid.NewGuid().ToString("N"))).FullName;
            string brokenManifest = Path.Combine(damaged, ".keiterm-cache.json");
            await File.WriteAllTextAsync(brokenManifest, "{ damaged");
            Assert.Equal(2, LocalFileTracker.ClearClosedCache(directory));
            Assert.Equal("{ damaged", await File.ReadAllTextAsync(brokenManifest));
            Assert.False(File.Exists(committed));
            Assert.False(File.Exists(clean));
            Assert.Equal(0, LocalFileTracker.ClearClosedCache(directory));
            Assert.Equal("unsent changes", await File.ReadAllTextAsync(pending));
            Assert.Equal("personal file", await File.ReadAllTextAsync(unrelated));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Polling_DetectsSameLengthEditWithPreservedTimestamp()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_poll_hash_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using LocalFileTracker tracker = new(directory, FileWatcherMode.Polling, 1, 200);
            string file = tracker.GetLocalCachePath(Guid.NewGuid(), "/edit.txt");
            await File.WriteAllTextAsync(file, "before");
            DateTime timestamp = File.GetLastWriteTimeUtc(file);
            await tracker.RegisterTrackedFileAsync(Guid.NewGuid(), "/edit.txt", file);
            TaskCompletionSource<LocalFileChangedEventArgs> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            tracker.FileChanged += (_, e) => changed.TrySetResult(e);

            await File.WriteAllTextAsync(file, "edited");
            File.SetLastWriteTimeUtc(file, timestamp);
            LocalFileChangedEventArgs notification = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("/edit.txt", notification.RemotePath);
            Assert.Equal("edited", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(FileWatcherMode.Auto)]
    [InlineData(FileWatcherMode.OSNative)]
    [InlineData(FileWatcherMode.Polling)]
    public async Task AtomicReplace_ReportsLatestContentOnce(FileWatcherMode mode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_atomic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using LocalFileTracker tracker = new(directory, mode, 1, 200);
            string file = tracker.GetLocalCachePath(Guid.NewGuid(), "/edit.txt");
            await File.WriteAllTextAsync(file, "original");
            await tracker.RegisterTrackedFileAsync(Guid.NewGuid(), "/edit.txt", file);
            TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int notifications = 0;
            tracker.FileChanged += (_, _) => { Interlocked.Increment(ref notifications); changed.TrySetResult(); };

            string replacement = file + ".tmp";
            await File.WriteAllTextAsync(replacement, "saved atomically");
            File.Move(replacement, file, overwrite: true);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await tracker.CheckForChangesAsync(file);
            await tracker.CheckForChangesAsync(file);
            Assert.Equal(1, Volatile.Read(ref notifications));
            Assert.Equal("saved atomically", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Dispose_CancelsPendingNativeNotificationAndPreservesCache()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_tracker_dispose_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using LocalFileTracker tracker = new(directory, FileWatcherMode.OSNative, 1, 100);
            string file = tracker.GetLocalCachePath(Guid.NewGuid(), "/edit.txt");
            await File.WriteAllTextAsync(file, "before");
            await tracker.RegisterTrackedFileAsync(Guid.NewGuid(), "/edit.txt", file);
            int notifications = 0;
            tracker.FileChanged += (_, _) => Interlocked.Increment(ref notifications);
            await File.WriteAllTextAsync(file, "saved just before close");
            await tracker.DisposeAsync();
            await Task.Delay(250);
            Assert.Equal(0, Volatile.Read(ref notifications));
            Assert.Equal("saved just before close", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
