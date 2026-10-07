using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Ssh.Abstractions;
using Renci.SshNet.Common;
using Xunit;

namespace Kei.Term.Tests;

// 连接编排：认证失败回弹重试、取消、主机密钥握手外确认、跳板物化、文件通道材料、KI 桥
public class ConnectionOrchestratorTests
{
    private readonly ScriptedInteraction _ui = new();
    private readonly FakeSshFactory _factory = new();
    private readonly FakeHost _host = new();
    private readonly FixedSettingsService _settings = new(new AppSettings { PreferSystemAgent = false });

    private ConnectionOrchestrator Create(HostKeyTrustService? trust = null)
    {
        var vault = new VaultSessionService(new PlainVault(), new PlainVault(), _settings, () => _ui);
        var collector = new AuthMaterialCollector(new InMemoryIdentities(), _settings, vault, () => _ui);
        return new ConnectionOrchestrator(_factory, collector, trust, _settings, () => _ui, action => action());
    }

    private static ResolvedSessionConfig Config(string host = "target.example", Guid? jump = null) => new(
        Guid.NewGuid(), host, host, 22, "ops", null, "xterm-256color", null, jump, new Dictionary<string, string>());

    private static MaterializedAuthMethod Password(string value)
        => new(AuthMaterialKind.Password, new SecretPayload { Password = value });

    private static AuthPromptResult PasswordPrompt(string value)
        => new(AuthPromptMethod.Password, "ops", value, null, null, null);

    private static ConnectionRequest WithPassword(string value) => new(Config(), UseIdentity: false, Password(value));

    [Fact]
    public async Task FirstAttemptSucceeds_TabConnected_FileChannelUsesSameMaterials()
    {
        await Create().ConnectAsync(WithPassword("p1"), _host);

        FakeTarget tab = Assert.Single(_host.Tabs);
        Assert.True(tab.Connected);
        Assert.NotNull(tab.AttachedSession);
        Assert.Single(_factory.SessionCalls);
        var fileCall = await _factory.WaitForFileSystemAsync();
        Assert.Equal("p1", Assert.Single(fileCall.Materials).Secret!.Password);
    }

    [Fact]
    public async Task AuthFailure_RetryReplacesPassword_AndFileChannelUsesRetriedMaterials()
    {
        _factory.Outcomes.Enqueue(new SshAuthenticationException("Permission denied (password)."));
        _ui.AuthResults.Enqueue(PasswordPrompt("p2"));

        await Create().ConnectAsync(WithPassword("p1"), _host);

        Assert.Equal(2, _factory.SessionCalls.Count);
        // 第二次只带新密码，旧的失败密码被替换而非追加
        Assert.Equal(["p2"], _factory.SessionCalls[1].Materials.Select(m => m.Secret!.Password));
        Assert.True(_host.Tabs[0].Connected);
        var fileCall = await _factory.WaitForFileSystemAsync();
        Assert.Equal("p2", Assert.Single(fileCall.Materials).Secret!.Password);
    }

    [Fact]
    public async Task AuthFailure_GivesUpAfterMaxRetries()
    {
        for (int i = 0; i <= ConnectionOrchestrator.MaxAuthRetries; i++)
        {
            _factory.Outcomes.Enqueue(new SshAuthenticationException("denied"));
            _ui.AuthResults.Enqueue(PasswordPrompt($"try{i}"));
        }

        await Create().ConnectAsync(WithPassword("p1"), _host);

        Assert.Equal(ConnectionOrchestrator.MaxAuthRetries, _ui.AuthPromptUsernames.Count);
        Assert.Equal(ConnectionOrchestrator.MaxAuthRetries + 1, _factory.SessionCalls.Count);
        Assert.Equal("denied", Assert.Single(_host.Tabs).Error);
        Assert.False(_host.Tabs[0].Connected);
    }

    [Fact]
    public async Task AuthRetryCancelled_ReportsOriginalError()
    {
        _factory.Outcomes.Enqueue(new SshAuthenticationException("denied"));

        await Create().ConnectAsync(WithPassword("p1"), _host);

        Assert.Single(_factory.SessionCalls);
        Assert.Equal("denied", _host.Tabs[0].Error);
    }

    [Fact]
    public async Task NonAuthenticationFailure_ReportsWithoutPrompting()
    {
        _factory.Outcomes.Enqueue(new IOException("Connection refused"));

        await Create().ConnectAsync(WithPassword("p1"), _host);

        Assert.Empty(_ui.AuthPromptUsernames);
        Assert.Equal("Connection refused", _host.Tabs[0].Error);
    }

