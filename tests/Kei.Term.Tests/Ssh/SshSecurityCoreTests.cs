using Kei.Term.Core.Security;
using Kei.Term.Ssh.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace Kei.Term.Tests;

public sealed class SshSecurityCoreTests
{
    private static SshTarget Target(string host = "target.example")
        => new(host, 2222, "user", [new PasswordAuthenticationMethod("user", "password")]);

    private static SshClientOptions Options(IHostKeyVerifier? verifier = null)
        => new(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(13), verifier);

    [Theory]
    [InlineData(HostKeyVerdict.Unknown, false)]
    [InlineData(HostKeyVerdict.Changed, false)]
    [InlineData(HostKeyVerdict.Revoked, false)]
    [InlineData(HostKeyVerdict.Trusted, true)]
    [InlineData(HostKeyVerdict.Unknown, true)]
    public async Task Dialer_EnforcesHostKeyDecision_AndPreservesLogicalEndpoint(HostKeyVerdict verdict, bool accepted)
    {
        Verifier verifier = new(verdict, accepted);
        await using SshDialer dialer = new(Target(), [], null, Options(verifier), NullLogger.Instance, operations: new TestOperations());
        TestClient? created = null;
        Task<TestClient> connect = dialer.ConnectClientAsync(info => created = new TestClient(info), CancellationToken.None);

        if (accepted)
        {
            using TestClient connected = await connect;
            Assert.True(connected.IsConnected);
            Assert.False(connected.Disposed);
            Assert.Equal(TimeSpan.FromSeconds(13), connected.KeepAliveInterval);
            Assert.Equal(TimeSpan.FromSeconds(7), connected.ConnectionInfo.Timeout);
            Assert.StartsWith("rsa-sha2-", connected.ConnectionInfo.HostKeyAlgorithms.Keys.First());
        }
        else
        {
            HostKeyRejectedException error = await Assert.ThrowsAsync<HostKeyRejectedException>(() => connect);
            Assert.Equal(verdict, error.Outcome.Evaluation.Verdict);
            Assert.IsType<SshConnectionException>(error.InnerException);
            Assert.True(created!.Disposed);
            Assert.False(created.Connected);
        }

        Assert.NotNull(verifier.Presented);
        Assert.Equal("target.example", verifier.Presented.Host);
        Assert.Equal(2222, verifier.Presented.Port);
        Assert.Equal("ssh-rsa", verifier.Presented.KeyType);
        Assert.Equal(created!.PresentedBlob, verifier.Presented.PublicKey);
    }

