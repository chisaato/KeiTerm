using System.IO;
using System.Text;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class LocalFileTrackerTests
{
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