    [Fact]
    public async Task NoMaterialsOrUsername_AndFallbackCancelled_OpensNoTab()
    {
        await Create().ConnectAsync(new ConnectionRequest(Config() with { Username = "" }, UseIdentity: false), _host);

        Assert.Single(_ui.AuthPromptUsernames);
        Assert.Empty(_host.Tabs);
        Assert.Empty(_factory.SessionCalls);
    }

    [Fact]
    public async Task UsernameWithoutMaterials_TriesEmptyPasswordBeforePrompting()
    {
        await Create().ConnectAsync(new ConnectionRequest(Config(), UseIdentity: false), _host);

        Assert.True(Assert.Single(_host.Tabs).Connected);
        Assert.Empty(_ui.AuthPromptUsernames);
        Assert.Equal(string.Empty, Assert.Single(Assert.Single(_factory.SessionCalls).Materials).Secret!.Password);
    }

    [Fact]
    public async Task AuthenticationInteraction_RemainsWithItsTargetAfterAnotherConnection()
    {
        ConnectionOrchestrator orchestrator = Create();
        _factory.Outcomes.Enqueue(new SshAuthenticationException("denied"));
        _ui.AuthResults.Enqueue(PasswordPrompt("retry"));
        await orchestrator.ConnectAsync(WithPassword("first"), _host);
        FakeTarget interactive = Assert.Single(_host.Tabs);
        await orchestrator.ConnectAsync(WithPassword("second"), _host);

        Assert.True(interactive.RequiresInteractiveAuthentication);
        Assert.False(_host.Tabs[1].RequiresInteractiveAuthentication);
    }

    [Fact]
    public async Task ConcurrentKeyboardInteractive_DoesNotMarkTheOtherConnectionInteractive()
    {
        ConnectionOrchestrator orchestrator = Create();
        TaskCompletionSource prompted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.OnConnect = async (config, options) =>
        {
            if (config.Host != "interactive.example") return;
            await options.InteractivePrompt!("OTP");
            prompted.TrySetResult();
            await resume.Task;
        };
        Task first = orchestrator.ConnectAsync(new ConnectionRequest(Config("interactive.example"), false, Password("p1")), _host);
        await prompted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await orchestrator.ConnectAsync(WithPassword("p2"), _host);
        resume.TrySetResult();
        await first;

        Assert.True(_host.Tabs[0].RequiresInteractiveAuthentication);
        Assert.False(_host.Tabs[1].RequiresInteractiveAuthentication);
    }

    [Fact]
    public async Task Reconnect_ReusesGivenTab_AndDoesNotOpenAnother()
    {
        var existing = new FakeTarget();
        ResolvedSessionConfig config = Config();

        await Create().ConnectAsync(new ConnectionRequest(config, UseIdentity: false, Password("p1"), ReuseTarget: existing), _host);

        Assert.Empty(_host.Tabs);
        Assert.Equal(config, Assert.Single(existing.Resets));
        Assert.True(existing.Connected);
        Assert.NotNull(existing.AttachedSession);
    }

    [Fact]
    public async Task Reconnect_AuthCancelled_LeavesExistingTabUntouched()
    {
        var existing = new FakeTarget();

        // 无预置材料、无用户名且兜底认证框被取消：不得复位旧标签（保留断开前的状态与错误信息）
        await Create().ConnectAsync(new ConnectionRequest(Config() with { Username = "" }, UseIdentity: false, ReuseTarget: existing), _host);

        Assert.Empty(existing.Resets);
        Assert.Empty(_factory.SessionCalls);
    }

    [Theory]
    [InlineData(HostKeyDecision.AcceptOnce, true)]
    [InlineData(HostKeyDecision.Reject, false)]
    public async Task HostKeyNeedingConfirmation_PromptsOutsideHandshake_ThenReconnectsOrStops(HostKeyDecision decision, bool connected)
    {
        var trust = new HostKeyTrustService(new InMemoryKnownHosts(), () => HostKeyPolicy.Ask, (ev, _) => _ui.PromptHostKeyAsync(ev));
        _factory.Outcomes.Enqueue(UnknownHostKey());
        _ui.HostKeyDecisions.Enqueue(decision);

        await Create(trust).ConnectAsync(WithPassword("p1"), _host);

        Assert.Single(_ui.HostKeyPrompts);
        Assert.Equal(connected, _host.Tabs[0].Connected);
        Assert.Equal(connected ? 2 : 1, _factory.SessionCalls.Count);
        // 主机密钥问题不应回弹密码框
        Assert.Empty(_ui.AuthPromptUsernames);
    }