    [Fact]
    public async Task Gate_VerifierFailureFailsClosed_AndUnrelatedTransportFailureIsNotRelabeled()
    {
        Verifier verifier = new(HostKeyVerdict.Trusted, true) { Failure = new IOException("信任库不可用") };
        await using SshDialer dialer = new(Target(), [], null, Options(verifier), NullLogger.Instance, operations: new TestOperations());
        HostKeyRejectedException error = await Assert.ThrowsAsync<HostKeyRejectedException>(() =>
            dialer.ConnectClientAsync(info => new TestClient(info), CancellationToken.None));
        Assert.False(error.Outcome.Accepted);
        Assert.Equal(HostKeyDecision.Reject, error.Outcome.Decision);

        TestClient? failed = null;
        IOException transportError = new("传输失败");
        await using SshDialer noVerifier = new(Target(), [], null, Options(), NullLogger.Instance, operations: new TestOperations(false));
        Exception actual = await Assert.ThrowsAsync<IOException>(() => noVerifier.ConnectClientAsync(
            info => failed = new TestClient(info) { Failure = transportError }, CancellationToken.None));
        Assert.Same(transportError, actual);
        Assert.True(failed!.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(17)]
    public void KeepAlive_NonPositiveDoesNotReplaceExistingInterval(int seconds)
    {
        using TestClient client = new(SshJumpChain.BuildConnectionInfo(Target(), "remote", 22, Options()));
        client.KeepAliveInterval = TimeSpan.FromSeconds(29);
        SshJumpChain.ApplyKeepAlive(client, Options() with { KeepAliveInterval = TimeSpan.FromSeconds(seconds) });
        Assert.Equal(TimeSpan.FromSeconds(seconds > 0 ? seconds : 29), client.KeepAliveInterval);
    }

    [Fact]
    public async Task JumpChain_RoutesThroughConnectedPredecessor_AndDisposesAllResources()
    {
        List<TestClient> clients = [];
        List<TestForward> forwards = [];
        SshJumpChain chain = await SshJumpChain.OpenAsync([Target("outer"), Target("inner"), Target("deep")],
            Options(new Verifier(HostKeyVerdict.Trusted, true)), NullLogger.Instance, CancellationToken.None,
            info =>
            {
                TestClient client = new(info);
                if (clients.Count > 0)
                {
                    TestForward route = forwards[^1];
                    Assert.True(route.Started);
                    Assert.Equal("127.0.0.1", info.Host);
                    Assert.Equal((int)route.BoundPort, info.Port);
                }
                clients.Add(client);
                return client;
            },
            (bind, port, host, targetPort) =>
            {
                TestForward forward = new(bind, (uint)(23000 + forwards.Count), host, targetPort, clients[^1]);
                forwards.Add(forward);
                return forward;
            }, new TestOperations());

        Assert.True(chain.IsConnected);
        Assert.Equal(new[] { "inner", "deep" }, forwards.Select(f => f.Host));
        Assert.All(clients, client => Assert.Equal(TimeSpan.FromSeconds(13), client.KeepAliveInterval));
        await chain.DisposeAsync();
        await chain.DisposeAsync();
        Assert.False(chain.IsConnected);
        Assert.All(clients, client => Assert.True(client.Disposed));
        Assert.All(forwards, forward => Assert.False(forward.Started));
        Assert.Throws<ObjectDisposedException>(() => chain.ForwardTo("unreachable", 22));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JumpChain_FailureReleasesEarlierHopsAndForwards(bool failForward)
    {
        List<TestClient> clients = [];
        List<TestForward> forwards = [];
        IOException failure = new("中途失败");
        IOException actual = await Assert.ThrowsAsync<IOException>(() => SshJumpChain.OpenAsync(
            [Target("outer"), Target("inner"), Target("never")], Options(), NullLogger.Instance, CancellationToken.None,
            info =>
            {
                TestClient client = new(info) { Failure = clients.Count == 1 ? failure : null };
                clients.Add(client);
                return client;
            },
            (bind, port, host, targetPort) =>
            {
                if (failForward) throw failure;
                TestForward forward = new(bind, 23000, host, targetPort, clients[^1]);
                forwards.Add(forward);
                return forward;
            }, new TestOperations(false)));
        Assert.Same(failure, actual);
        Assert.Equal(failForward ? 1 : 2, clients.Count);
        Assert.All(clients, client => Assert.True(client.Disposed));
        Assert.All(forwards, forward => Assert.False(forward.Started));
    }

    [Fact]
    public async Task JumpChain_EmptyRouteIsRejected()
        => await Assert.ThrowsAsync<ArgumentException>(() => SshJumpChain.OpenAsync([], Options(), NullLogger.Instance, CancellationToken.None));

    [Fact]
    public async Task BorrowedChain_ReusesTargetTunnel_AndDisposingDialerDoesNotCloseOwner()
    {
        TestClient? owner = null;
        List<TestForward> forwards = [];
        await using SshJumpChain chain = await SshJumpChain.OpenAsync([Target("outer")], Options(),
            NullLogger.Instance, CancellationToken.None, info => owner = new TestClient(info),
            (bind, port, host, targetPort) =>
            {
                TestForward forward = new(bind, (uint)(24000 + forwards.Count), host, targetPort, owner!);
                forwards.Add(forward);
                return forward;
            }, new TestOperations(false));
        Verifier verifier = new(HostKeyVerdict.Trusted, true);
        SshTarget target = Target() with { Socks5Host = "must-not-use-proxy", Socks5Port = 1080 };
        SshDialer dialer = new(target, [], chain, Options(verifier), NullLogger.Instance, operations: new TestOperations());
        using TestClient first = await dialer.ConnectClientAsync(info => new TestClient(info), CancellationToken.None);
        using TestClient second = await dialer.ConnectClientAsync(info => new TestClient(info), CancellationToken.None);
        Assert.Equal("127.0.0.1", first.ConnectionInfo.Host);
        Assert.Equal(ProxyTypes.None, first.ConnectionInfo.ProxyType);
        Assert.Equal(first.ConnectionInfo.Port, second.ConnectionInfo.Port);
        Assert.True(Assert.Single(forwards).Started);
        Assert.Equal(target.Host, verifier.Presented!.Host);
        Assert.Equal(target.Port, verifier.Presented.Port);
        await dialer.DisposeAsync();
        Assert.False(forwards[0].Started);
        Assert.True(chain.IsConnected);
        Assert.False(owner!.Disposed);
    }

    [Theory]
    [InlineData("remote.example", true)]
    [InlineData("127.0.0.1", false)]
    public void ConnectionInfo_UsesProxyOnlyOutsideLocalTunnel(string dialHost, bool usesProxy)
    {
        SshTarget target = Target() with { Socks5Host = "proxy", Socks5Port = 1080, Socks5Username = "proxy-user", Socks5Password = "secret" };
        ConnectionInfo info = SshJumpChain.BuildConnectionInfo(target, dialHost, 23000, Options());
        Assert.Equal(usesProxy ? ProxyTypes.Socks5 : ProxyTypes.None, info.ProxyType);
        Assert.Equal(dialHost, info.Host);
        Assert.Equal(23000, info.Port);
        Assert.Equal("user", info.Username);
        Assert.Equal(TimeSpan.FromSeconds(7), info.Timeout);
        if (usesProxy)
        {
            Assert.Equal("proxy", info.ProxyHost);
            Assert.Equal(1080, info.ProxyPort);
            Assert.Equal("proxy-user", info.ProxyUsername);
            Assert.Equal("secret", info.ProxyPassword);
        }
    }

    private sealed class Verifier(HostKeyVerdict verdict, bool accepted) : IHostKeyVerifier
    {
        public PresentedHostKey? Presented { get; private set; }
        public Exception? Failure { get; init; }
        public Task<IReadOnlyList<string>> GetKnownKeyTypesAsync(string host, int port, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(["ssh-rsa"]);
        public Task<HostKeyCheckOutcome> VerifyAsync(PresentedHostKey presented, CancellationToken ct = default)
        {
            Presented = presented;
            if (Failure != null) throw Failure;
            return Task.FromResult(new HostKeyCheckOutcome(accepted, new HostKeyEvaluation(verdict, presented, []),
                accepted ? HostKeyDecision.AcceptOnce : HostKeyDecision.Reject));
        }
    }

    private sealed class TestClient(ConnectionInfo info) : SshClient(info)
    {
        public bool Connected { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? Failure { get; init; }
        public byte[]? PresentedBlob { get; private set; }
        public override bool IsConnected => Connected && !Disposed;
        public Task ConnectForTestAsync(HostKeyGate? gate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Failure != null) throw Failure;
            using RsaKey key = new(new System.Numerics.BigInteger(3233), new System.Numerics.BigInteger(17),
                new System.Numerics.BigInteger(2753), new System.Numerics.BigInteger(61), new System.Numerics.BigInteger(53), new System.Numerics.BigInteger(38));
            HostKeyEventArgs args = new(new KeyHostAlgorithm("rsa-sha2-512", key));
            PresentedBlob = args.HostKey;
            gate?.OnHostKeyReceived(this, args);
            if (!args.CanTrust) throw new SshConnectionException("密钥交换失败");
            Connected = true;
            return Task.CompletedTask;
        }
        protected override void Dispose(bool disposing)
        {
            Connected = false;
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TestOperations(bool raiseKey = true) : SshConnectionOperations
    {
        public override Task ConnectAsync(HostKeyGate gate, BaseClient client, CancellationToken ct)
            => gate.ConnectAsync(client, ct, token => ((TestClient)client).ConnectForTestAsync(raiseKey ? gate : null, token));

        public override void AddForwardedPort(SshClient client, ForwardedPortLocal forward)
        {
            if (!client.IsConnected) throw new IOException("前置跳板未连接");
        }
    }

    private sealed class TestForward(string bind, uint port, string host, uint targetPort, TestClient owner)
        : ForwardedPortLocal(bind, port, host, targetPort)
    {
        public bool Started { get; private set; }
        public override void Start()
        {
            if (!owner.IsConnected) throw new IOException("前置跳板已断开");
            Started = true;
        }
        public override void Stop() => Started = false;
    }
}
