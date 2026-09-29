using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Ssh.Abstractions;
using RoyalTerminal.Avalonia.Controls;
using Xunit;

namespace Kei.Term.Tests;

// Package B：有效预览/已提交分离、同 ID 刷新、设置事务门控、原子持久化与订阅释放。
// 纯 VM/Core：配色注入用 RecordingThemeSink，Terminal 用“禁止创建”工厂隔离原生控件。
public class TerminalProfileApplicationTests
{
    private static TerminalProfile Profile(string id, string foreground = "#111111")
    {
        var profile = new TerminalProfile
        {
            Id = id,
            Name = id,
            Foreground = foreground,
            Background = "#000000",
            CursorColor = "#FFFFFF",
            SelectionBackground = "#264F78",
            AnsiColors = new string[16]
        };

        for (int i = 0; i < 16; i++)
        {
            profile.AnsiColors[i] = "#010101";
        }

        return profile;
    }

    private static TerminalFontSnapshot Font(string family = "JetBrains Mono")
        => new(family, new[] { "Noto Sans Mono" }, 14.0, true);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"keiterm_b_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Task WriteBundleAsync(string path, ProfileBundle bundle)
        => File.WriteAllTextAsync(path, ProfileBundleSerializer.Serialize(bundle));

    // 记录主题注入，不触碰原生控件
    private sealed class RecordingThemeSink : ITerminalThemeSink
    {
        public List<TerminalProfile> Applied { get; } = new();

        public List<TerminalProfile> Reapplied { get; } = new();

        // 是否模拟 Renderer 就绪；false 用于验证失败路径不抛异常
        public bool ReapplyResult { get; set; } = true;

        public void Apply(TerminalProfile profile) => Applied.Add(profile);

        public bool TryReapplySelectionOverride(TerminalProfile profile)
        {
            Reapplied.Add(profile);
            return ReapplyResult;
        }
    }

    // 记录/禁止 TerminalControl 创建；返回计数与抛错工厂
    private sealed class TerminalFactoryProbe
    {
        public int Calls { get; private set; }

        public TerminalControl Create()
        {
            Calls++;
            throw new InvalidOperationException("纯状态测试不得创建原生 TerminalControl");
        }
    }

    private sealed class InMemorySettingsService : ISettingsService
    {
        public InMemorySettingsService(AppSettings? initial = null) => Current = initial ?? new AppSettings();

        public AppSettings Current { get; private set; }

        // 剩余“写入抛错”次数，用于模拟落盘失败
        public int FailuresRemaining { get; set; }

        public int SaveCount { get; private set; }

        public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);

        public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
        {
            SaveCount++;
            // 与 JsonSettingsService 一致：先更新内存再落盘
            Current = settings;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("disk full");
            }