    [Fact]
    public async Task HostKeyConfirmation_IsCapped()
    {
        var trust = new HostKeyTrustService(new InMemoryKnownHosts(), () => HostKeyPolicy.Ask, (ev, _) => _ui.PromptHostKeyAsync(ev));
        for (int i = 0; i <= ConnectionOrchestrator.MaxHostKeyConfirmations; i++)
        {
            // 每次都出示不同的"未知"密钥，确认也无法让下一次命中信任
            _factory.Outcomes.Enqueue(UnknownHostKey());
            _ui.HostKeyDecisions.Enqueue(HostKeyDecision.AcceptOnce);
        }

        await Create(trust).ConnectAsync(WithPassword("p1"), _host);

        Assert.Equal(ConnectionOrchestrator.MaxHostKeyConfirmations, _ui.HostKeyPrompts.Count);
        Assert.NotNull(_host.Tabs[0].Error);
    }

    [Fact]
    public async Task JumpHost_IsMaterializedAndPassedToFactory()
    {
        _settings.Current.PreferSystemAgent = true;
        var bastion = new SessionNode { Name = "bastion", Host = "bastion.example", Username = "jump" };
        _host.Sessions[bastion.Id] = bastion;

        await Create().ConnectAsync(new ConnectionRequest(Config(jump: bastion.Id), UseIdentity: false, Password("p1")), _host);

        SshHop hop = Assert.Single(_factory.SessionCalls[0].Options.JumpHosts);
        Assert.Equal("bastion.example", hop.Config.Host);
        Assert.Equal("jump", hop.Config.Username);
        // 无身份 + PreferSystemAgent → 跳板走 Agent
        Assert.Equal(AuthMaterialKind.Agent, Assert.Single(hop.Methods).Kind);
    }

