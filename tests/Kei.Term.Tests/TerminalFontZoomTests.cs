using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.Terminals;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Ssh.Abstractions;
using Xunit;

namespace Kei.Term.Tests;

// Ctrl+滚轮缩放：纯函数步进/累积 + MainViewModel 全局字号写回与标签快照刷新。
public class TerminalFontZoomTests
{
    private sealed class InMemorySettingsService : ISettingsService
    {
        public InMemorySettingsService(AppSettings? initial = null) => Current = initial ?? new AppSettings();

        public AppSettings Current { get; private set; }

        public int SaveCount { get; private set; }

        public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);

        public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
        {
            SaveCount++;
            // 与 JsonSettingsService 一致：先更新内存再落盘
            Current = settings;
            return Task.CompletedTask;
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

    private static ResolvedSessionConfig Config(string name) => new(
        SessionId: Guid.NewGuid(),
        SessionName: name,
        Host: "host.example",
        Port: 22,
        Username: "user",
        IdentityId: null,
        TerminalType: "xterm-256color",
        StartupScript: null,
        JumpHostSessionId: null,
        EnvironmentVariables: new Dictionary<string, string>());

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
        var host = (IConnectionHost)vm;
        host.OpenTab(Config("A"));
        host.OpenTab(Config("B"));

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
        var host = (IConnectionHost)vm;
        host.OpenTab(Config("A"));

        vm.ApplyGlobalFontZoom(1);

        Assert.Equal(36.0, settings.Current.FontSize);
        Assert.Equal(36.0, vm.Tabs[0].CurrentFontSnapshot.FontSize);
        Assert.Equal(0, settings.SaveCount);
    }
}
