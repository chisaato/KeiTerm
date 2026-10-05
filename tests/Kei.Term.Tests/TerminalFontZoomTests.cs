using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.Terminals;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Ssh.Abstractions;
using Xunit;

namespace Kei.Term.Tests;

// Ctrl+滚轮缩放：纯函数步进/累积 + MainViewModel 全局字号写回与标签快照刷新。
public class TerminalFontZoomTests
{
    private sealed class InMemorySettingsService : ISettingsService, INotifySettingsCommitted
    {
        public InMemorySettingsService(AppSettings? initial = null) => Current = initial ?? new AppSettings();

        public AppSettings Current { get; private set; }

        public int SaveCount { get; private set; }

        public event EventHandler? SettingsCommitted;

        public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);

        public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
        {
            SaveCount++;
            // 与 JsonSettingsService 一致：先更新内存再落盘
            Current = settings;
            return Task.CompletedTask;
        }

        // 与生产实现相同：先改内存并通知，再保存。设置页靠这个事件看到新字号。
        public Task CommitFontSizeAsync(double fontSize, CancellationToken ct = default)
        {
            Current.FontSize = fontSize;
            SettingsCommitted?.Invoke(this, EventArgs.Empty);
            return SaveSettingsAsync(Current, ct);
        }
    }

    private sealed class DummyTreeRepository : ITreeRepository
    {
        public Task<IReadOnlyList<TreeNodeBase>> GetAllNodesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TreeNodeBase>>(Array.Empty<TreeNodeBase>());

        public Task<TreeNodeBase?> GetNodeByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<TreeNodeBase?>(null);
        public Task SaveNodeAsync(TreeNodeBase node, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteNodeAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveNodeAsync(Guid nodeId, Guid? newParentId, int sortOrder, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateFolderExpandedAsync(Guid folderId, bool isExpanded, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DummyIdentityRepository : IIdentityRepository
    {
        public Task<IReadOnlyList<Identity>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Identity>>(Array.Empty<Identity>());
        public Task<Identity?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Identity?>(null);
        public Task SaveAsync(Identity identity, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DummyVaultManager : IVaultManager
    {
        public bool IsUnlocked => true;
        public bool IsPlainMode => true;
        public Task SetMasterPasswordAsync(string masterPassword, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryAutoUnlockAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task UnlockAsync(string masterPassword, bool rememberOnThisDevice, CancellationToken ct = default) => Task.CompletedTask;
        public void Lock() { }
    }

    private sealed class DummyVaultSecretStore : IVaultSecretStore
    {
        public Task<Dictionary<string, SecretPayload>> GetSecretsAsync(Guid identityId, CancellationToken ct = default) => Task.FromResult(new Dictionary<string, SecretPayload>());
        public Task SaveSecretsAsync(Guid identityId, Dictionary<string, SecretPayload> secrets, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteSecretsAsync(Guid identityId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DummySshSessionFactory : ISshSessionFactory
    {
        public Task<ISshSession> CreateSessionAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IRemoteFileSystem> CreateFileSystemAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            ISshSession? activeSession = null,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private static MainViewModel CreateMainViewModel(ISettingsService settings)
        => new(
            new DummyTreeRepository(),
            new DummyIdentityRepository(),
            new DummyVaultManager(),
            new DummyVaultSecretStore(),
            settings,
            new DummySshSessionFactory(),
            editorRepo: null,
            profileManager: null,
            logger: null,
            loggerFactory: null,
            // 测试注入同步分派器，避免依赖 Avalonia UI 线程
            uiDispatch: action => action());

    // 纯步进：每格 ±1pt，钳制到 8–36
    [Fact]
    public void Step_AddsOnePointPerNotch_AndClampsToRange()
    {
        Assert.Equal(15.0, TerminalFontZoom.Step(14.0, 1));
        Assert.Equal(13.0, TerminalFontZoom.Step(14.0, -1));
        Assert.Equal(8.0, TerminalFontZoom.Step(8.0, -1));
        Assert.Equal(36.0, TerminalFontZoom.Step(36.0, 1));
    }

    // 小数 delta 需累积满一格；不足部分作为余量留到下次
    [Fact]
    public void Accumulator_EmitsNotchOnlyWhenFull_AndKeepsRemainder()
    {
        var accumulator = new WheelNotchAccumulator();

        Assert.Equal(0, accumulator.Accumulate(0.4));
        Assert.Equal(0, accumulator.Accumulate(0.4));
        Assert.Equal(1, accumulator.Accumulate(0.4));
        Assert.Equal(0.2, accumulator.Remainder, 5);
    }

    // 负向累积同样按满一格才步进
    [Fact]
    public void Accumulator_HandlesNegativeDeltas()
    {
        var accumulator = new WheelNotchAccumulator();

        Assert.Equal(0, accumulator.Accumulate(-0.6));
        Assert.Equal(-1, accumulator.Accumulate(-0.6));
        Assert.Equal(-0.2, accumulator.Remainder, 5);
    }

    // 缩放写回全局字号并刷新所有已开标签的快照，不创建真实 TerminalControl
    [Fact]
    public void ApplyGlobalFontZoom_UpdatesSettingsAndAllOpenTabSnapshots()
    {
        var settings = new InMemorySettingsService();
        var vm = CreateMainViewModel(settings);
        // 不走 OpenTab：它会立刻创建真实 TerminalControl，并行时会占错 Avalonia 线程
        AddSnapshotTab(vm, settings, "A");
        AddSnapshotTab(vm, settings, "B");

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Equal(14.0, settings.Current.FontSize);
        Assert.Equal(14.0, vm.Tabs[0].CurrentFontSnapshot.FontSize);

        vm.ApplyGlobalFontZoom(1);

        Assert.Equal(15.0, settings.Current.FontSize);
        Assert.Equal(15.0, vm.Tabs[0].CurrentFontSnapshot.FontSize);
        Assert.Equal(15.0, vm.Tabs[1].CurrentFontSnapshot.FontSize);
        // 立即保存一次
        Assert.Equal(1, settings.SaveCount);
    }

    // 已到边界：字号不变则不写不存
    [Fact]
    public void ApplyGlobalFontZoom_AtBoundary_DoesNotSave()
    {
        var settings = new InMemorySettingsService();
        settings.Current.FontSize = 36.0;
        var vm = CreateMainViewModel(settings);
        AddSnapshotTab(vm, settings, "A");

        vm.ApplyGlobalFontZoom(1);

        Assert.Equal(36.0, settings.Current.FontSize);
        Assert.Equal(36.0, vm.Tabs[0].CurrentFontSnapshot.FontSize);
        Assert.Equal(0, settings.SaveCount);
    }

    // 设置已打开：滚轮只推进字号草稿，其他未保存编辑保持原样
    [Fact]
    public void WheelZoom_WhileSettingsAlreadyOpen_ShowsNewFontSize_AndKeepsOtherDrafts()
    {
        string tempDir = TempSettingsDir();
        try
        {
            var settings = new InMemorySettingsService();
            var main = CreateMainViewModel(settings);
            var settingsVm = new SettingsViewModel(settings, tempDir);
            var appearance = AppearanceOf(settingsVm);
            var general = GeneralOf(settingsVm);
            appearance.FontFamily = "Custom Mono";
            appearance.CursorBlink = false;
            general.ConfirmBeforeClose = false;
            general.AutoReconnectOnDisconnect = true;

            main.ApplyGlobalFontZoom(2);

            Assert.Equal(16.0, appearance.FontSize);
            Assert.Equal("Custom Mono", appearance.FontFamily);
            Assert.False(appearance.CursorBlink);
            Assert.False(general.ConfirmBeforeClose);
            Assert.True(general.AutoReconnectOnDisconnect);
            Assert.Equal(16.0, settings.Current.FontSize);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // 重开设置：从已提交字号重读，而不是停留在打开前的旧值
    [Fact]
    public void WheelZoom_ThenSettingsReopened_ShowsCommittedFontSize()
    {
        string tempDir = TempSettingsDir();
        try
        {
            var settings = new InMemorySettingsService();
            var main = CreateMainViewModel(settings);
            var open = new SettingsViewModel(settings, tempDir);
            AppearanceOf(open).FontSize = 20.0;

            main.ApplyGlobalFontZoom(1);
            // 手改尚未保存时，已打开的页面不跟滚轮
            Assert.Equal(20.0, AppearanceOf(open).FontSize);

            open.Reload();
            Assert.Equal(15.0, AppearanceOf(open).FontSize);

            var reopened = new SettingsViewModel(settings, tempDir);
            Assert.Equal(15.0, AppearanceOf(reopened).FontSize);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // 保存其他草稿时，不能把打开设置时的旧字号写回去
    [Fact]
    public async Task WheelZoom_ThenApplyOtherDrafts_PersistsZoom_AndKeepsThoseDrafts()
    {
        string tempDir = TempSettingsDir();
        try
        {
            var settings = new InMemorySettingsService();
            var main = CreateMainViewModel(settings);
            var settingsVm = new SettingsViewModel(settings, tempDir);
            AppearanceOf(settingsVm).FontFamily = "Custom Mono";
            GeneralOf(settingsVm).ConfirmBeforeClose = false;

            main.ApplyGlobalFontZoom(1);
            Assert.True(await settingsVm.ApplyChangesAsync());

            Assert.Equal(15.0, settings.Current.FontSize);
            Assert.Equal("Custom Mono", settings.Current.FontFamily);
            Assert.False(settings.Current.ConfirmBeforeClose);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // 用户正在改字号：滚轮不冲掉草稿，随后保存以手改为准，其他草稿仍在
    [Fact]
    public async Task ManualFontSizeDraft_IsNotOverwrittenByWheelZoom_AndSaveKeepsIt()
    {
        string tempDir = TempSettingsDir();
        try
        {
            var settings = new InMemorySettingsService();
            var main = CreateMainViewModel(settings);
            var settingsVm = new SettingsViewModel(settings, tempDir);
            var appearance = AppearanceOf(settingsVm);
            appearance.FontSize = 20.0;
            appearance.FontFamily = "Custom Mono";
            GeneralOf(settingsVm).ConfirmBeforeClose = false;

            main.ApplyGlobalFontZoom(1);

            Assert.Equal(20.0, appearance.FontSize);
            Assert.Equal("Custom Mono", appearance.FontFamily);
            Assert.Equal(15.0, settings.Current.FontSize);

            Assert.True(await settingsVm.ApplyChangesAsync());

            Assert.Equal(20.0, settings.Current.FontSize);
            Assert.Equal("Custom Mono", settings.Current.FontFamily);
            Assert.False(settings.Current.ConfirmBeforeClose);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // 没有通知时，未手改的旧字号草稿也不能在保存时覆盖外部缩放
    [Fact]
    public async Task Apply_WithoutFontSizeNotification_DoesNotPersistStaleDraftOverExternalZoom()
    {
        string tempDir = TempSettingsDir();
        try
        {
            var settings = new FixedSettingsService();
            var settingsVm = new SettingsViewModel(settings, tempDir);
            GeneralOf(settingsVm).ConfirmBeforeClose = false;
            settings.Current.FontSize = 17.0;
            settings.Current.UiFontSize = 15.0;

            Assert.Equal(14.0, AppearanceOf(settingsVm).FontSize);
            Assert.True(await settingsVm.ApplyChangesAsync());

            Assert.Equal(17.0, settings.Current.FontSize);
            Assert.False(settings.Current.ConfirmBeforeClose);
            Assert.Equal(15.0, settings.Current.UiFontSize);
            Assert.Equal(17.0, AppearanceOf(settingsVm).FontSize);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // 字号提交落盘后重读，无关字段保持原值（滚轮不能写成一份旧的完整快照）
    [Fact]
    public async Task CommitFontSize_PersistsZoom_AndKeepsUnrelatedSettings()
    {
        string tempDir = TempSettingsDir();
        string path = Path.Combine(tempDir, "settings.json");
        try
        {
            var service = new JsonSettingsService(path);
            service.Current.ConfirmBeforeClose = false;
            service.Current.UiFontSize = 15.0;
            service.Current.FileTransfer.PollingIntervalSeconds = 17;
            service.Current.FileTransfer.IsFileManagerOnLeft = true;
            await service.SaveSettingsAsync(service.Current);

            await service.CommitFontSizeAsync(19.0);

            var reloaded = await new JsonSettingsService(path).LoadSettingsAsync();
            Assert.Equal(19.0, reloaded.FontSize);
            Assert.False(reloaded.ConfirmBeforeClose);
            Assert.Equal(15.0, reloaded.UiFontSize);
            Assert.Equal(17, reloaded.FileTransfer.PollingIntervalSeconds);
            Assert.True(reloaded.FileTransfer.IsFileManagerOnLeft);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // 14→15→16 排队：15 拿到锁后不得把内存打回 15，否则下一格从 15 计算会丢步，设置页也会和 Current 不一致。
    [Fact]
    public async Task RapidWheelSteps_WhilePreviousPersistQueued_DoNotLoseAStepOrDesyncSettings()
    {
        string tempDir = TempSettingsDir();
        string path = Path.Combine(tempDir, "settings.json");
        var service = new LockGateSettingsService(path);
        SettingsViewModel? settingsVm = null;
        try
        {
            service.Current.FontSize = 14;
            service.Current.ConfirmBeforeClose = false;
            service.Current.FileTransfer.PollingIntervalSeconds = 17;
            await service.SaveSettingsAsync(service.Current);

            var main = CreateMainViewModel(service);
            settingsVm = new SettingsViewModel(service, tempDir);

            LockHold blocker = service.EnqueueHold();
            Task blocking = service.CommitFontSizeAsync(14);
            await blocker.Entered;

            main.ApplyGlobalFontZoom(1);
            main.ApplyGlobalFontZoom(1);
            Assert.Equal(16, service.Current.FontSize);
            Assert.Equal(16, AppearanceOf(settingsVm).FontSize);

            LockHold oldPersist = service.EnqueueHold();
            blocker.Release();
            await oldPersist.Entered;

            Assert.Equal(16, service.Current.FontSize);
            Assert.Equal(16, AppearanceOf(settingsVm).FontSize);

            main.ApplyGlobalFontZoom(1);
            Assert.Equal(17, service.Current.FontSize);
            Assert.Equal(17, AppearanceOf(settingsVm).FontSize);
            Assert.False(service.Current.ConfirmBeforeClose);

            oldPersist.Release();
            Task drain = service.CommitFontSizeAsync(service.Current.FontSize);
            await Task.WhenAll(blocking, drain);

            var reloaded = await new JsonSettingsService(path).LoadSettingsAsync();
            Assert.Equal(17, reloaded.FontSize);
            Assert.False(reloaded.ConfirmBeforeClose);
            Assert.Equal(17, reloaded.FileTransfer.PollingIntervalSeconds);
        }
        finally
        {
            settingsVm?.Dispose();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // 整对象保存插在两次字号提交之间：旧落盘不得回退最后字号，也不得把保存带上的无关字段打回旧快照。
    [Fact]
    public async Task QueuedFontCommits_DoNotRollBackLatestSize_AndFullSaveKeepsUnrelatedFields()
    {
        string tempDir = TempSettingsDir();
        string path = Path.Combine(tempDir, "settings.json");
        try
        {
            var service = new LockGateSettingsService(path);
            service.Current.FontSize = 14;
            service.Current.ConfirmBeforeClose = true;
            service.Current.UiFontSize = 13;
            service.Current.FileTransfer.PollingIntervalSeconds = 3;
            await service.SaveSettingsAsync(service.Current);

            LockHold blocker = service.EnqueueHold();
            Task blocking = service.CommitFontSizeAsync(14);
            await blocker.Entered;

            var stale = new AppSettings
            {
                FontSize = 14,
                ConfirmBeforeClose = false,
                UiFontSize = 15,
                FileTransfer = new FileTransferSettings
                {
                    PollingIntervalSeconds = 19,
                    IsFileManagerOnLeft = true
                }
            };
            Task save = service.SaveSettingsAsync(stale);
            Task commit15 = service.CommitFontSizeAsync(15);
            Task commit16 = service.CommitFontSizeAsync(16);
            Assert.Equal(16, service.Current.FontSize);

            LockHold oldPersist = service.EnqueueHold();
            blocker.Release();
            await oldPersist.Entered;

            Assert.Equal(16, service.Current.FontSize);
            Assert.False(service.Current.ConfirmBeforeClose);
            Assert.Equal(15, service.Current.UiFontSize);
            Assert.Equal(19, service.Current.FileTransfer.PollingIntervalSeconds);
            Assert.True(service.Current.FileTransfer.IsFileManagerOnLeft);

            oldPersist.Release();
            await Task.WhenAll(blocking, save, commit15, commit16);

            Assert.Equal(16, service.Current.FontSize);
            var reloaded = await new JsonSettingsService(path).LoadSettingsAsync();
            Assert.Equal(16, reloaded.FontSize);
            Assert.False(reloaded.ConfirmBeforeClose);
            Assert.Equal(15, reloaded.UiFontSize);
            Assert.Equal(19, reloaded.FileTransfer.PollingIntervalSeconds);
            Assert.True(reloaded.FileTransfer.IsFileManagerOnLeft);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // 没有更新的字号提交时，整对象保存仍写入快照里的字号（手改不被代际补丁吞掉）
    [Fact]
    public async Task SaveSettingsAsync_WithoutNewerFontCommit_PersistsSnapshotFontSize()
    {
        string tempDir = TempSettingsDir();
        string path = Path.Combine(tempDir, "settings.json");
        try
        {
            var service = new JsonSettingsService(path);
            await service.CommitFontSizeAsync(16);
            var snapshot = new AppSettings
            {
                FontSize = 20,
                ConfirmBeforeClose = false,
                UiFontSize = 15
            };

            await service.SaveSettingsAsync(snapshot);

            var reloaded = await new JsonSettingsService(path).LoadSettingsAsync();
            Assert.Equal(20, reloaded.FontSize);
            Assert.False(reloaded.ConfirmBeforeClose);
            Assert.Equal(15, reloaded.UiFontSize);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // 设置 VM 不是单例：释放后不得再跟着外部字号走，也不能继续钉在设置服务上
    [Fact]
    public async Task DisposedSettingsViewModel_StopsFollowingExternalFontSize()
    {
        string tempDir = TempSettingsDir();
        try
        {
            var settings = new InMemorySettingsService();
            var disposed = new SettingsViewModel(settings, tempDir);
            var live = new SettingsViewModel(settings, tempDir);
            disposed.Dispose();

            await settings.CommitFontSizeAsync(18);

            Assert.Equal(14, AppearanceOf(disposed).FontSize);
            Assert.Equal(18, AppearanceOf(live).FontSize);
            live.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class LockHold
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void MarkEntered() => _entered.TrySetResult();

        public void Release() => _release.TrySetResult();

        public Task WaitReleaseAsync(CancellationToken ct) => _release.Task.WaitAsync(ct);
    }

    // 在保存锁内、写盘前停下。用来把 15 的落盘卡在 16 已经提交之后，不靠 sleep。
    private sealed class LockGateSettingsService : JsonSettingsService
    {
        private readonly ConcurrentQueue<LockHold> _holds = new();

        public LockGateSettingsService(string path) : base(path)
        {
        }

        public LockHold EnqueueHold()
        {
            var hold = new LockHold();
            _holds.Enqueue(hold);
            return hold;
        }

        protected override async Task OnSaveLockHeldAsync(CancellationToken ct)
        {
            if (!_holds.TryDequeue(out LockHold? hold))
            {
                return;
            }

            hold.MarkEntered();
            await hold.WaitReleaseAsync(ct);
        }
    }

    // 只放进标签集合，不访问 Terminal，因此不会创建原生控件
    private sealed class NoControlThemeSink : ITerminalThemeSink
    {
        public void Apply(TerminalProfile profile)
        {
        }

        public bool TryReapplySelectionOverride(TerminalProfile profile) => false;
    }

    private static void AddSnapshotTab(MainViewModel vm, ISettingsService settings, string name)
    {
        AppSettings current = settings.Current;
        var tab = new TerminalTabViewModel(
            name,
            new TerminalFontSnapshot(current.FontFamily, [], current.FontSize, current.CursorBlink),
            BuiltInPresets.GetDefaultTerminalProfile(),
            explicitProfileId: null,
            themeSink: new NoControlThemeSink());
        vm.Tabs.Add(tab);
    }

    private static string TempSettingsDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"keiterm-fontzoom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static AppearanceSettingsPage AppearanceOf(SettingsViewModel vm)
        => vm.Categories.Select(c => c.Page).OfType<AppearanceSettingsPage>().Single();

    private static GeneralSettingsPage GeneralOf(SettingsViewModel vm)
        => vm.Categories.Select(c => c.Page).OfType<GeneralSettingsPage>().Single();
}
