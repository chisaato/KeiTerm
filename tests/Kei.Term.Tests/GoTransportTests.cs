using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Ssh.Services;
using Kei.Term.Transport.Go;
using Xunit;

namespace Kei.Term.Tests;

public class GoTransportTests
{
    [Theory]
    [InlineData(SshTransportEngine.BuiltIn, null, SshTransportEngine.BuiltIn)]
    [InlineData(SshTransportEngine.GoSidecar, null, SshTransportEngine.GoSidecar)]
    [InlineData(SshTransportEngine.GoSidecar, SshTransportEngine.BuiltIn, SshTransportEngine.BuiltIn)]
    [InlineData(SshTransportEngine.BuiltIn, SshTransportEngine.GoSidecar, SshTransportEngine.GoSidecar)]
    public void Builder_ResolvesEngine(SshTransportEngine global, SshTransportEngine? session, SshTransportEngine expected)
    {
        var node = new SessionNode { Host = "example.test" };
        node.Overrides.TransportEngine = session;
        Assert.Equal(expected, SessionConfigBuilder.Build(node, new AppSettings { SshTransportEngine = global }).TransportEngineOverride);
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
        var config = SessionConfigBuilder.Build(new SessionNode { Host = "example.test" }, new AppSettings { SshTransportEngine = SshTransportEngine.GoSidecar });
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
