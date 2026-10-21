using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Dapper;
using Kei.Term.App.Services;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Sync;
using Xunit;

namespace Kei.Term.Tests;

public sealed partial class FullSyncSnapshotTests
{
    [Fact]
    public async Task SharedFactory_SettingsAndProfileSavesPublishRealChangeSignals()
    {
        var changes = new SyncChangeSignal();
        var device = await CreateAsync("signals", changes);
        int signals = 0;
        changes.Changed += () => Interlocked.Increment(ref signals);
        await device.Settings.CommitFontSizeAsync(29);
        Assert.True(Volatile.Read(ref signals) > 0);
        int before = signals;
        device.Profiles.AddCustomTerminalProfile(new() { Id = "new-profile", Name = "new-profile" });
        await device.Profiles.CommitAndSaveProfilesAsync();
        Assert.True(Volatile.Read(ref signals) > before);
        Assert.Contains(await device.Store.ExportAsync(), e => e.EntityType == "profile" && e.EntityId == "new-profile");
    }

    [Fact]
    public Task SchedulerAndUiImportedReload_DoNotRestampOrQueueAnotherRound_UserSavesDo() => HeadlessAvalonia.RunAsync(async () =>
    {
        var remote = await CreateAsync("remote");
        var changes = new SyncChangeSignal();
        var local = await CreateAsync("local", changes);
        remote.Settings.Current.FontSize = 23;
        await remote.Settings.SaveSettingsAsync(remote.Settings.Current);
        remote.Profiles.AddCustomTerminalProfile(new() { Id = "incoming", Name = "incoming" });
        await remote.Profiles.CommitAndSaveProfilesAsync();
        await SyncAsync(remote);
        var remoteEntities = await remote.Store.ExportAsync();
        var store = new SqliteSyncSnapshotStore(local.Db, local.Vault, Path.Combine(local.Directory, "settings.json"), Path.Combine(local.Directory, "profiles.json"));
        int uiReloads = 0;
        bool uiSuppressed = false;
        store.Imported += async () =>
        {
            // 与 App 相同的真实消息投递、两层 await，不用 Task.Run 代替 UI Dispatcher。
            Task reload = await Dispatcher.UIThread.InvokeAsync<Task>(async () =>
            {
                uiSuppressed = changes.IsSuppressed;
                await local.Settings.ReloadSynchronizedAsync();
                await local.Profiles.ReloadSynchronizedAsync();
                uiReloads++;
            });
            await reload;
        };
        var coordinator = new SyncCoordinator(store, new AgeSyncPackage(), local.Vault, new PersistentDeviceId(Path.Combine(local.Directory, "sync-device-id.txt")), local.Vault);
        var clock = new ReviewClock();
        int rounds = 0, signals = 0;
        changes.Changed += () => Interlocked.Increment(ref signals);
        using var scheduler = new SyncScheduler(clock, new ReviewLocationSource(Location), local.Vault, changes, async (location, ct) =>
        {
            Interlocked.Increment(ref rounds);
            var result = await coordinator.SyncAsync(location, ct);
            // 本例验证已授权的导入重载，不把首次加入交互放到后台生产代码里。
            while (result.Confirmation is { } preview)
                result = await coordinator.SyncApprovedAsync(location, new(preview.Token), ct);
            Assert.True(result.Success, result.Message);
            return result;
        });
        scheduler.Start();
        int epoch = await clock.ParkedAfterAsync(0);
        Assert.Equal(1, rounds);
        Assert.Equal(1, uiReloads);
        Assert.True(uiSuppressed);
        Assert.Equal(0, signals);
        Assert.Equal(clock.UtcNow + SyncScheduler.Interval, clock.Due);
        Assert.Equal(23, local.Settings.Current.FontSize);
        var imported = await store.ExportAsync();
        foreach (var expected in remoteEntities.Where(e => e.EntityType == "profile" || e.EntityType == "setting" && e.EntityId == "FontSize"))
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(imported.Single(e => e.EntityType == expected.EntityType && e.EntityId == expected.EntityId)));

        // 不是只看 Publish：真实调度器必须从真实保存预约防抖，最后在真实包中看到数据。
        await local.Settings.CommitFontSizeAsync(31);
        epoch = await clock.ParkedAfterAsync(epoch);
        Assert.True(signals > 0);
        Assert.Equal(clock.UtcNow + SyncScheduler.Debounce, clock.Due);
        clock.Advance(SyncScheduler.Debounce);
        epoch = await clock.ParkedAfterAsync(epoch);
        Assert.Equal(2, rounds);
        Assert.Equal(clock.UtcNow.Date + SyncScheduler.Interval, clock.Due);
        await SyncAsync(remote);
        Assert.Equal(31, remote.Settings.Current.FontSize);

        local.Profiles.AddCustomTerminalProfile(new() { Id = "user-profile", Name = "user-profile" });
        await local.Profiles.CommitAndSaveProfilesAsync();
        epoch = await clock.ParkedAfterAsync(epoch);
        Assert.Equal(clock.UtcNow + SyncScheduler.Debounce, clock.Due);
        clock.Advance(SyncScheduler.Debounce);
        await clock.ParkedAfterAsync(epoch);
        Assert.Equal(3, rounds);
        await SyncAsync(remote);
        Assert.Contains(remote.Profiles.AllTerminalProfiles, p => p.Id == "user-profile");
    });

    private sealed class ReviewLocationSource(ISyncLocation location) : ISyncLocationSource
    {
        public bool HasConfiguration => true;
        public Task<ISyncLocation?> OpenAsync(CancellationToken cancellationToken = default) => Task.FromResult<ISyncLocation?>(location);
    }

    private sealed class ReviewClock : ISyncClock
    {
        private readonly object _gate = new();
        private TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _delay;
        private int _epoch;
        public DateTime UtcNow { get; private set; } = new(2035, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime Due { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            lock (_gate)
            {
                Due = delay == Timeout.InfiniteTimeSpan ? DateTime.MaxValue : UtcNow + delay;
                _delay = completion;
                _epoch++;
                _parked.TrySetResult();
                _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return completion.Task;
        }

        public void Advance(TimeSpan time)
        {
            lock (_gate)
            {
                UtcNow += time;
                if (Due <= UtcNow) _delay?.TrySetResult();
            }
        }

        public async Task<int> ParkedAfterAsync(int epoch)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                Task parked;
                lock (_gate)
                {
                    if (_epoch > epoch && _delay is { Task.IsCompleted: false }) return _epoch;
                    parked = _parked.Task;
                }
                await parked.WaitAsync(timeout.Token);
            }
        }
    }
}
