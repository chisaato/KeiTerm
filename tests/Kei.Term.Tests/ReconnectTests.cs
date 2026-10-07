using System;
using System.Collections.Generic;
using System.IO;
using Renci.SshNet.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;
using Kei.Term.Ssh.Abstractions;
using Xunit;

namespace Kei.Term.Tests;

// 意外断开后的指数退避。不重连的几类原因必须被策略拒绝，重连必须复用原标签。
public class ReconnectTests
{
    [Fact]
    public void Schedule_IsExactlySixDelays_AndSeventhIsNotScheduled()
    {
        Assert.Equal([1, 2, 4, 8, 16, 30], Enumerable.Range(1, 6).Select(ReconnectSchedule.DelaySeconds));
        Assert.Null(ReconnectSchedule.DelaySeconds(7));
        Assert.Null(ReconnectSchedule.DelaySeconds(0));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Policy_DoesNotReconnect_ForUserAuthHostKeyOrInteractive(
        bool userDisconnect,
        bool authCancelled,
        bool hostKeyRejected,
        bool interactiveRequired)
    {
        var decision = ReconnectPolicy.Decide(new ReconnectFacts(
            Enabled: true,
            UserDisconnected: userDisconnect,
            AuthCancelled: authCancelled,
            HostKeyRejected: hostKeyRejected,
            InteractiveRequired: interactiveRequired,
            NextAttempt: 1));

        Assert.False(decision.ShouldReconnect);
        Assert.Null(decision.DelaySeconds);
    }

    [Fact]
    public void Policy_UnexpectedDisconnect_WhenEnabled_SchedulesFirstDelay()
    {
        Assert.False(new AppSettings().AutoReconnectOnDisconnect);

        var off = ReconnectPolicy.Decide(new ReconnectFacts(false, false, false, false, false, 1));
        Assert.False(off.ShouldReconnect);

        var on = ReconnectPolicy.Decide(new ReconnectFacts(true, false, false, false, false, 1));
        Assert.True(on.ShouldReconnect);
        Assert.Equal(1, on.DelaySeconds);
        Assert.Equal(1, on.Attempt);

        var exhausted = ReconnectPolicy.Decide(new ReconnectFacts(true, false, false, false, false, 7));
        Assert.False(exhausted.ShouldReconnect);
    }

    [Fact]
    public void Messages_MatchSpecWording()
    {
        Assert.Equal("连接已断开，1 秒后重连（第 1/6 次）", ReconnectMessages.Waiting(1, 1));
        Assert.Equal("连接已断开，30 秒后重连（第 6/6 次）", ReconnectMessages.Waiting(30, 6));
        Assert.Equal("已重新连接", ReconnectMessages.Reconnected);
    }

    [Fact]
    public async Task Orchestrator_UnexpectedDisconnect_ReconnectsSameTarget_NotANewTab()
    {
        var requested = new List<TimeSpan>();
        var orchestrator = Create(delay: (span, _) =>
        {
            requested.Add(span);
            return Task.CompletedTask;
        }, enabled: true);
        var host = new ReconnectHost();
        var target = new ReconnectTarget();
        var request = new ConnectionRequest(Config(), UseIdentity: false, Password("p1"), target);

        await orchestrator.ScheduleReconnectAsync(request, host, Unexpected(1), CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(1)], requested);
        Assert.Empty(host.Opened);
        Assert.Equal(1, target.Reconnects);
        Assert.Equal(1, Factory.Sessions);
        Assert.Contains(ReconnectMessages.Waiting(1, 1), target.LocalLines);
        Assert.Contains(ReconnectMessages.Reconnected, target.LocalLines);
    }

    [Fact]
    public async Task Orchestrator_CancelledWait_DoesNotConnect()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = Create(delay: async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, enabled: true);
        var host = new ReconnectHost();
        var target = new ReconnectTarget();
        using var cts = new CancellationTokenSource();
        var pending = orchestrator.ScheduleReconnectAsync(
            new ConnectionRequest(Config(), UseIdentity: false, Password("p1"), target),
            host,
            Unexpected(1),
            cts.Token);