            return Task.CompletedTask;
        }
    }

    // 可暂停的 settings 服务：用于验证 Apply 期间的事务门控
    private sealed class PausableSettingsService : ISettingsService
    {
        private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AppSettings Current { get; private set; } = new();

        public bool PauseNextSave { get; set; }

        public TaskCompletionSource<bool> SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult(true);

        public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);

        public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
        {
            SaveStarted.TrySetResult(true);
            if (PauseNextSave)
            {
                PauseNextSave = false;
                await _gate.Task;
            }

            Current = settings;
        }
    }

    // profiles.json 写入必失败：验证旧文件保留
    private sealed class FailingProfileStore : IProfileStore
    {
        public int Calls { get; private set; }

        public Task WriteAsync(string filePath, string json, CancellationToken ct = default)
        {
            Calls++;
            throw new IOException("simulated disk failure");
        }
    }

    private sealed class DummyTreeRepository : ITreeRepository
    {
        public Task<IReadOnlyList<TreeNodeBase>> GetAllNodesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TreeNodeBase>>(Array.Empty<TreeNodeBase>());
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

        public Task<Kei.Term.Core.Abstractions.IRemoteFileSystem> CreateFileSystemAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            ISshSession? activeSession = null,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private static MainViewModel CreateMainViewModel(ISettingsService settings, ProfileManagerService profileManager)
    {
        return new MainViewModel(
            new DummyTreeRepository(),
            new DummyIdentityRepository(),
            new DummyVaultManager(),
            new DummyVaultSecretStore(),
            settings,
            new DummySshSessionFactory(),
            editorRepo: null,
            profileManager: profileManager,
            logger: null,
            loggerFactory: null,
            // 测试注入同步分派器，避免依赖 Avalonia UI 线程
            uiDispatch: action => action());
    }

    private static TerminalTabViewModel CreateTab(
        TerminalProfile profile,
        string? explicitId,
        RecordingThemeSink sink,
        TerminalFactoryProbe? probe = null)
    {
        probe ??= new TerminalFactoryProbe();
        return new TerminalTabViewModel(
            "Tab",
            Font(),
            profile,
            explicitId,
            logger: null,
            terminalFactory: probe.Create,
            themeSink: sink);
    }

    private static AppearanceSettingsPage AppearanceOf(SettingsViewModel vm)
        => (AppearanceSettingsPage)Assert.Single(vm.Categories, c => c.Page is AppearanceSettingsPage).Page!;

    // A) 解析顺序：显式 ID → 全局默认 ID → 内置默认
    [Fact]
    public async Task ResolveEffectiveTerminalProfile_FollowsExplicitThenDefaultThenBuiltIn()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var custom = Profile("Custom-X");
            manager.AddOrUpdateCustomTerminalProfile(custom);

            Assert.Equal("Custom-X", manager.ResolveEffectiveTerminalProfile("Custom-X").Id);

            var builtIn = BuiltInPresets.GetDefaultTerminalProfile();
            Assert.Equal(builtIn.Id, manager.ResolveEffectiveTerminalProfile("不存在").Id);
            Assert.Equal(builtIn.Id, manager.ResolveEffectiveTerminalProfile(null).Id);

            manager.NotifyDefaultProfileSelectionChanged("Custom-X");
            Assert.Equal("Custom-X", manager.ResolveEffectiveTerminalProfile(null).Id);
            Assert.Equal("Custom-X", manager.ResolveEffectiveTerminalProfile("不存在").Id);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // B) 默认切换只刷新继承标签，显式覆盖标签不跟随
    [Fact]
    public async Task DefaultSelectionChange_RefreshesInheritingTabsOnly()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var customX = Profile("Custom-X");
            var customY = Profile("Custom-Y");
            manager.AddOrUpdateCustomTerminalProfile(customX);
            manager.AddOrUpdateCustomTerminalProfile(customY);

            var vm = CreateMainViewModel(settings, manager);
            var inheritTab = CreateTab(manager.ResolveEffectiveTerminalProfile(null), null, new RecordingThemeSink());
            var explicitTab = CreateTab(customX, "Custom-X", new RecordingThemeSink());
            vm.Tabs.Add(inheritTab);
            vm.Tabs.Add(explicitTab);

            manager.NotifyDefaultProfileSelectionChanged("Custom-Y");

            Assert.Equal("Custom-Y", inheritTab.EffectiveProfileId);
            Assert.Equal("Custom-X", explicitTab.EffectiveProfileId);
            Assert.Equal("Custom-Y", inheritTab.CurrentProfile.Id);
            Assert.Equal("Custom-X", explicitTab.CurrentProfile.Id);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // C) 同 ID 内容编辑：继承与显式使用该 ID 的标签都刷新
    [Fact]
    public async Task ProfileContentEdited_RefreshesInheritingAndExplicitTabsUsingId()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var customX = Profile("Custom-X", "#111111");
            manager.AddOrUpdateCustomTerminalProfile(customX);
            manager.NotifyDefaultProfileSelectionChanged("Custom-X");

            var vm = CreateMainViewModel(settings, manager);
            var inheritTab = CreateTab(manager.ResolveEffectiveTerminalProfile(null), null, new RecordingThemeSink());
            var explicitTab = CreateTab(customX, "Custom-X", new RecordingThemeSink());
            var unrelatedTab = CreateTab(Profile("Other"), "Other", new RecordingThemeSink());
            vm.Tabs.Add(inheritTab);
            vm.Tabs.Add(explicitTab);
            vm.Tabs.Add(unrelatedTab);

            manager.AddOrUpdateCustomTerminalProfile(Profile("Custom-X", "#ABCDEF"));

            Assert.Equal("#ABCDEF", inheritTab.CurrentProfile.Foreground);
            Assert.Equal("#ABCDEF", explicitTab.CurrentProfile.Foreground);
            Assert.Equal("#111111", unrelatedTab.CurrentProfile.Foreground);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // I2) 同 ID 草稿编辑后取消：显式标签内容也必须随已提交权威恢复
    [Fact]
    public async Task SameIdEdit_ThenCancel_RestoresExplicitTabContent()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var committed = Profile("Custom-Shared", "#111111");
            manager.AddOrUpdateCustomTerminalProfile(committed);
            manager.NotifyDefaultProfileSelectionChanged("Custom-Shared");
            await manager.CommitAndSaveProfilesAsync();

            var vm = CreateMainViewModel(settings, manager);
            var inheritSink = new RecordingThemeSink();
            var explicitSink = new RecordingThemeSink();
            var inheritTab = CreateTab(manager.ResolveEffectiveTerminalProfile(null), null, inheritSink);
            var explicitTab = CreateTab(committed, "Custom-Shared", explicitSink);
            vm.Tabs.Add(inheritTab);
            vm.Tabs.Add(explicitTab);

            var settingsVm = new SettingsViewModel(settings, tempDir, identityRepo: null, profileManager: manager);

            // 未提交的同 ID 草稿
            manager.AddOrUpdateCustomTerminalProfile(Profile("Custom-Shared", "#ABCDEF"));
            Assert.Equal("#ABCDEF", explicitTab.CurrentProfile.Foreground);
            Assert.Equal("#ABCDEF", inheritTab.CurrentProfile.Foreground);
            Assert.Equal("#ABCDEF", explicitSink.Applied[^1].Foreground);

            await settingsVm.CancelCommand.ExecuteAsync(null);

            // 显式标签与继承标签都恢复为已提交内容，且通过 sink 真实注入
            Assert.Equal("#111111", explicitTab.CurrentProfile.Foreground);
            Assert.Equal("#111111", inheritTab.CurrentProfile.Foreground);
            Assert.NotEmpty(explicitSink.Applied);
            Assert.Equal("Custom-Shared", explicitSink.Applied[^1].Id);
            Assert.Equal("#111111", explicitSink.Applied[^1].Foreground);
            Assert.Equal("#111111", inheritSink.Applied[^1].Foreground);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // I5) 设置内 Bundle 导入纳入父事务：取消后不落盘 draft / 导入内容
    [Fact]
    public async Task SettingsBundleImport_ThenCancel_DoesNotPersistDraftsOrImports()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var settingsVm = new SettingsViewModel(settings, tempDir, identityRepo: null, profileManager: manager);
            var appearance = AppearanceOf(settingsVm);

            // 未提交草稿
            var draft = Profile("Custom-Draft");
            manager.AddOrUpdateCustomTerminalProfile(draft);
            appearance.SelectedTerminalProfile = draft;

            // 设置内 Bundle 导入（persist:false，应由父事务决定）
            var bundle = new ProfileBundle
            {
                TerminalProfiles = new List<TerminalProfile> { Profile("Bundle-Imported") }
            };
            await manager.ImportBundleAsync(ProfileBundleSerializer.Serialize(bundle), overwrite: true, persist: false);

            await settingsVm.CancelCommand.ExecuteAsync(null);

            // 重新从磁盘初始化：draft 与导入内容都不存在
            var reopened = new ProfileManagerService(settings, tempDir);
            await reopened.InitializeAsync();
            Assert.DoesNotContain(reopened.AllTerminalProfiles, p => p.Id == "Custom-Draft");
            Assert.DoesNotContain(reopened.AllTerminalProfiles, p => p.Id == "Bundle-Imported");

            // 内存有效状态同样已回滚
            Assert.DoesNotContain(manager.AllTerminalProfiles, p => p.Id == "Custom-Draft");
            Assert.DoesNotContain(manager.AllTerminalProfiles, p => p.Id == "Bundle-Imported");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // I5b) 设置 Bundle 导入后 Apply：导入内容随父事务统一提交
    [Fact]
    public async Task SettingsBundleImport_ThenApply_PersistsImportedProfile()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var settingsVm = new SettingsViewModel(settings, tempDir, identityRepo: null, profileManager: manager);

            var bundle = new ProfileBundle
            {
                TerminalProfiles = new List<TerminalProfile> { Profile("Bundle-Imported") }
            };
            await manager.ImportBundleAsync(ProfileBundleSerializer.Serialize(bundle), overwrite: true, persist: false);

            Assert.True(await settingsVm.ApplyChangesAsync());

            var reopened = new ProfileManagerService(settings, tempDir);
            await reopened.InitializeAsync();
            Assert.Contains(reopened.AllTerminalProfiles, p => p.Id == "Bundle-Imported");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // I6) Apply 期间事务门控：Cancel/Save 被拒、选择变更被抑制、IsBusy 正确翻转
    [Fact]
    public async Task ApplyInProgress_BlocksCancelSaveAndPreview()
    {
        var settings = new PausableSettingsService { PauseNextSave = true };
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var a = Profile("Custom-A");
            manager.AddOrUpdateCustomTerminalProfile(a);
            manager.NotifyDefaultProfileSelectionChanged("Custom-A");

            var settingsVm = new SettingsViewModel(settings, tempDir, identityRepo: null, profileManager: manager);
            var appearance = AppearanceOf(settingsVm);
            appearance.SelectedTerminalProfile = a;

            int closeCount = 0;
            settingsVm.RequestClose += () => closeCount++;

            var applyTask = settingsVm.ApplyChangesAsync();
            await settings.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(settingsVm.IsBusy);

            // busy 中 Cancel 不得触发关闭
            await settingsVm.CancelCommand.ExecuteAsync(null);
            Assert.Equal(0, closeCount);
            Assert.True(settingsVm.IsBusy);

            // busy 中 Save 不得再次提交/关闭
            await settingsVm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(0, closeCount);

            // busy 中改选择不生效（预览被抑制）
            var b = Profile("Custom-B");
            appearance.SelectedTerminalProfile = b;
            Assert.Equal("Custom-A", manager.ResolveEffectiveTerminalProfile(null).Id);

            settings.Release();
            Assert.True(await applyTask);
            Assert.False(settingsVm.IsBusy);
            Assert.Equal("Custom-A", manager.ResolveEffectiveTerminalProfile(null).Id);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // I7) 原子写失败：旧文件与旧内存权威保留，新方案不落盘
    [Fact]
    public async Task AtomicWriteFailure_KeepsOldFileAndCommittedSnapshot()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            // 先用真实原子写建立磁盘基线
            var seed = new ProfileManagerService(settings, tempDir);
            await seed.InitializeAsync();
            seed.AddOrUpdateCustomTerminalProfile(Profile("Keep-Me", "#111111"));
            await seed.CommitAndSaveProfilesAsync();

            // 失败存储 + 同一目录：加载旧文件后提交新方案
            var failingStore = new FailingProfileStore();
            var manager = new ProfileManagerService(settings, tempDir, failingStore);
            await manager.InitializeAsync();
            Assert.Equal("#111111", manager.ResolveEffectiveTerminalProfile("Keep-Me").Foreground);

            manager.AddOrUpdateCustomTerminalProfile(Profile("New-One", "#222222"));
            await Assert.ThrowsAsync<IOException>(() => manager.CommitAndSaveProfilesAsync());
            Assert.True(failingStore.Calls > 0);

            // 内存已提交权威未推进：导出不含 New-One，仍含 Keep-Me
            string exported = manager.ExportBundle();
            Assert.Contains("Keep-Me", exported);
            Assert.DoesNotContain("New-One", exported);

            // 磁盘旧文件保留：重新初始化仍只有 Keep-Me
            var reopened = new ProfileManagerService(settings, tempDir);
            await reopened.InitializeAsync();
            Assert.Contains(reopened.AllTerminalProfiles, p => p.Id == "Keep-Me");
            Assert.DoesNotContain(reopened.AllTerminalProfiles, p => p.Id == "New-One");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // I8) Dispose 后到达的排队事件不再刷新（shutdown race 安全）
    [Fact]
    public async Task DisposeAsync_UnsubscribesFromProfileChanges()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var vm = CreateMainViewModel(settings, manager);
            var tab = CreateTab(manager.ResolveEffectiveTerminalProfile(null), null, new RecordingThemeSink());
            vm.Tabs.Add(tab);

            manager.NotifyDefaultProfileSelectionChanged(BuiltInPresets.GetDefaultTerminalProfile().Id);
            int afterFirst = vm.TerminalProfileRefreshCount;
            Assert.True(afterFirst > 0);

            await vm.DisposeAsync();
            // 即使直接调用公开入口，也不再刷新
            vm.ApplyTerminalProfileChange(new TerminalProfileChange("Custom-After-Dispose", TerminalProfileChangeReason.DefaultSelectionChanged));
            manager.NotifyDefaultProfileSelectionChanged("Custom-After-Dispose");

            Assert.Equal(afterFirst, vm.TerminalProfileRefreshCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // D) 已关闭（移出 Tabs）的标签不再更新
    [Fact]
    public async Task ClosedTab_IsNotUpdated()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var vm = CreateMainViewModel(settings, manager);
            var tab = CreateTab(manager.ResolveEffectiveTerminalProfile(null), null, new RecordingThemeSink());
            vm.Tabs.Add(tab);
            vm.Tabs.Remove(tab);

            var before = tab.EffectiveProfileId;
            manager.AddOrUpdateCustomTerminalProfile(Profile("Custom-Z"));
            manager.NotifyDefaultProfileSelectionChanged("Custom-Z");

            Assert.Equal(before, tab.EffectiveProfileId);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // E) 选择即预览 + 取消回滚；预览期间新建的继承标签也被恢复
    [Fact]
    public async Task SettingsPreview_ThenCancel_RestoresBaselineAndPreviewBornTab()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var baselined = Profile("Custom-Base");
            manager.AddOrUpdateCustomTerminalProfile(baselined);
            manager.NotifyDefaultProfileSelectionChanged("Custom-Base");
            await manager.CommitAndSaveProfilesAsync();
            settings.Current.ActiveTerminalProfileId = "Custom-Base";

            var vm = CreateMainViewModel(settings, manager);
            var settingsVm = new SettingsViewModel(settings, tempDir, identityRepo: null, profileManager: manager);
            var appearance = AppearanceOf(settingsVm);

            var inheritTab = CreateTab(manager.ResolveEffectiveTerminalProfile(null), null, new RecordingThemeSink());
            vm.Tabs.Add(inheritTab);
            Assert.Equal("Custom-Base", inheritTab.EffectiveProfileId);

            var draft = Profile("Custom-Draft", "#222222");
            manager.AddOrUpdateCustomTerminalProfile(draft);
            appearance.SelectedTerminalProfile = draft;
            Assert.Equal("Custom-Draft", inheritTab.EffectiveProfileId);

            var previewBornTab = CreateTab(manager.ResolveEffectiveTerminalProfile(null), null, new RecordingThemeSink());
            vm.Tabs.Add(previewBornTab);
            Assert.Equal("Custom-Draft", previewBornTab.EffectiveProfileId);

            await settingsVm.CancelCommand.ExecuteAsync(null);

            Assert.Equal("Custom-Base", inheritTab.EffectiveProfileId);
            Assert.Equal("Custom-Base", previewBornTab.EffectiveProfileId);
            Assert.Equal("Custom-Base", manager.ResolveEffectiveTerminalProfile(null).Id);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // F) 应用成功推进权威；再次预览取消回最后成功状态
    [Fact]
    public async Task ApplyUpdatesBaseline_ThenCancelReturnsToLastApplied()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var settingsVm = new SettingsViewModel(settings, tempDir, identityRepo: null, profileManager: manager);
            var appearance = AppearanceOf(settingsVm);

            var first = Profile("Custom-First");
            manager.AddOrUpdateCustomTerminalProfile(first);
            appearance.SelectedTerminalProfile = first;

            Assert.True(await settingsVm.ApplyChangesAsync());
            Assert.Equal("Custom-First", manager.ResolveEffectiveTerminalProfile(null).Id);

            var second = Profile("Custom-Second");
            manager.AddOrUpdateCustomTerminalProfile(second);
            appearance.SelectedTerminalProfile = second;
            Assert.Equal("Custom-Second", manager.ResolveEffectiveTerminalProfile(null).Id);

            await settingsVm.CancelCommand.ExecuteAsync(null);
            Assert.Equal("Custom-First", manager.ResolveEffectiveTerminalProfile(null).Id);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // G) 设置写入失败：不推进权威、不假成功、有提示（补偿写回成功）
    [Fact]
    public async Task SaveFailure_DoesNotAdvanceBaseline_AndNotifies()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var settingsVm = new SettingsViewModel(settings, tempDir, identityRepo: null, profileManager: manager);
            var appearance = AppearanceOf(settingsVm);

            string? notifiedTitle = null;
            string? notifiedMessage = null;
            settingsVm.ShowNotificationAsync = (title, message) =>
            {
                notifiedTitle = title;
                notifiedMessage = message;
                return Task.CompletedTask;
            };

            var draft = Profile("Custom-Fail");
            manager.AddOrUpdateCustomTerminalProfile(draft);
            appearance.SelectedTerminalProfile = draft;
            manager.NotifyDefaultProfileSelectionChanged("Custom-Fail");

            settings.FailuresRemaining = 1;
            await settingsVm.ApplyCommand.ExecuteAsync(null);

            Assert.False(settingsVm.IsConfirmed);
            Assert.NotNull(notifiedTitle);
            Assert.False(string.IsNullOrEmpty(notifiedMessage));
            // 权威未推进（回滚到内置默认）
            Assert.NotEqual("Custom-Fail", manager.ResolveEffectiveTerminalProfile(null).Id);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // H) profiles 写入失败：恢复已提交状态并提示
    [Fact]
    public async Task ProfilesWriteFailure_RestoresCommittedStateAndNotifies()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        string fileInsteadOfDir = Path.Combine(tempDir, "not-a-directory");
        try
        {
            // 用一个“其实是文件”的路径当作 profiles 目录，令写入抛错
            await File.WriteAllTextAsync(fileInsteadOfDir, "x");
            var manager = new ProfileManagerService(settings, fileInsteadOfDir);
            await manager.InitializeAsync();

            var settingsVm = new SettingsViewModel(settings, fileInsteadOfDir, identityRepo: null, profileManager: manager);
            var appearance = AppearanceOf(settingsVm);

            string? notifiedMessage = null;
            settingsVm.ShowNotificationAsync = (_, message) =>
            {
                notifiedMessage = message;
                return Task.CompletedTask;
            };

            var baselineId = manager.ResolveEffectiveTerminalProfile(null).Id;
            var draft = Profile("Custom-PartialFail");
            manager.AddOrUpdateCustomTerminalProfile(draft);
            appearance.SelectedTerminalProfile = draft;

            await settingsVm.ApplyCommand.ExecuteAsync(null);

            Assert.False(settingsVm.IsConfirmed);
            Assert.False(string.IsNullOrEmpty(notifiedMessage));
            Assert.Equal(baselineId, manager.ResolveEffectiveTerminalProfile(null).Id);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // M2) Loaded 后选区 alpha 恢复：对最新 CurrentProfile 重放，失败不抛异常
    [Fact]
    public void ReapplySelectionOverride_UsesLatestProfile_AndFailureIsNonFatal()
    {
        var probe = new TerminalFactoryProbe();
        var sink = new RecordingThemeSink();
        var tab = new TerminalTabViewModel(
            "Tab",
            Font(),
            Profile("P-Start"),
            explicitProfileId: null,
            logger: null,
            terminalFactory: probe.Create,
            themeSink: sink);

        var latest = Profile("P-Latest", "#ABCDEF");
        tab.ApplyTerminalProfile(latest);

        Assert.True(tab.ReapplySelectionOverride());
        Assert.Single(sink.Reapplied);
        Assert.Equal("P-Latest", sink.Reapplied[^1].Id);

        // Renderer 未就绪：返回 false 且不抛异常
        sink.ReapplyResult = false;
        Assert.False(tab.ReapplySelectionOverride());
        // 全程未创建原生控件
        Assert.Equal(0, probe.Calls);
    }

    // M2) 纯状态标签不创建原生 TerminalControl，配色只经 sink
    [Fact]
    public void StateOnlyTab_DoesNotCreateNativeTerminal_AndInjectsThroughSink()
    {
        var probe = new TerminalFactoryProbe();
        var sink = new RecordingThemeSink();
        var tab = new TerminalTabViewModel(
            "Tab",
            Font("Fira Code"),
            Profile("P-Start"),
            explicitProfileId: null,
            logger: null,
            terminalFactory: probe.Create,
            themeSink: sink);

        var next = Profile("P-Next", "#ABCDEF");
        tab.ApplyTerminalProfile(next);
        tab.ApplyFontSnapshot(new TerminalFontSnapshot("Fira Code", new[] { "Fallback" }, 15.0, false));

        Assert.Equal(0, probe.Calls);
        Assert.Single(sink.Applied);
        Assert.Equal("P-Next", sink.Applied[0].Id);
        Assert.Equal("P-Next", tab.EffectiveProfileId);
        Assert.Same(next, tab.CurrentProfile);
    }

    // M2) New 命令：确认仅进草稿不落盘；取消无副作用
    [Fact]
    public async Task NewCommand_Confirm_DraftsWithoutPersisting_AndCancelIsSideEffectFree()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();
            var settingsVm = new SettingsViewModel(settings, tempDir, identityRepo: null, profileManager: manager);

            settingsVm.OpenTerminalProfileEditDialogAsync = (_, _) =>
                Task.FromResult<TerminalProfile?>(Profile("Custom-New"));

            await settingsVm.NewTerminalProfileCommand.ExecuteAsync(null);

            // 仅进有效状态
            Assert.Contains(manager.AllTerminalProfiles, p => p.Id == "Custom-New");
            var reopened = new ProfileManagerService(settings, tempDir);
            await reopened.InitializeAsync();
            Assert.DoesNotContain(reopened.AllTerminalProfiles, p => p.Id == "Custom-New");

            // 取消后无副作用
            await settingsVm.CancelCommand.ExecuteAsync(null);
            Assert.DoesNotContain(manager.AllTerminalProfiles, p => p.Id == "Custom-New");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // M2) Edit 命令：同 ID 草稿确认后不落盘；取消恢复已提交内容
    [Fact]
    public async Task EditCommand_Confirm_DraftsSameId_AndCancelRestoresCommitted()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var committed = Profile("Custom-C", "#111111");
            manager.AddOrUpdateCustomTerminalProfile(committed);
            await manager.CommitAndSaveProfilesAsync();

            var settingsVm = new SettingsViewModel(settings, tempDir, identityRepo: null, profileManager: manager);
            var appearance = AppearanceOf(settingsVm);
            appearance.SelectedTerminalProfile = manager.ResolveEffectiveTerminalProfile("Custom-C");

            settingsVm.OpenTerminalProfileEditDialogAsync = (source, _) =>
                Task.FromResult<TerminalProfile?>(Profile(source?.Id ?? "missing", "#999999"));

            await settingsVm.EditTerminalProfileCommand.ExecuteAsync(null);
            Assert.Equal("#999999", manager.ResolveEffectiveTerminalProfile("Custom-C").Foreground);

            // 磁盘仍是已提交内容
            var reopened = new ProfileManagerService(settings, tempDir);
            await reopened.InitializeAsync();
            Assert.Equal("#111111", reopened.ResolveEffectiveTerminalProfile("Custom-C").Foreground);

            await settingsVm.CancelCommand.ExecuteAsync(null);
            Assert.Equal("#111111", manager.ResolveEffectiveTerminalProfile("Custom-C").Foreground);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // M2) Konsole 导入命令：取消无副作用；确认仅进草稿不落盘
    [Fact]
    public async Task ImportKonsoleCommand_CancelAndConfirm_DoNotPersistUnappliedDraft()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();
            var settingsVm = new SettingsViewModel(settings, tempDir, identityRepo: null, profileManager: manager);

            var schemePath = Path.Combine(tempDir, "sample.colorscheme");
            await File.WriteAllTextAsync(schemePath, "[General]\nDescription=Sample\n[Background]\nColor=35,38,39\n[Foreground]\nColor=252,252,252\n");
            settingsVm.OpenKonsoleFileDialogAsync = () => Task.FromResult<string?>(schemePath);

            // 取消：不得产生任何自定义方案
            settingsVm.OpenTerminalProfileEditDialogAsync = (_, _) => Task.FromResult<TerminalProfile?>(null);
            await settingsVm.ImportKonsoleSchemeCommand.ExecuteAsync(null);
            Assert.DoesNotContain(manager.AllTerminalProfiles, p => !p.IsBuiltIn);

            // 确认：仅进有效状态，不落盘
            settingsVm.OpenTerminalProfileEditDialogAsync = (_, _) => Task.FromResult<TerminalProfile?>(Profile("Konsole-Imported"));
            await settingsVm.ImportKonsoleSchemeCommand.ExecuteAsync(null);
            Assert.Contains(manager.AllTerminalProfiles, p => p.Id == "Konsole-Imported");

            var reopened = new ProfileManagerService(settings, tempDir);
            await reopened.InitializeAsync();
            Assert.DoesNotContain(reopened.AllTerminalProfiles, p => p.Id == "Konsole-Imported");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // R3) ImportBundleCommand 同 ID 覆盖：显式与继承标签经 sink 即时更新；Apply 落盘、Cancel 回滚
    [Fact]
    public async Task ImportBundleCommand_SameIdOverwrite_RefreshesExplicitAndInheritingTabs()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            manager.AddOrUpdateCustomTerminalProfile(Profile("Custom-X", "#111111"));
            manager.NotifyDefaultProfileSelectionChanged("Custom-X");
            await manager.CommitAndSaveProfilesAsync();

            var vm = CreateMainViewModel(settings, manager);
            var inheritSink = new RecordingThemeSink();
            var explicitSink = new RecordingThemeSink();
            var inheritTab = CreateTab(manager.ResolveEffectiveTerminalProfile(null), null, inheritSink);
            var explicitTab = CreateTab(manager.ResolveEffectiveTerminalProfile("Custom-X"), "Custom-X", explicitSink);
            vm.Tabs.Add(inheritTab);
            vm.Tabs.Add(explicitTab);

            var settingsVm = new SettingsViewModel(settings, tempDir, identityRepo: null, profileManager: manager);
            var bundlePath = Path.Combine(tempDir, "overwrite.bundle.json");
            await WriteBundleAsync(bundlePath, new ProfileBundle
            {
                TerminalProfiles = new List<TerminalProfile> { Profile("Custom-X", "#999999") }
            });
            settingsVm.OpenBundleFileDialogAsync = () => Task.FromResult<string?>(bundlePath);

            // 导入（persist:false，父事务草稿）：同 ID 覆盖后继承与显式都即时刷新
            await settingsVm.ImportBundleCommand.ExecuteAsync(null);
            Assert.Equal("#999999", inheritTab.CurrentProfile.Foreground);
            Assert.Equal("#999999", explicitTab.CurrentProfile.Foreground);
            Assert.Equal("Custom-X", explicitSink.Applied[^1].Id);
            Assert.Equal("#999999", explicitSink.Applied[^1].Foreground);
            Assert.Equal("#999999", inheritSink.Applied[^1].Foreground);

            // 未 Apply 不得落盘
            var beforeApply = new ProfileManagerService(settings, tempDir);
            await beforeApply.InitializeAsync();
            Assert.Equal("#111111", beforeApply.ResolveEffectiveTerminalProfile("Custom-X").Foreground);

            // Apply 后落盘
            Assert.True(await settingsVm.ApplyChangesAsync());
            var afterApply = new ProfileManagerService(settings, tempDir);
            await afterApply.InitializeAsync();
            Assert.Equal("#999999", afterApply.ResolveEffectiveTerminalProfile("Custom-X").Foreground);

            // 再次导入新内容后 Cancel：恢复 Apply 后的已提交权威
            await WriteBundleAsync(bundlePath, new ProfileBundle
            {
                TerminalProfiles = new List<TerminalProfile> { Profile("Custom-X", "#222222") }
            });
            await settingsVm.ImportBundleCommand.ExecuteAsync(null);
            Assert.Equal("#222222", explicitTab.CurrentProfile.Foreground);

            await settingsVm.CancelCommand.ExecuteAsync(null);
            Assert.Equal("#999999", explicitTab.CurrentProfile.Foreground);
            Assert.Equal("#999999", inheritTab.CurrentProfile.Foreground);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // R3) ImportBundleCommand 改变有效默认：已开继承标签跟随；显式覆盖不跟随；新继承标签一致
    [Fact]
    public async Task ImportBundleCommand_ChangesEffectiveDefault_SyncsOldAndNewInheritingTabs()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            manager.AddOrUpdateCustomTerminalProfile(Profile("Custom-A", "#AAAAAA"));
            manager.NotifyDefaultProfileSelectionChanged("Custom-A");
            await manager.CommitAndSaveProfilesAsync();

            var vm = CreateMainViewModel(settings, manager);
            var oldInheritSink = new RecordingThemeSink();
            var explicitSink = new RecordingThemeSink();
            var oldInheritTab = CreateTab(manager.ResolveEffectiveTerminalProfile(null), null, oldInheritSink);
            var explicitTab = CreateTab(manager.ResolveEffectiveTerminalProfile("Custom-A"), "Custom-A", explicitSink);
            vm.Tabs.Add(oldInheritTab);
            vm.Tabs.Add(explicitTab);
            Assert.Equal("Custom-A", oldInheritTab.EffectiveProfileId);

            var settingsVm = new SettingsViewModel(settings, tempDir, identityRepo: null, profileManager: manager);
            var bundlePath = Path.Combine(tempDir, "default-switch.bundle.json");
            await WriteBundleAsync(bundlePath, new ProfileBundle
            {
                DefaultTerminalProfileId = "Custom-B",
                TerminalProfiles = new List<TerminalProfile> { Profile("Custom-B", "#BBBBBB") }
            });
            settingsVm.OpenBundleFileDialogAsync = () => Task.FromResult<string?>(bundlePath);

            await settingsVm.ImportBundleCommand.ExecuteAsync(null);

            Assert.Equal("Custom-B", manager.ResolveEffectiveTerminalProfile(null).Id);
            // 已开继承标签跟随新默认
            Assert.Equal("Custom-B", oldInheritTab.EffectiveProfileId);
            Assert.Equal("#BBBBBB", oldInheritSink.Applied[^1].Foreground);
            // 显式覆盖不跟随全局默认
            Assert.Equal("Custom-A", explicitTab.EffectiveProfileId);
            Assert.Equal("Custom-A", explicitSink.Applied[^1].Id);
            // 新继承标签与已开标签一致
            var newTab = CreateTab(manager.ResolveEffectiveTerminalProfile(null), null, new RecordingThemeSink());
            vm.Tabs.Add(newTab);
            Assert.Equal("Custom-B", newTab.EffectiveProfileId);
            Assert.Equal("#BBBBBB", newTab.CurrentProfile.Foreground);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // I) 字体草稿快照：取值真实、变化即通知
    [Fact]
    public void DraftFontSnapshot_ReflectsRealDraft_AndNotifies()
    {
        var page = new AppearanceSettingsPage();
        var notified = new List<string>();
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppearanceSettingsPage.DraftFontSnapshot))
            {
                notified.Add(e.PropertyName);
            }
        };

        page.FontFamily = "Fira Code";
        page.FontSize = 16.0;
        page.CursorBlink = false;
        page.SetTerminalFallbackFonts("Noto Sans Mono, monospace");

        var snapshot = page.DraftFontSnapshot;
        Assert.Equal("Fira Code", snapshot.FontFamily);
        Assert.Equal(16.0, snapshot.FontSize);
        Assert.False(snapshot.CursorBlink);
        Assert.Equal(new[] { "Noto Sans Mono", "monospace" }, snapshot.FallbackFonts);
        Assert.Contains(nameof(AppearanceSettingsPage.DraftFontSnapshot), notified);
    }

    // K) 自定义方案深拷贝保留字体字段（配色编辑不丢字段、不改字体）
    [Fact]
    public async Task AddOrUpdate_CustomProfileCopy_PreservesFontFields()
    {
        var settings = new InMemorySettingsService();
        string tempDir = NewTempDir();
        try
        {
            var manager = new ProfileManagerService(settings, tempDir);
            await manager.InitializeAsync();

            var source = Profile("Custom-Font");
            source.FontFamily = "Cascadia Mono, Consolas, monospace";
            source.FontSize = 15.5;
            source.FontWeight = "SemiBold";
            source.IsItalic = true;

            manager.AddOrUpdateCustomTerminalProfile(source.DeepCopy());

            var stored = manager.ResolveEffectiveTerminalProfile("Custom-Font");
            Assert.Equal("Cascadia Mono, Consolas, monospace", stored.FontFamily);
            Assert.Equal(15.5, stored.FontSize);
            Assert.Equal("SemiBold", stored.FontWeight);
            Assert.True(stored.IsItalic);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
