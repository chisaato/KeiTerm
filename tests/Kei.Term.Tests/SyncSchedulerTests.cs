using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Models;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Storage;
using Xunit;

namespace Kei.Term.Tests;

public sealed class SyncSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keiterm-sync-schedule-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LocalChange_DebouncesThirtySeconds_AndResetsWhenAnotherChangeArrives()
    {
        await using Harness harness = Start(configured: true);
        await harness.NextParkAsync();
        Assert.Equal(1, harness.Calls);

        int epoch = harness.Clock.Epoch;
        harness.Changes.Publish();
        await harness.NextParkAsync(epoch);
        harness.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(1, harness.Calls);

        epoch = harness.Clock.Epoch;
        harness.Changes.Publish();
        await harness.NextParkAsync(epoch);
        Assert.Equal(1, harness.Calls);
        harness.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, harness.Calls);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.WaitForCallsAsync(2);
    }

    [Fact]
    public async Task Interval_RunsEveryFiveMinutes()
    {
        await using Harness harness = Start(configured: true);
        await harness.NextParkAsync();
        Assert.Equal(1, harness.Calls);

        harness.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Equal(1, harness.Calls);

        int epoch = harness.Clock.Epoch;
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.WaitForCallsAsync(2);
        await harness.NextParkAsync(epoch);
        epoch = harness.Clock.Epoch;
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.WaitForCallsAsync(3);
    }

    [Fact]
    public async Task TriggerDuringRound_CoalescesIntoExactlyOneFollowUp()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Harness harness = Start(configured: true, sync: async (call, ct) =>
        {
            if (call == 1) await release.Task.WaitAsync(ct);
            return new SyncResult(true, "同步成功");
        });
        await harness.WaitForCallsAsync(1);
        harness.Changes.Publish();
        Task<SyncResult> manual = harness.Scheduler.SyncNowAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        int epoch = harness.Clock.Epoch;
        release.SetResult();
        SyncResult result = await manual.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.NextParkAsync(epoch);
        Assert.True(result.Success);
        Assert.Equal(2, harness.Calls);
    }

    [Fact]
    public async Task LockedVault_PausesAndCatchesUpOnceWhenUnlocked()
    {
        await using Harness harness = Start(configured: true, unlocked: false);
        await harness.NextParkAsync();
        Assert.Equal(0, harness.Calls);
        Assert.Equal(SyncScheduler.VaultLockedMessage, harness.Scheduler.Status);

        SyncResult manual = await harness.Scheduler.SyncNowAsync();
        Assert.False(manual.Success);
        Assert.Equal(SyncScheduler.VaultLockedMessage, manual.Message);
        harness.Changes.Publish();
        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, harness.Calls);

        harness.Secrets.IsUnlocked = true;
        harness.Scheduler.NotifyVault(true);
        await harness.WaitForCallsAsync(1);
        await harness.NextParkAsync();
        Assert.Equal(1, harness.Calls);
        Assert.Contains("同步成功", harness.Scheduler.Status);
    }

    [Fact]
    public async Task UnconfiguredSync_DoesNotRunOnStartupIntervalOrLocalChange()
    {
        await using Harness harness = Start(configured: false, passphrase: null);
        int epoch = await harness.NextParkAsync();
        harness.Changes.Publish();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.NextParkAsync(epoch);
        Assert.Equal(0, harness.Calls);
        Assert.Equal("", harness.Scheduler.Status);

        SyncResult manual = await harness.Scheduler.SyncNowAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(manual.Success);
        Assert.Equal("请选择同步目录", manual.Message);
        Assert.Equal(0, harness.Calls);
    }

    [Fact]
    public async Task MissingPassphrase_DoesNotRunUntilManualAsks()
    {
        await using Harness harness = Start(configured: true, passphrase: null);
        int epoch = await harness.NextParkAsync();
        Assert.Equal(0, harness.Calls);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.NextParkAsync(epoch);
        Assert.Equal(0, harness.Calls);

        SyncResult manual = await harness.Scheduler.SyncNowAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("请先设定或加入同步口令", manual.Message);
        Assert.Equal(0, harness.Calls);
    }

    [Fact]
    public async Task Failure_IsShownAndRetriedAtNextTrigger()
    {
        await using Harness harness = Start(configured: true, sync: (call, ct) =>
            Task.FromResult(call == 1
                ? new SyncResult(false, "同步口令不正确")
                : new SyncResult(true, "同步成功")));
        await harness.NextParkAsync();
        Assert.Contains("同步口令不正确", harness.Scheduler.Status);
        Assert.Equal(1, harness.Refreshes);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.WaitForCallsAsync(2);
        Assert.Contains("同步成功", harness.Scheduler.Status);
        Assert.Equal(2, harness.Refreshes);
    }

    [Fact]
    public async Task ManualSync_RunsImmediatelyWhileIdle()
    {
        await using Harness harness = Start(configured: true);
        await harness.NextParkAsync();
        SyncResult result = await harness.Scheduler.SyncNowAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.Equal(2, harness.Calls);
    }

    [Fact]
    public async Task ImportWrite_DoesNotScheduleAnotherRound_LaterUserSaveDoes()
    {
        Directory.CreateDirectory(_root);
        var changes = new SyncChangeSignal();
        var db = new SqliteConnectionFactory($"Data Source={Path.Combine(_root, "db.sqlite")};Pooling=False", changes: changes);
        var tree = new SqliteTreeRepository(db);
        await tree.InitializeAsync();
        var folder = new FolderNode { Name = "folder" };
        await tree.SaveNodeAsync(folder);

        await using Harness harness = Start(configured: true, changes: changes, sync: async (call, ct) =>
        {
            if (call == 1)
            {
                folder.Name = "imported";
                await tree.SaveNodeAsync(folder, ct);
            }
            return new SyncResult(true, "同步成功");
        });
        await harness.NextParkAsync();
        Assert.Equal(1, harness.Calls);

        await tree.UpdateFolderExpandedAsync(folder.Id, true);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, harness.Calls);

        int epoch = harness.Clock.Epoch;
        folder.Name = "renamed";
        await tree.SaveNodeAsync(folder);
        await harness.NextParkAsync(epoch);
        harness.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, harness.Calls);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.WaitForCallsAsync(2);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Harness Start(bool configured, bool unlocked = true, string? passphrase = "口令",
        ISyncChangeSignal? changes = null, Func<int, CancellationToken, Task<SyncResult>>? sync = null)
    {
        var clock = new ManualSyncClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var secrets = new FakePassphrases { IsUnlocked = unlocked, Passphrase = passphrase };
        var source = new FakeLocations { HasConfiguration = configured, Location = new FakeSyncLocation() };
        changes ??= new SyncChangeSignal();
        var harness = new Harness(clock, secrets, changes);
        sync ??= (_, _) => Task.FromResult(new SyncResult(true, "同步成功", clock.UtcNow, "device"));
        var scheduler = new SyncScheduler(clock, source, secrets, changes, async (location, ct) =>
        {
            int call = harness.NoteCall();
            return await sync(call, ct);
        });
        scheduler.Synchronized += () =>
        {
            Interlocked.Increment(ref harness.RefreshCount);
            return Task.CompletedTask;
        };
        harness.Attach(scheduler);
        scheduler.Start();
        return harness;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private int _calls;
        public ManualSyncClock Clock { get; }
        public FakePassphrases Secrets { get; }
        public ISyncChangeSignal Changes { get; }
        public SyncScheduler Scheduler { get; private set; } = null!;
        public int Calls => Volatile.Read(ref _calls);
        public int RefreshCount;
        public int Refreshes => Volatile.Read(ref RefreshCount);

        public Harness(ManualSyncClock clock, FakePassphrases secrets, ISyncChangeSignal changes)
        {
            Clock = clock;
            Secrets = secrets;
            Changes = changes;
        }

        public void Attach(SyncScheduler scheduler) => Scheduler = scheduler;

        public int NoteCall() => Interlocked.Increment(ref _calls);

        public async Task WaitForCallsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Calls < count) await Task.Delay(10, timeout.Token);
        }

        public Task<int> NextParkAsync(int afterEpoch = 0) => Clock.WaitUntilParkedAfterAsync(afterEpoch);

        public ValueTask DisposeAsync()
        {
            Scheduler.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualSyncClock : ISyncClock
    {
        private readonly object _gate = new();
        private readonly List<Waiter> _waiters = new();
        private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _epoch;

        public ManualSyncClock(DateTime start) => UtcNow = start;

        public DateTime UtcNow { get; private set; }
        public int Epoch { get { lock (_gate) return _epoch; } }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DateTime due = delay == Timeout.InfiniteTimeSpan ? DateTime.MaxValue : UtcNow + delay;
            var waiter = new Waiter(due, completion);
            cancellationToken.Register(() =>
            {
                completion.TrySetCanceled(cancellationToken);
                lock (_gate) _waiters.Remove(waiter);
            });
            lock (_gate)
            {
                _waiters.Add(waiter);
                _epoch++;
                TaskCompletionSource pulse = _pulse;
                _pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pulse.TrySetResult();
            }
            return completion.Task;
        }

        public void Advance(TimeSpan by)
        {
            Waiter[] ready;
            lock (_gate)
            {
                UtcNow += by;
                ready = _waiters.FindAll(waiter => waiter.Due <= UtcNow).ToArray();
                foreach (Waiter waiter in ready) _waiters.Remove(waiter);
            }
            foreach (Waiter waiter in ready) waiter.Completion.TrySetResult();
        }

        public async Task<int> WaitUntilParkedAfterAsync(int epoch)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task pulse;
                lock (_gate)
                {
                    if (_epoch > epoch && _waiters.Count > 0) return _epoch;
                    pulse = _pulse.Task;
                }
                await pulse.WaitAsync(timeout.Token);
            }
        }

        private sealed record Waiter(DateTime Due, TaskCompletionSource Completion);
    }

    private sealed class FakeLocations : ISyncLocationSource
    {
        public bool HasConfiguration { get; set; }
        public ISyncLocation? Location { get; set; }
        public Task<ISyncLocation?> OpenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(HasConfiguration ? Location : null);
    }

    private sealed class FakePassphrases : ISyncPassphraseStore
    {
        public bool IsUnlocked { get; set; }
        public string? Passphrase { get; set; }
        public Task<char[]?> GetPassphraseAsync(CancellationToken cancellationToken = default)
        {
            if (!IsUnlocked) throw new InvalidOperationException("Vault 已锁定，请先解锁");
            return Task.FromResult(Passphrase == null ? null : Passphrase.ToCharArray());
        }
        public Task SavePassphraseAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeSyncLocation : ISyncLocation
    {
        public bool SupportsConditionalWrites => false;
        public Task TestConnectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SyncObject?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult<SyncObject?>(null);
        public Task WriteAsync(byte[] content, string? expectedVersionTag = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SyncHistoryEntry>> ListHistoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SyncHistoryEntry>>([]);
        public Task<byte[]?> ReadHistoryAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
        public Task WriteHistoryAsync(string name, byte[] content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteHistoryAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
