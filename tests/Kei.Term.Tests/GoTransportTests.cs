using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Kei.Term.Core.Security;
using Kei.Term.Ssh.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Ssh.Services;
using Kei.Term.Transport.Go;
using Kei.Term.Infrastructure.Storage;
using Xunit;

namespace Kei.Term.Tests;

public class GoTransportTests
{
    [Fact]
    public void SidecarSearch_PrefersConfigurationThenOutputThenSourceTreeThenPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "keiterm-search-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(root, "src", "Kei.Term.App", "bin", "Debug", "net10.0");
        string source = Path.Combine(root, "src", "Kei.Term.Transport.Go", "engine", "cmd", "daemon");
        string path = Path.Combine(root, "tools");
        string name = OperatingSystem.IsWindows() ? "kei-trans-daemon.exe" : "kei-trans-daemon";
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(path);
        string outputBinary = Path.Combine(output, name);
        string sourceBinary = Path.Combine(source, name);
        string pathBinary = Path.Combine(path, name);
        string configured = Path.Combine(root, "configured");
        string environment = Path.Combine(root, "environment");
        try
        {
            foreach (string file in new[] { outputBinary, sourceBinary, pathBinary, configured, environment })
                File.WriteAllText(file, "test binary");
            Assert.Equal(configured, GoSidecarProcessManager.FindBinary(configured, environment, output, path));
            Assert.Equal(environment, GoSidecarProcessManager.FindBinary(null, environment, output, path));
            Assert.Equal(outputBinary, GoSidecarProcessManager.FindBinary(null, null, output, path));
            File.Delete(outputBinary);
            Assert.Equal(sourceBinary, GoSidecarProcessManager.FindBinary(null, null, output, path));
            File.Delete(sourceBinary);
            Assert.Equal(pathBinary, GoSidecarProcessManager.FindBinary(null, null, output, path));
            File.Delete(pathBinary);
            var error = Assert.Throws<FileNotFoundException>(() => GoSidecarProcessManager.FindBinary(null, null, output, path));
            Assert.Contains("dotnet build", error.Message);
            Assert.Throws<FileNotFoundException>(() => GoSidecarProcessManager.FindBinary(outputBinary, environment, output, path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false, false, HostKeyPolicy.Ask, HostKeyDecision.AcceptOnce)]
    [InlineData(false, true, HostKeyPolicy.Ask, HostKeyDecision.AcceptOnce)]
    [InlineData(false, false, HostKeyPolicy.Ask, HostKeyDecision.AcceptAndRemember)]
    [InlineData(false, true, HostKeyPolicy.Ask, HostKeyDecision.AcceptAndRemember)]
    [InlineData(false, false, HostKeyPolicy.Ask, HostKeyDecision.Reject)]
    [InlineData(false, true, HostKeyPolicy.Strict, HostKeyDecision.Reject)]
    [InlineData(true, false, HostKeyPolicy.Ask, HostKeyDecision.Reject)]
    [InlineData(true, true, HostKeyPolicy.Ask, HostKeyDecision.Reject)]
    public async Task GoConnection_SharesTrustAndDefersConfirmationBeforeSendingCredentials(
        bool known, bool useOptionsVerifier, HostKeyPolicy policy, HostKeyDecision decision)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-go-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var repository = new SqliteKnownHostRepository($"Data Source={Path.Combine(directory, "known-hosts.db")};Pooling=False");
        await repository.InitializeAsync();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var socket = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync(timeout.Token);
            await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var server = await accepting;
            await using var client = new GoIpcClient(socket.GetStream());
            byte[] rawKey = Encoding.UTF8.GetBytes("test host public key");
            var presented = new PresentedHostKey("example.test", 22, "ssh-ed25519", rawKey);
            int prompts = 0;
            var trust = new HostKeyTrustService(repository, () => policy, (_, _) =>
            {
                prompts++;
                return Task.FromResult(decision);
            });
            if (known)
                await repository.SaveAsync(new KnownHostEntry { Host = presented.Host, Port = presented.Port,
                    KeyType = presented.KeyType, PublicKeyBase64 = presented.PublicKeyBase64,
                    FingerprintSha256 = presented.FingerprintSha256, Status = KnownHostStatus.Trusted });
            // options 中的服务必须覆盖工厂回退（即使回退服务会拒绝未知主机）。
            var fallback = new HostKeyTrustService(repository, () => HostKeyPolicy.Strict);
            var factory = new GoSshSessionFactory(_ => Task.FromResult(client), useOptionsVerifier ? fallback : trust);
            var config = SessionConfigBuilder.Build(new SessionNode { Host = "example.test" }, new AppSettings()) with
            {
                EnvironmentVariables = new Dictionary<string, string> { ["LANG"] = "C.UTF-8" },
                StartupScript = "cd /work"
            };
            await using var session = await factory.CreateSessionAsync(config,
                [new(Kei.Term.Core.Abstractions.AuthMaterialKind.Password, new Kei.Term.Core.Vault.SecretPayload { Password = "secret" })],
                new SshConnectOptions { KeepAliveInterval = TimeSpan.FromSeconds(30),
                    HostKeyVerifier = useOptionsVerifier ? trust : null }, timeout.Token);
            var connecting = session.ConnectAsync(timeout.Token);
            var wire = server.GetStream();
            using var probe = await ReadRequestAsync(wire, timeout.Token);
            Assert.Equal("session.probeHostKey", probe.RootElement.GetProperty("method").GetString());
            Assert.False(probe.RootElement.GetProperty("params").TryGetProperty("auth", out _));
            var reply = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = probe.RootElement.GetProperty("id").GetInt64(),
                result = new { keyType = presented.KeyType, publicKeyBase64 = Convert.ToBase64String(rawKey) } });
            await SendAsync(wire, 0, 5, reply, timeout.Token);
            Assert.Equal(0, prompts);
            if (!known)
            {
                var failure = await Assert.ThrowsAsync<HostKeyRejectedException>(() => connecting);
                Assert.Equal(HostKeyVerdict.Unknown, failure.Outcome.Evaluation.Verdict);
                Assert.Equal(presented.FingerprintSha256, failure.Outcome.Evaluation.Presented.FingerprintSha256);
                Assert.Equal(policy == HostKeyPolicy.Ask, failure.Outcome.RequiresConfirmation);
                Assert.Equal(policy == HostKeyPolicy.Ask ? (HostKeyDecision?)null : HostKeyDecision.Reject,
                    failure.Outcome.Decision);
                Assert.Empty(await repository.GetAllAsync());
                // 未信任的密钥不能触发连接 RPC 或泄露认证材料。
                Assert.False(wire.DataAvailable);
                if (!failure.Outcome.RequiresConfirmation) return;
                // 模拟编排器在连接外确认后重试，复用同一信任服务与已有会话。
                bool accepted = await trust.ConfirmAsync(failure.Outcome.Evaluation, timeout.Token);
                Assert.Equal(1, prompts);
                Assert.Equal(decision == HostKeyDecision.AcceptAndRemember ? 1 : 0,
                    (await repository.GetAllAsync()).Count);
                if (!accepted) return;
                connecting = session.ConnectAsync(timeout.Token);
                using var retryProbe = await ReadRequestAsync(wire, timeout.Token);
                Assert.Equal("session.probeHostKey", retryProbe.RootElement.GetProperty("method").GetString());
                Assert.False(retryProbe.RootElement.GetProperty("params").TryGetProperty("auth", out _));
                await SendAsync(wire, 0, 5, JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0",
                    id = retryProbe.RootElement.GetProperty("id").GetInt64(),
                    result = new { keyType = presented.KeyType, publicKeyBase64 = Convert.ToBase64String(rawKey) } }), timeout.Token);
            }
            using var connect = await ReadRequestAsync(wire, timeout.Token);
            Assert.Equal(known ? 0 : 1, prompts);
            Assert.Equal("session.connect", connect.RootElement.GetProperty("method").GetString());
            var args = connect.RootElement.GetProperty("params");
            Assert.Equal(presented.FingerprintSha256, args.GetProperty("hostKeySha256").GetString());
            Assert.Equal(30, args.GetProperty("keepAliveSeconds").GetInt32());
            Assert.Equal("C.UTF-8", args.GetProperty("env").GetProperty("LANG").GetString());
            Assert.Equal("cd /work", args.GetProperty("startupScript").GetString());
            Assert.Equal("secret", args.GetProperty("auth").GetProperty("password").GetString());
            long id = connect.RootElement.GetProperty("id").GetInt64();
            await SendAsync(wire, 0, 5, Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"sessionId\":\"s\",\"streamId\":7}}}}"), timeout.Token);
            await connecting;
            await client.DisposeAsync();
        }
        finally { listener.Stop(); Directory.Delete(directory, recursive: true); }
    }

    private static async Task<JsonDocument> ReadRequestAsync(Stream wire, CancellationToken ct)
    {
        byte[] header = new byte[9];
        await wire.ReadExactlyAsync(header, ct);
        Assert.Equal(5, header[4]);
        byte[] payload = new byte[BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5))];
        await wire.ReadExactlyAsync(payload, ct);
        return JsonDocument.Parse(payload);
    }

    [Theory]
    [InlineData(TransportEngine.BuiltIn, null, TransportEngine.BuiltIn)]
    [InlineData(TransportEngine.GoCore, null, TransportEngine.GoCore)]
    [InlineData(TransportEngine.GoCore, TransportEngine.BuiltIn, TransportEngine.BuiltIn)]
    [InlineData(TransportEngine.BuiltIn, TransportEngine.GoCore, TransportEngine.GoCore)]
    public void Builder_ResolvesEngine(TransportEngine global, TransportEngine? session, TransportEngine expected)
    {
        var node = new SessionNode { Host = "example.test" };
        node.Overrides.TransportEngine = session;
        Assert.Equal(expected, SessionConfigBuilder.Build(node, new AppSettings { TransportEngine = global }).TransportEngineOverride);
    }

    [Fact]
    public async Task DefaultEngine_NeverCreatesSidecarFactory()
    {
        var factory = new CompositeSshSessionFactory(new SshSessionFactory(), () => throw new InvalidOperationException("Go 不得初始化"));
        var config = SessionConfigBuilder.Build(new SessionNode { Host = "example.test" }, new AppSettings());
        await using var session = await factory.CreateSessionAsync(config, [new(Kei.Term.Core.Abstractions.AuthMaterialKind.Password,
            new Kei.Term.Core.Vault.SecretPayload { Password = "test" })]);
        Assert.IsType<SshNetSession>(session);
        await using var files = await factory.CreateFileSystemAsync(config, [new(Kei.Term.Core.Abstractions.AuthMaterialKind.Password,
            new Kei.Term.Core.Vault.SecretPayload { Password = "test" })]);
        Assert.IsType<SftpRemoteFileSystem>(files);
        await using var transfer = await factory.CreateTransferChannelAsync(config, [new(Kei.Term.Core.Abstractions.AuthMaterialKind.Password,
            new Kei.Term.Core.Vault.SecretPayload { Password = "test" })]);
        Assert.NotNull(transfer);
    }

    [Fact]
    public async Task GoOverride_SelectsGoFactoryWithoutStartingDaemon()
    {
        await using var manager = new GoSidecarProcessManager("/nonexistent/kei-trans-daemon");
        var factory = new CompositeSshSessionFactory(new SshSessionFactory(), () => new GoSshSessionFactory(manager));
        var config = SessionConfigBuilder.Build(new SessionNode { Host = "example.test" }, new AppSettings { TransportEngine = TransportEngine.GoCore });
        await using var session = await factory.CreateSessionAsync(config, []);
        Assert.IsType<GoSshSession>(session);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ConnectAsync());
    }

    [Fact]
    public async Task Ipc_ParsesFragmentedFramesAndRegistersStreamBeforeOutput()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var socket = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync(timeout.Token);
            await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var server = await accepting;
            await using var client = new GoIpcClient(socket.GetStream());
            var call = client.CallAsync("session.connect", new { host = "test" }, timeout.Token);
            var wire = server.GetStream();
            byte[] header = new byte[9];
            await wire.ReadExactlyAsync(header, timeout.Token);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(header));
            Assert.Equal(5, header[4]);
            byte[] request = new byte[BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5))];
            await wire.ReadExactlyAsync(request, timeout.Token);
            using var doc = JsonDocument.Parse(request);
            long id = doc.RootElement.GetProperty("id").GetInt64();
            await SendAsync(wire, 0, 5, Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"sessionId\":\"s\",\"streamId\":7}}}}"), timeout.Token);
            await SendAsync(wire, 7, 1, Encoding.UTF8.GetBytes("hello"), timeout.Token);
            await SendAsync(wire, 7, 3, [], timeout.Token);
            var response = await call;
            await using var stream = client.GetStream(response.GetProperty("streamId").GetUInt32());
            byte[] output = new byte[5];
            await stream.ReadExactlyAsync(output, timeout.Token);
            Assert.Equal("hello", Encoding.UTF8.GetString(output));
            Assert.Equal(0, await stream.ReadAsync(output, timeout.Token));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Ipc_DisconnectFailsPendingRpc()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var socket = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync(timeout.Token);
            await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var server = await accepting;
            await using var client = new GoIpcClient(socket.GetStream());
            var call = client.CallAsync("daemon.ping", ct: timeout.Token);
            server.Dispose();
            await Assert.ThrowsAnyAsync<IOException>(() => call);
        }
        finally { listener.Stop(); }
    }

    private static async Task SendAsync(Stream stream, uint id, byte type, byte[] payload, CancellationToken ct)
    {
        byte[] frame = new byte[9 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, id);
        frame[4] = type;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), (uint)payload.Length);
        payload.CopyTo(frame, 9);
        // 模拟短读，不能假设一次 Read 就取得完整帧头或 payload。
        foreach (byte value in frame) await stream.WriteAsync(new byte[] { value }, ct);
    }
}
