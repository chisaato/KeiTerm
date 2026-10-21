using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.Tests;

public class DelayedBusyIndicatorTests
{
    private static TimeSpan Ms(int value) => TimeSpan.FromMilliseconds(value);

    [Fact]
    public void QuickWork_NeverShows()
    {
        ManualTimeProvider time = new();
        DelayedBusyIndicator indicator = new(time);
        List<bool> changes = [];
        indicator.Changed += changes.Add;

        indicator.SetBusy(true);
        time.Advance(Ms(149));
        indicator.SetBusy(false);
        time.Advance(Ms(1000));

        Assert.False(indicator.IsShown);
        Assert.Empty(changes);
    }

    [Fact]
    public void SlowWork_ShowsAfterDelayAndHidesAtOnceWhenMinimumVisibleElapsed()
    {
        ManualTimeProvider time = new();
        DelayedBusyIndicator indicator = new(time);
        indicator.SetBusy(true);
        time.Advance(Ms(149));
        Assert.False(indicator.IsShown);
        time.Advance(Ms(1));
        Assert.True(indicator.IsShown);

        time.Advance(Ms(500));
        indicator.SetBusy(false);
        Assert.False(indicator.IsShown);
    }

    [Fact]
    public void WorkEndingRightAfterShowing_StaysVisibleUntilMinimumDuration()
    {
        ManualTimeProvider time = new();
        DelayedBusyIndicator indicator = new(time);
        indicator.SetBusy(true);
        time.Advance(Ms(150));
        Assert.True(indicator.IsShown);

        indicator.SetBusy(false);
        Assert.True(indicator.IsShown);
        time.Advance(Ms(299));
        Assert.True(indicator.IsShown);
        time.Advance(Ms(1));
        Assert.False(indicator.IsShown);
    }

    [Fact]
    public void BackToBackWork_DuringMinimumStayDoesNotFlicker()
    {
        ManualTimeProvider time = new();
        DelayedBusyIndicator indicator = new(time);
        List<bool> changes = [];
        indicator.Changed += changes.Add;
        indicator.SetBusy(true);
        time.Advance(Ms(150));
        indicator.SetBusy(false);
        time.Advance(Ms(100));
        // 停留期内接着加载：保持显示，不触发隐藏再显示。
        indicator.SetBusy(true);
        time.Advance(Ms(1000));
        Assert.True(indicator.IsShown);
        indicator.SetBusy(false);
        Assert.False(indicator.IsShown);

        Assert.Equal([true, false], changes);
    }

    [Fact]
    public void WorkAfterHidden_NeedsDelayAgain()
    {
        ManualTimeProvider time = new();
        DelayedBusyIndicator indicator = new(time);
        indicator.SetBusy(true);
        time.Advance(Ms(150));
        indicator.SetBusy(false);
        time.Advance(Ms(300));
        Assert.False(indicator.IsShown);

        indicator.SetBusy(true);
        time.Advance(Ms(100));
        Assert.False(indicator.IsShown);
        time.Advance(Ms(50));
        Assert.True(indicator.IsShown);
    }

    [Fact]
    public async Task FileManager_ShowsIndicatorOnlyForSlowRefreshAndKeepsOldItemsWhileLoading()
    {
        ManualTimeProvider time = new();
        GatedFileSystem files = new();
        await using RemoteFileManagerViewModel model = new(Guid.NewGuid(), files, new NoTracker(), timeProvider: time);

        // 快速刷新：指示条始终不亮。
        files.Next = Task.FromResult<IReadOnlyList<RemoteFileItem>>([File("old.txt")]);
        await model.RefreshDirectoryAsync();
        time.Advance(Ms(1000));
        Assert.False(model.IsLoadingIndicatorVisible);
        Assert.Contains(model.Items, item => item.Name == "old.txt");

        // 慢刷新：加载期间保留旧列表，150ms 后亮起，完成后再保持到 300ms。
        TaskCompletionSource<IReadOnlyList<RemoteFileItem>> gate = new();
        files.Next = gate.Task;
        Task refresh = model.RefreshDirectoryAsync();
        Assert.True(model.IsLoading);
        Assert.Contains(model.Items, item => item.Name == "old.txt");
        time.Advance(Ms(149));
        Assert.False(model.IsLoadingIndicatorVisible);
        time.Advance(Ms(1));
        Assert.True(model.IsLoadingIndicatorVisible);

        gate.SetResult([File("new.txt")]);
        await refresh;
        Assert.False(model.IsLoading);
        Assert.True(model.IsLoadingIndicatorVisible);
        Assert.Equal(["new.txt"], model.Items.Select(item => item.Name));
        time.Advance(Ms(300));
        Assert.False(model.IsLoadingIndicatorVisible);
    }

    [Fact]
    public async Task FileManager_FailedRefreshKeepsOldItemsAndReportsError()
    {
        GatedFileSystem files = new();
        await using RemoteFileManagerViewModel model = new(Guid.NewGuid(), files, new NoTracker(), timeProvider: new ManualTimeProvider());
        files.Next = Task.FromResult<IReadOnlyList<RemoteFileItem>>([File("keep.txt")]);
        await model.RefreshDirectoryAsync();

        files.Next = Task.FromException<IReadOnlyList<RemoteFileItem>>(new IOException("boom"));
        await model.RefreshDirectoryAsync();

        Assert.Equal(["keep.txt"], model.Items.Select(item => item.Name));
        Assert.Contains("刷新失败", model.TransferStatusMessage);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task DirectoryNode_LoadingIndicatorIsDelayedAndNeverRaisedForQuickLoads()
    {
        ManualTimeProvider time = new();
        GatedFileSystem files = new();
        RemoteDirectoryNodeViewModel node = new("etc", "/etc", files, () => false, timeProvider: time);

        files.Next = Task.FromResult<IReadOnlyList<RemoteFileItem>>([]);
        await node.LoadChildrenAsync();
        time.Advance(Ms(1000));
        Assert.False(node.IsLoadingIndicatorVisible);

        RemoteDirectoryNodeViewModel slow = new("var", "/var", files, () => false, timeProvider: time);
        TaskCompletionSource<IReadOnlyList<RemoteFileItem>> gate = new();
        files.Next = gate.Task;
        Task load = slow.LoadChildrenAsync();
        time.Advance(Ms(150));
        Assert.True(slow.IsLoadingIndicatorVisible);
        gate.SetResult([]);
        await load;
        time.Advance(Ms(300));
        Assert.False(slow.IsLoadingIndicatorVisible);
    }

    private static RemoteFileItem File(string name) => new(name, "/" + name, false, 1, DateTimeOffset.UtcNow, "-rw-r--r--");

    // 手动推进的时间源：只实现一次性定时器，足够驱动指示器。
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state, _ticks + dueTime.Ticks);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            long target = _ticks + by.Ticks;
            while (true)
            {
                ManualTimer? next = _timers.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                if (next == null) break;
                _timers.Remove(next);
                _ticks = Math.Max(_ticks, next.Due);
                next.Fire();
            }
            _ticks = target;
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, long due) : ITimer
        {
            public long Due { get; } = due;
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => owner._timers.Remove(this);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class GatedFileSystem : IRemoteFileSystem
    {
        public Task<IReadOnlyList<RemoteFileItem>> Next { get; set; } = Task.FromResult<IReadOnlyList<RemoteFileItem>>([]);
        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default) => Next;
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => Task.FromResult<RemoteFileItem?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoTracker : ILocalFileTracker
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
}