    [Fact]
    public async Task JumpHostAuthCancelled_NotifiesAndOpensNoTab()
    {
        var bastion = new SessionNode { Name = "bastion", Host = "bastion.example" };
        _host.Sessions[bastion.Id] = bastion;

        await Create().ConnectAsync(new ConnectionRequest(Config(jump: bastion.Id), UseIdentity: false, Password("p1")), _host);

        Assert.Empty(_host.Tabs);
        Assert.Contains(_ui.Notifications, n => n.Message.Contains("bastion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeyboardInteractivePrompt_IsRoutedThroughInteractionService()
    {
        _ui.KeyboardInteractiveAnswers.Enqueue("123456");

        await Create().ConnectAsync(WithPassword("p1"), _host);
        Func<string, Task<string?>> ki = _factory.SessionCalls[0].Options.InteractivePrompt!;

        Assert.Equal("123456", await ki("Verification code: "));
        Assert.Equal(["Verification code: "], _ui.KeyboardInteractivePrompts);
    }

    [Fact]
    public async Task TabClosedWhileCreatingSession_DisposesNewSession()
    {
        // 用户在后台建会话期间关掉标签
        _factory.OnCreate = () => _host.Tabs[0].IsDisposed = true;

        await Create().ConnectAsync(WithPassword("p1"), _host);

        Assert.Null(_host.Tabs[0].AttachedSession);
        Assert.True(_factory.CreatedSessions.Single().Disposed);
    }

    private static HostKeyRejectedException UnknownHostKey()
    {
        var presented = new PresentedHostKey("target.example", 22, "ssh-ed25519", Guid.NewGuid().ToByteArray());
        var evaluation = new HostKeyEvaluation(HostKeyVerdict.Unknown, presented, []);
        return new HostKeyRejectedException(new HostKeyCheckOutcome(false, evaluation, null));
    }

    // === 测试替身 ===

    private sealed record SessionCall(ResolvedSessionConfig Config, IReadOnlyList<MaterializedAuthMethod> Materials, SshConnectOptions Options);

    private sealed class FakeSshFactory : ISshSessionFactory
    {
        private readonly TaskCompletionSource<SessionCall> _fileSystem = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 每次建连的结果：null = 成功；耗尽后一律成功
        public Queue<Exception?> Outcomes { get; } = new();
        public List<SessionCall> SessionCalls { get; } = [];
        public List<FakeSession> CreatedSessions { get; } = [];
        public Action? OnCreate { get; set; }
        public Func<ResolvedSessionConfig, SshConnectOptions, Task>? OnConnect { get; set; }

        public Task<ISshSession> CreateSessionAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
        {
            SessionCalls.Add(new SessionCall(config, methods.ToList(), options!));
            OnCreate?.Invoke();
            var session = new FakeSession(Outcomes.Count > 0 ? Outcomes.Dequeue() : null, () => OnConnect?.Invoke(config, options!) ?? Task.CompletedTask);
            CreatedSessions.Add(session);
            return Task.FromResult<ISshSession>(session);
        }

        public Task<IRemoteFileSystem> CreateFileSystemAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            ISshSession? activeSession = null,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
        {
            _fileSystem.TrySetResult(new SessionCall(config, methods, options!));
            return Task.FromResult<IRemoteFileSystem>(null!);
        }

        // 文件通道在后台任务中创建
        public async Task<SessionCall> WaitForFileSystemAsync()
            => await _fileSystem.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakeSession(Exception? connectFailure, Func<Task>? onConnect = null) : ISshSession
    {
        public bool Disposed { get; private set; }
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => connectFailure == null && !Disposed;
        public object? UnderlyingClient => null;

        public event Action<byte[]>? OutputReceived { add { } remove { } }
        public event Action<Exception?>? Disconnected { add { } remove { } }

        public Task ConnectAsync(CancellationToken ct = default)
            => connectFailure == null ? onConnect?.Invoke() ?? Task.CompletedTask : Task.FromException(connectFailure);

        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;

        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTarget : IConnectionTarget
    {
        public bool IsDisposed { get; set; }
        public bool RequiresInteractiveAuthentication { get; private set; }
        public void SetRequiresInteractiveAuthentication(bool value) => RequiresInteractiveAuthentication = value;
        public ITerminalSession? AttachedSession { get; private set; }
        public bool Connected { get; private set; }
        public string? Error { get; private set; }

        public void AttachSession(ITerminalSession session) => AttachedSession = session;

        public Task DetachSessionAsync()
        {
            AttachedSession = null;
            return Task.CompletedTask;
        }

        public void MarkConnected() => Connected = true;

        public void ReportError(string message) => Error = message;

        public Task AttachFileSystemAsync(IRemoteFileSystem fileSystem) => Task.CompletedTask;

        public List<ResolvedSessionConfig> Resets { get; } = [];

        public Task ResetForReconnectAsync(ResolvedSessionConfig config)
        {
            Resets.Add(config);
            AttachedSession = null;
            Connected = false;
            Error = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHost : IConnectionHost
    {
        public List<FakeTarget> Tabs { get; } = [];
        public Dictionary<Guid, SessionNode> Sessions { get; } = [];
        public IConnectionTarget OpenTab(ResolvedSessionConfig config)
        {
            var tab = new FakeTarget();
            Tabs.Add(tab);
            return tab;
        }

        public SessionNode? FindSession(Guid id) => Sessions.GetValueOrDefault(id);
    }

    // 明文模式 Vault：恒解锁，材料存内存
    private sealed class PlainVault : IVaultManager, IVaultSecretStore
    {
        private readonly Dictionary<Guid, Dictionary<string, SecretPayload>> _secrets = [];

        public bool IsUnlocked => true;
        public bool IsPlainMode => true;

        public Task SetMasterPasswordAsync(string masterPassword, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryAutoUnlockAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task UnlockAsync(string masterPassword, bool rememberOnThisDevice, CancellationToken ct = default) => Task.CompletedTask;
        public void Lock() { }

        public Task<Dictionary<string, SecretPayload>> GetSecretsAsync(Guid identityId, CancellationToken ct = default)
            => Task.FromResult(_secrets.TryGetValue(identityId, out var s) ? new Dictionary<string, SecretPayload>(s) : new());

        public Task SaveSecretsAsync(Guid identityId, Dictionary<string, SecretPayload> secrets, CancellationToken ct = default)
        {
            _secrets[identityId] = new Dictionary<string, SecretPayload>(secrets);
            return Task.CompletedTask;
        }

        public Task DeleteSecretsAsync(Guid identityId, CancellationToken ct = default)
        {
            _secrets.Remove(identityId);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryIdentities : IIdentityRepository
    {
        private readonly Dictionary<Guid, Identity> _items = [];

        public Task<IReadOnlyList<Identity>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Identity>>(_items.Values.ToList());

        public Task<Identity?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault(id));

        public Task SaveAsync(Identity identity, CancellationToken ct = default)
        {
            _items[identity.Id] = identity;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            _items.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryKnownHosts : IKnownHostRepository
    {
        private readonly List<KnownHostEntry> _entries = [];

        public Task<IReadOnlyList<KnownHostEntry>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KnownHostEntry>>(_entries.ToList());

        public Task<IReadOnlyList<KnownHostEntry>> GetCandidatesAsync(string host, int port, CancellationToken ct = default)
            => GetAllAsync(ct);

        public Task SaveAsync(KnownHostEntry entry, CancellationToken ct = default)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task TouchAsync(Guid id, DateTime seenAtUtc, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            _entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }
    }
}