        await started.Task;
        cts.Cancel();
        await pending;

        Assert.Equal(0, Factory.Sessions);
        Assert.Empty(host.Opened);
    }

    [Fact]
    public async Task Orchestrator_SeventhAttempt_IsNotScheduled()
    {
        var delays = 0;
        var orchestrator = Create(delay: (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        }, enabled: true);

        await orchestrator.ScheduleReconnectAsync(
            new ConnectionRequest(Config(), UseIdentity: false, Password("p1"), new ReconnectTarget()),
            new ReconnectHost(),
            Unexpected(7),
            CancellationToken.None);

        Assert.Equal(0, delays);
        Assert.Equal(0, Factory.Sessions);
    }

    [Fact]
    public async Task TransientDialFailures_RetryUntilTheSameTargetConnects()
    {
        List<TimeSpan> delays = [];
        ConnectionOrchestrator orchestrator = Create((delay, _) => { delays.Add(delay); return Task.CompletedTask; }, true);
        Factory.Outcomes.Enqueue(new IOException("offline"));
        Factory.Outcomes.Enqueue(new IOException("offline"));
        ReconnectTarget target = new();
        ReconnectHost host = new();

        await orchestrator.ScheduleReconnectAsync(new ConnectionRequest(Config(), false, Password("p1"), target), host, Unexpected(1), CancellationToken.None);

        Assert.Equal([1, 2, 4], delays.Select(delay => delay.TotalSeconds));
        Assert.Equal(3, Factory.Sessions);
        Assert.Equal(3, target.Reconnects);
        Assert.Empty(host.Opened);
        Assert.Contains(ReconnectMessages.Reconnected, target.LocalLines);
    }

    [Fact]
    public async Task PersistentDialFailure_StopsAfterSixActualAttempts()
    {
        List<TimeSpan> delays = [];
        ConnectionOrchestrator orchestrator = Create((delay, _) => { delays.Add(delay); return Task.CompletedTask; }, true);
        for (int i = 0; i < 8; i++) Factory.Outcomes.Enqueue(new IOException("offline"));
        ReconnectTarget target = new();

        await orchestrator.ScheduleReconnectAsync(new ConnectionRequest(Config(), false, Password("p1"), target), new ReconnectHost(), Unexpected(1), CancellationToken.None);

        Assert.Equal([1, 2, 4, 8, 16, 30], delays.Select(delay => delay.TotalSeconds));
        Assert.Equal(6, Factory.Sessions);
        Assert.DoesNotContain(ReconnectMessages.Reconnected, target.LocalLines);
    }

    [Fact]
    public async Task AuthenticationFailure_DoesNotPromptOrRetryAutomatically()
    {
        ScriptedInteraction interaction = new();
        ConnectionOrchestrator orchestrator = Create((_, _) => Task.CompletedTask, true, interaction);
        Factory.Outcomes.Enqueue(new SshAuthenticationException("denied"));
        ReconnectTarget target = new();

        await orchestrator.ScheduleReconnectAsync(new ConnectionRequest(Config(), false, Password("p1"), target), new ReconnectHost(), Unexpected(1), CancellationToken.None);

        Assert.Equal(1, Factory.Sessions);
        Assert.Empty(interaction.AuthPromptUsernames);
        Assert.DoesNotContain(ReconnectMessages.Reconnected, target.LocalLines);
    }

    [Fact]
    public async Task CancelDuringDial_CancelsTheConnectionAndStopsFurtherAttempts()
    {
        ConnectionOrchestrator orchestrator = Create((_, _) => Task.CompletedTask, true);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Factory.OnConnect = async ct => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using CancellationTokenSource cancellation = new();
        ReconnectTarget target = new();
        Task reconnect = orchestrator.ScheduleReconnectAsync(new ConnectionRequest(Config(), false, Password("p1"), target), new ReconnectHost(), Unexpected(1), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await reconnect.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, Factory.Sessions);
        Assert.DoesNotContain(ReconnectMessages.Reconnected, target.LocalLines);
    }

    private static ReconnectFacts Unexpected(int attempt) => new(true, false, false, false, false, attempt);

    private static RecordingFactory Factory { get; set; } = new();

    private static ConnectionOrchestrator Create(Func<TimeSpan, CancellationToken, Task> delay, bool enabled, ScriptedInteraction? interaction = null)
    {
        Factory = new RecordingFactory();
        var ui = interaction ?? new ScriptedInteraction();
        var settings = new FixedSettingsService(new AppSettings { AutoReconnectOnDisconnect = enabled, PreferSystemAgent = false });
        var vault = new VaultSessionService(new PlainVault(), new PlainVault(), settings, () => ui);
        var collector = new AuthMaterialCollector(new EmptyIdentities(), settings, vault, () => ui);
        return new ConnectionOrchestrator(
            Factory,
            collector,
            null,
            settings,
            () => ui,
            action => action(),
            delay: delay);
    }

    private static ResolvedSessionConfig Config() => new(
        Guid.NewGuid(), "target", "target.example", 22, "ops", null, "xterm-256color", null, null,
        new Dictionary<string, string>());

    private static MaterializedAuthMethod Password(string value)
        => new(AuthMaterialKind.Password, new SecretPayload { Password = value });

    private sealed class RecordingFactory : ISshSessionFactory
    {
        public int Sessions { get; private set; }
        public Queue<Exception?> Outcomes { get; } = new();
        public Func<CancellationToken, Task>? OnConnect { get; set; }

        public Task<ISshSession> CreateSessionAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
        {
            Sessions++;
            return Task.FromResult<ISshSession>(new IdleSession(Outcomes.Count > 0 ? Outcomes.Dequeue() : null, OnConnect));
        }

        public Task<IRemoteFileSystem> CreateFileSystemAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            ISshSession? activeSession = null,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
            => Task.FromResult<IRemoteFileSystem>(null!);

    }

    private sealed class IdleSession(Exception? failure, Func<CancellationToken, Task>? onConnect) : ISshSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => true;
        public object? UnderlyingClient => null;
        public event Action<byte[]>? OutputReceived { add { } remove { } }
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default)
            => failure != null ? Task.FromException(failure) : onConnect?.Invoke(ct) ?? Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReconnectHost : IConnectionHost
    {
        public List<ResolvedSessionConfig> Opened { get; } = [];

        public IConnectionTarget OpenTab(ResolvedSessionConfig config)
        {
            Opened.Add(config);
            return new ReconnectTarget();
        }

        public SessionNode? FindSession(Guid id) => null;
    }

    private sealed class ReconnectTarget : IConnectionTarget
    {
        public bool IsDisposed { get; set; }
        public int Reconnects { get; private set; }
        public List<string> LocalLines { get; } = [];
        public void AttachSession(ITerminalSession session) { }
        public Task DetachSessionAsync() => Task.CompletedTask;
        public void MarkConnected() { }
        public void ReportError(string message) { }
        public void WriteLocalStatus(string message) => LocalLines.Add(message);
        public Task AttachFileSystemAsync(IRemoteFileSystem fileSystem) => Task.CompletedTask;
        public Task ResetForReconnectAsync(ResolvedSessionConfig config)
        {
            Reconnects++;
            return Task.CompletedTask;
        }
    }

    private sealed class PlainVault : IVaultManager, IVaultSecretStore
    {
        public bool IsUnlocked => true;
        public bool IsPlainMode => true;
        public Task SetMasterPasswordAsync(string masterPassword, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryAutoUnlockAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task UnlockAsync(string masterPassword, bool rememberOnThisDevice, CancellationToken ct = default) => Task.CompletedTask;
        public void Lock() { }
        public Task<Dictionary<string, SecretPayload>> GetSecretsAsync(Guid identityId, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, SecretPayload>());
        public Task SaveSecretsAsync(Guid identityId, Dictionary<string, SecretPayload> secrets, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteSecretsAsync(Guid identityId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyIdentities : IIdentityRepository
    {
        public Task<IReadOnlyList<Identity>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Identity>>([]);
        public Task<Identity?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Identity?>(null);
        public Task SaveAsync(Identity identity, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
