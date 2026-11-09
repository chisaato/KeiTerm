using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Xunit;
using System.Threading.Tasks;
using Kei.Term.App.Services.Connection;
using System;
using System.Threading;
using Kei.Term.App.Services;
using Kei.Term.App.Terminals;

namespace Kei.Term.Tests;

public class ProtocolRoutingTests
{
    [Fact]
    public void SerialConfigurationPreservesImmutableBaseOptionsThroughCloneAndResolution()
    {
        var options = new SerialOptions { PortName = "", BaudRate = 115200, FlowControl = SerialFlowControl.RtsCts, Parity = SerialParity.Even };
        var node = new SessionNode { Protocol = SessionProtocols.Serial, Overrides = new SessionOverrides { Serial = options } };
        var clone = node.Overrides.Clone();
        var config = SessionConfigBuilder.Build(node, new AppSettings());
        Assert.Equal(options, config.Serial);
        Assert.Equal(options, clone.Serial);
        node.Overrides.Serial = options with { BaudRate = 9600 };
        Assert.Equal(115200, config.Serial!.BaudRate);
        Assert.Equal(115200, clone.Serial!.BaudRate);
        Assert.Equal(SessionProtocols.Serial, config.Protocol);
        Assert.Null(config.NetworkPort);
    }

    [Theory]
    [InlineData(SessionProtocols.Ssh, null, 2200)]
    [InlineData(SessionProtocols.Telnet, null, 23)]
    [InlineData(SessionProtocols.Telnet, 2323, 2323)]
    [InlineData(SessionProtocols.Serial, 115200, 0)]
    [InlineData("future", null, 0)]
    public void ConfigurationPreservesProtocolAndDoesNotBorrowSshPort(string protocol, int? port, int expected)
    {
        var config = SessionConfigBuilder.Build(new SessionNode { Protocol = protocol, Port = port }, new AppSettings { DefaultPort = 2200 });
        Assert.Equal(expected, config.Port);
        Assert.Equal(protocol, config.Protocol);
        if (protocol == SessionProtocols.Serial) Assert.Null(config.NetworkPort);
    }
}

public partial class ConnectionOrchestratorTests
{
    [Fact]
    public Task ProtocolRouting_DisconnectCancelsRealBindingConfirmation() => HeadlessAvalonia.RunAsync(async () =>
    {
        var owner = new Kei.Term.App.Views.MainWindow(deferStartupLayout: true);
        owner.Show();
        var interaction = new Kei.Term.App.Views.MainWindowInteractionService(owner, null!, null!, null!, null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var vault = new VaultSessionService(new PlainVault(), new PlainVault(), _settings, () => _ui);
        var collector = new AuthMaterialCollector(new InMemoryIdentities(), _settings, vault, () => _ui);
        var sessions = new ConfirmingRoutingFactory(interaction);
        var orchestrator = new ConnectionOrchestrator(_factory, collector, null, _settings, () => _ui, action => action(), protocolSessions: sessions);
        var tab = new Kei.Term.App.ViewModels.TerminalTabViewModel("confirm", "monospace", 14);
        var target = new TerminalTabConnectionTarget(tab, _settings, null, null);
        try
        {
            var connecting = orchestrator.ConnectAsync(new ConnectionRequest(Config() with { Protocol = SessionProtocols.Serial }, false, ReuseTarget: target), _host);
            for (int i = 0; i < 200 && !System.Linq.Enumerable.Any(owner.OwnedWindows); i++) await Task.Delay(10);
            var dialog = Assert.Single(owner.OwnedWindows);
            await tab.DisconnectAsync();
            await connecting.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(dialog.IsVisible);
            Assert.Equal(Kei.Term.App.ViewModels.ConnectionState.Disconnected, tab.State);
        }
        finally
        {
            foreach (var child in System.Linq.Enumerable.ToArray(owner.OwnedWindows)) child.Close();
            owner.Close();
            await tab.DisposeAsync();
        }
    });

    private sealed class ConfirmingRoutingFactory(IInteractionService interaction) : IProtocolSessionFactory
    {
        public bool Supports(string protocol, TransportEngine engine) => true;
        public async Task<ITerminalSession> CreateSessionAsync(ResolvedSessionConfig config, TransportEngine engine, CancellationToken ct = default)
        {
            if (!await interaction.PromptConfirmSerialBindingAsync(config.SessionName, "/dev/test", ct))
                throw new OperationCanceledException(ct);
            return new RoutingSession();
        }
    }
    [Fact]
    public Task ProtocolRouting_DisconnectDuringConnectCannotMarkOrReportOverNewAttempt() => HeadlessAvalonia.RunAsync(async () =>
    {
        var vault = new VaultSessionService(new PlainVault(), new PlainVault(), _settings, () => _ui);
        var collector = new AuthMaterialCollector(new InMemoryIdentities(), _settings, vault, () => _ui);
        var old = new DelayedConnectingSession();
        var sessions = new SingleDelayedConnectFactory(old);
        var orchestrator = new ConnectionOrchestrator(_factory, collector, null, _settings, () => _ui, action => action(), protocolSessions: sessions);
        var tab = new Kei.Term.App.ViewModels.TerminalTabViewModel("connect", "monospace", 14);
        var target = new TerminalTabConnectionTarget(tab, _settings, null, null);
        var config = Config() with { Protocol = SessionProtocols.Telnet };
        var first = orchestrator.ConnectAsync(new ConnectionRequest(config, false, ReuseTarget: target), _host);
        await old.Started.Task;
        await tab.DisconnectAsync();
        Assert.True(old.Token.IsCancellationRequested);
        await orchestrator.ConnectAsync(new ConnectionRequest(config, false, ReuseTarget: target), _host);
        old.Late.SetException(new InvalidOperationException("old failed late"));
        await first;
        Assert.Equal(Kei.Term.App.ViewModels.ConnectionState.Connected, tab.State);
        Assert.DoesNotContain("old failed late", tab.StatusMessage ?? "");
        Assert.True(old.Disposed);
        await tab.DisposeAsync();
    });

    private sealed class SingleDelayedConnectFactory(ITerminalSession first) : IProtocolSessionFactory
    {
        private int _calls;
        public bool Supports(string protocol, TransportEngine engine) => true;
        public Task<ITerminalSession> CreateSessionAsync(ResolvedSessionConfig config, TransportEngine engine, CancellationToken ct = default)
            => Task.FromResult(Interlocked.Increment(ref _calls) == 1 ? first : new RoutingSession());
    }

    private sealed class DelayedConnectingSession : RoutingSession
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Late { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public override Task ConnectAsync(CancellationToken ct = default)
        {
            Token = ct;
            Started.SetResult();
            return Late.Task;
        }
    }
    [Fact]
    public Task ProtocolRouting_DisconnectAndNewAttemptRejectLateFactory() => HeadlessAvalonia.RunAsync(async () =>
    {
        var vault = new VaultSessionService(new PlainVault(), new PlainVault(), _settings, () => _ui);
        var collector = new AuthMaterialCollector(new InMemoryIdentities(), _settings, vault, () => _ui);
        var sessions = new DelayedRoutingSessions();
        var orchestrator = new ConnectionOrchestrator(_factory, collector, null, _settings, () => _ui, action => action(), protocolSessions: sessions);
        var tab = new Kei.Term.App.ViewModels.TerminalTabViewModel("attempt", "monospace", 14);
        var target = new TerminalTabConnectionTarget(tab, _settings, null, null);
        var config = Config() with { Protocol = SessionProtocols.Telnet };
        var first = orchestrator.ConnectAsync(new ConnectionRequest(config, false, ReuseTarget: target), _host);
        await sessions.Started.Task;
        await tab.DisconnectAsync();
        Assert.True(sessions.Cancellation.IsCancellationRequested);
        await orchestrator.ConnectAsync(new ConnectionRequest(config, false, ReuseTarget: target), _host);
        sessions.Late.SetResult(sessions.Old);
        await first;
        Assert.Equal(Kei.Term.App.ViewModels.ConnectionState.Connected, tab.State);
        Assert.True(sessions.Old.Disposed);
        Assert.False(sessions.Old.IsConnected);
        await tab.DisposeAsync();
    });

    private sealed class DelayedRoutingSessions : IProtocolSessionFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ITerminalSession> Late { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RoutingSession Old { get; } = new();
        public CancellationToken Cancellation { get; private set; }
        private int _calls;
        public bool Supports(string protocol, TransportEngine engine) => true;
        public Task<ITerminalSession> CreateSessionAsync(ResolvedSessionConfig config, TransportEngine engine, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) != 1) return Task.FromResult<ITerminalSession>(new RoutingSession());
            Cancellation = ct;
            Started.SetResult();
            return Late.Task;
        }
    }
    [Fact]
    public async Task ProtocolRouting_ImmediateDisconnectDoesNotBecomeConnected()
    {
        var vault = new VaultSessionService(new PlainVault(), new PlainVault(), _settings, () => _ui);
        var collector = new AuthMaterialCollector(new InMemoryIdentities(), _settings, vault, () => _ui);
        var orchestrator = new ConnectionOrchestrator(_factory, collector, null, _settings, () => _ui, action => action(),
            protocolSessions: new RoutingSessions { CloseImmediately = true });
        var config = Config() with { Protocol = SessionProtocols.Telnet };
        await orchestrator.ConnectAsync(new ConnectionRequest(config, false), _host);
        var target = Assert.Single(_host.Tabs);
        Assert.False(target.AttachedSession!.IsConnected);
        Assert.False(target.Connected);
        Assert.Equal(0, target.FileSystemAttachments);
    }

    [Fact]
    public async Task ProtocolRouting_NonSshJumpIsRejectedWithoutCollectingItsCredentials()
    {
        var jump = new SessionNode { Protocol = SessionProtocols.Telnet, Host = "device", Username = "" };
        _host.Sessions[jump.Id] = jump;
        await Create().ConnectAsync(new ConnectionRequest(Config(jump: jump.Id), false, Password("target")), _host);
        Assert.Empty(_ui.AuthPromptUsernames);
        Assert.Empty(_factory.SessionCalls);
        Assert.Empty(_host.Tabs);
    }

    [Fact]
    public async Task ProtocolRouting_AutomaticReconnectUsesGenericRouteWithoutCredentials()
    {
        _settings.Current.AutoReconnectOnDisconnect = true;
        var vault = new VaultSessionService(new PlainVault(), new PlainVault(), _settings, () => _ui);
        var collector = new AuthMaterialCollector(new InMemoryIdentities(), _settings, vault, () => _ui);
        var orchestrator = new ConnectionOrchestrator(_factory, collector, null, _settings, () => _ui, action => action(),
            delay: (_, _) => Task.CompletedTask, protocolSessions: new RoutingSessions());
        var config = Config() with { Protocol = SessionProtocols.Telnet, Username = "" };
        await orchestrator.ConnectAsync(new ConnectionRequest(config, true), _host);
        var target = Assert.Single(_host.Tabs);
        var old = target.AttachedSession!;
        await old.DisposeAsync();
        await orchestrator.ScheduleReconnectAsync(new ConnectionRequest(config, true, ReuseTarget: target), _host,
            new ReconnectFacts(true, false, false, false, false, 1), CancellationToken.None);
        Assert.True(target.Connected);
        Assert.NotSame(old, target.AttachedSession);
        Assert.Single(_host.Tabs);
        Assert.Equal(SessionProtocols.Telnet, Assert.Single(target.Resets).Protocol);
        Assert.Empty(_ui.AuthPromptUsernames);
        Assert.Empty(_factory.SessionCalls);
        Assert.Equal(0, target.FileSystemAttachments);
        await target.AttachedSession!.DisposeAsync();
    }

    [Fact]
    public async Task ProtocolRouting_EndpointObservesChangingResizeCapability()
    {
        var session = new NegotiatedRoutingSession();
        await session.ConnectAsync();
        byte[]? output = null;
        using var endpoint = new TerminalSessionEndpoint(session, bytes => output = bytes, () => (80, 24));
        endpoint.SetSize(640, 480);
        Assert.Null(output);
        session.CanResize = true;
        endpoint.SetSize(640, 480);
        Assert.Equal(new byte[] { 80, 24 }, output);
        session.CanResize = false;
        output = null;
        endpoint.SetSize(800, 600);
        Assert.Null(output);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ProtocolRouting_GenericConnectionExchangesBytesWithoutSshOrFiles()
    {
        var sessions = new RoutingSessions();
        var vault = new VaultSessionService(new PlainVault(), new PlainVault(), _settings, () => _ui);
        var collector = new AuthMaterialCollector(new InMemoryIdentities(), _settings, vault, () => _ui);
        var orchestrator = new ConnectionOrchestrator(_factory, collector, null, _settings, () => _ui, action => action(), protocolSessions: sessions);
        var config = Config() with { Protocol = SessionProtocols.Telnet, Username = "", IdentityId = Guid.NewGuid() };
        await orchestrator.ConnectAsync(new ConnectionRequest(config, true), _host);
        var target = Assert.Single(_host.Tabs);
        Assert.True(target.Connected);
        var session = Assert.IsType<RoutingSession>(target.AttachedSession);
        byte[]? output = null;
        Exception? disconnected = null;
        bool ended = false;
        using var endpoint = new TerminalSessionEndpoint(session, bytes => output = bytes, () => (80, 24));
        session.Disconnected += error => { ended = true; disconnected = error; };
        endpoint.SendText(new byte[] { 0, 255, 65 });
        Assert.Equal(new byte[] { 0, 255, 65 }, output);
        endpoint.SetSize(640, 480);
        Assert.False(((ITerminalSession)session).Capabilities.CanResize);
        Assert.False(((ITerminalSession)session).Capabilities.HasFileSystem);
        await session.DisposeAsync();
        Assert.True(ended);
        Assert.Null(disconnected);
        Assert.False(session.IsConnected);
        endpoint.Dispose();
        output = null;
        session.Emit(new byte[] { 9 });
        Assert.Null(output);
        Assert.Empty(_ui.AuthPromptUsernames);
        Assert.Empty(_factory.SessionCalls);
        Assert.Equal(0, target.FileSystemAttachments);
    }

    private sealed class RoutingSessions : IProtocolSessionFactory
    {
        public bool CloseImmediately { get; init; }
        public bool Supports(string protocol, TransportEngine engine) => protocol == SessionProtocols.Telnet && engine == TransportEngine.BuiltIn;
        public Task<ITerminalSession> CreateSessionAsync(ResolvedSessionConfig config, TransportEngine engine, CancellationToken ct = default)
            => Task.FromResult<ITerminalSession>(new RoutingSession { CloseImmediately = CloseImmediately });
    }

    private class RoutingSession : ITerminalSession
    {
        public bool Disposed { get; private set; }
        public bool CloseImmediately { get; init; }
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected { get; private set; }
        public event Action<byte[]>? OutputReceived;
        public event Action<Exception?>? Disconnected;
        public void Emit(byte[] bytes) => OutputReceived?.Invoke(bytes);
        public virtual Task ConnectAsync(CancellationToken ct = default)
        {
            IsConnected = !CloseImmediately;
            if (CloseImmediately) Disconnected?.Invoke(null);
            return Task.CompletedTask;
        }
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (!IsConnected) throw new InvalidOperationException("已断开");
            OutputReceived?.Invoke(data.ToArray());
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsConnected = false;
            Disconnected?.Invoke(null);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NegotiatedRoutingSession : RoutingSession, ITerminalSession
    {
        public bool CanResize { get; set; }
        public TerminalSessionCapabilities Capabilities => new(CanResize: CanResize);
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default)
        {
            Emit(new byte[] { (byte)columns, (byte)rows });
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(SessionProtocols.Telnet, TransportEngine.BuiltIn)]
    [InlineData(SessionProtocols.Serial, TransportEngine.GoCore)]
    [InlineData("unknown", TransportEngine.BuiltIn)]
    [InlineData(SessionProtocols.Ssh, (TransportEngine)99)]
    public async Task ProtocolRouting_UnsupportedRouteReportsBeforeAuthentication(string protocol, TransportEngine engine)
    {
        var target = new FakeTarget();
        var config = Config() with { Protocol = protocol, TransportEngineOverride = engine, Username = "" };
        await Create().ConnectAsync(new ConnectionRequest(config, true, ReuseTarget: target), _host);
        Assert.False(target.Connected);
        Assert.NotNull(target.Error);
        Assert.Contains(protocol, target.Error);
        Assert.Empty(_ui.AuthPromptUsernames);
        Assert.Empty(_factory.SessionCalls);
        Assert.Empty(_host.Tabs);
    }
}
