using System.Diagnostics;
using System.Buffers.Binary;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Transport.Go;

namespace Kei.Term.Tests;

// 真正构建并启动 daemon：断言覆盖 C# -> IPC -> Go -> TCP，而非请求回显 Mock。
public sealed class GoProtocolTransportTests : IClassFixture<GoProtocolDaemonBinary>
{
    private readonly GoProtocolDaemonBinary _binary;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    public GoProtocolTransportTests(GoProtocolDaemonBinary binary) => _binary = binary;

    [Fact]
    public async Task SerialCatalogQueriesRealDaemonAndMatchesLocalDeviceNames()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        var factory = new GoProtocolFactory(ct => manager.GetClientAsync(ct));
        var names = await factory.ListSerialPortsAsync();
        string[] prefixes = ["ttyS", "ttyUSB", "ttyACM", "ttyAMA", "ttyAP", "ttyTHS", "ttyXRUSB", "rfcomm"];
        var expected = Directory.EnumerateFileSystemEntries("/dev").Where(p => !Directory.Exists(p) && prefixes.Any(prefix => Path.GetFileName(p).StartsWith(prefix, StringComparison.Ordinal))).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, names);
    }

    [Fact]
    public async Task FactoryIsLazyAndTelnetNeverAdvertisesSerialOrFiles()
    {
        int calls = 0;
        var factory = new GoProtocolFactory(_ => { calls++; throw new InvalidOperationException(); });
        await using var telnet = factory.CreateTelnet(Guid.NewGuid(), new() { Host = "127.0.0.1" });
        await using var serial = factory.CreateSerial(Guid.NewGuid(), new() { PortName = "/dev/test" });
        Assert.Equal(0, calls);
        Assert.False(telnet is ISerialControl);
        Assert.True(Assert.IsAssignableFrom<ISerialControl>(serial).CanSetRts);
        await using var automaticRts = factory.CreateSerial(Guid.NewGuid(), new() { PortName = "/dev/test", FlowControl = SerialFlowControl.RtsCts });
        Assert.False(Assert.IsAssignableFrom<ISerialControl>(automaticRts).CanSetRts);
        Assert.False(telnet.Capabilities.HasFileSystem);
        Assert.True(telnet.Capabilities.CanResize);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => telnet.ConnectAsync(new CancellationToken(true)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ResizeBeforeConnectIsCachedAndNawsUsesLatestDimensions()
    {
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        using var listener = Listen();
        await using var session = Create(manager, listener);
        Assert.True(session.Capabilities.CanResize);
        await session.ResizeTerminalAsync(120, 35, 0, 0);
        await session.ResizeTerminalAsync(255, 45, 0, 0);
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        Assert.False(peer.GetStream().DataAvailable);
        await peer.GetStream().WriteAsync(new byte[] { 255, 253, 31 });
        Assert.Equal(new byte[] { 255, 251, 31, 255, 250, 31, 0, 255, 255, 0, 45, 255, 240 }, await Read(peer.GetStream(), 13));
        await session.DisposeAsync();
        Assert.False(session.Capabilities.CanResize);
    }

    [Fact]
    public async Task FragmentedNvtEarlyOutputAndRemoteEofDoNotEndSharedIpc()
    {
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        using var listener = Listen();
        await using var session = Create(manager, listener);
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        foreach (byte b in new byte[] { 65, 255, 255, 13, 0, 66, 13, 10, 255, 250, 99, 1, 255, 255, 255, 240, 67 })
            await peer.GetStream().WriteAsync(new byte[] { b });
        peer.Client.Shutdown(SocketShutdown.Send);
        await Eventually(() => !session.IsConnected);
        var output = Observe(session);
        Assert.Equal(new byte[] { 65, 255, 13, 66, 13, 10, 67 }, await Read(output, 7));
        var ended = new TaskCompletionSource<Exception?>();
        session.Disconnected += ex => ended.TrySetResult(ex);
        Assert.Null(await ended.Task.WaitAsync(Deadline));
        var client = await manager.GetClientAsync();
        Assert.True((await client.CallAsync("daemon.ping")).GetProperty("pong").GetBoolean());
        // 同一 IPC 后续仍能连接，正常 EOF 不是共享故障。
        await using var next = Create(manager, listener);
        await next.ConnectAsync();
        using var nextPeer = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        await nextPeer.GetStream().WriteAsync(new byte[] { 90 });
        Assert.Equal(new byte[] { 90 }, await Read(Observe(next), 1));
    }

    [Fact]
    public async Task NegotiationEchoNawsAndTerminalTypeMatchBuiltInContract()
    {
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        using var listener = Listen();
        await using var session = Create(manager, listener);
        var output = Observe(session);
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        var wire = peer.GetStream();
        await session.SendInputAsync(new byte[] { 65, 255, 13 });
        Assert.Equal(new byte[] { 65, 255, 255, 13, 0 }, await Read(wire, 5));
        Assert.Equal(new byte[] { 65, 255, 13 }, await Read(output, 3));
        await wire.WriteAsync(new byte[] { 255, 251, 1, 255, 251, 1, 255, 253, 1, 255, 251, 77, 255, 251, 77, 255, 253, 77, 255, 253, 77, 255, 253, 3 });
        Assert.Equal(new byte[] { 255, 253, 1, 255, 252, 1, 255, 254, 77, 255, 252, 77, 255, 251, 3 }, await Read(wire, 15));
        await session.SendInputAsync(new byte[] { 66 });
        Assert.Equal(new byte[] { 66 }, await Read(wire, 1));
        await wire.WriteAsync(new byte[] { 66, 33 });
        Assert.Equal(new byte[] { 66, 33 }, await Read(output, 2));
        await wire.WriteAsync(new byte[] { 255, 252, 1 });
        Assert.Equal(new byte[] { 255, 254, 1 }, await Read(wire, 3));
        await session.SendInputAsync(new byte[] { 67 });
        Assert.Equal(new byte[] { 67 }, await Read(wire, 1));
        Assert.Equal(new byte[] { 67 }, await Read(output, 1));
        Assert.True(session.Capabilities.CanResize);
        await session.ResizeTerminalAsync(255, 24, 0, 0);
        Assert.False(wire.DataAvailable);
        foreach (byte b in new byte[] { 255, 253, 31, 255, 253, 24, 255, 250, 24, 1, 255, 240 })
            await wire.WriteAsync(new byte[] { b });
        Assert.Equal(new byte[] { 255, 251, 31, 255, 250, 31, 0, 255, 255, 0, 24, 255, 240, 255, 251, 24, 255, 250, 24, 0, 118, 116, 49, 48, 48, 255, 240 }, await Read(wire, 27));
        await wire.WriteAsync(new byte[] { 255, 254, 31 });
        Assert.Equal(new byte[] { 255, 252, 31 }, await Read(wire, 3));
        await session.ResizeTerminalAsync(120, 45, 0, 0);
        Assert.False(wire.DataAvailable);
        await wire.WriteAsync(new byte[] { 255, 253, 31 });
        Assert.Equal(new byte[] { 255, 251, 31, 255, 250, 31, 0, 120, 0, 45, 255, 240 }, await Read(wire, 12));
    }

    [Fact]
    public async Task BinaryDirectionsAreIndependentAndDisablingRestoresNvt()
    {
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        using var listener = Listen();
        await using var session = Create(manager, listener);
        var output = Observe(session);
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        var wire = peer.GetStream();
        await wire.WriteAsync(new byte[] { 255, 251, 1, 255, 251, 0, 13, 0, 255, 253, 0 });
        Assert.Equal(new byte[] { 255, 253, 1, 255, 253, 0, 255, 251, 0 }, await Read(wire, 9));
        Assert.Equal(new byte[] { 13, 0 }, await Read(output, 2));
        await session.SendInputAsync(new byte[] { 13, 0, 255 });
        Assert.Equal(new byte[] { 13, 0, 255, 255 }, await Read(wire, 4));
        await wire.WriteAsync(new byte[] { 255, 252, 0, 255, 254, 0, 13, 0, 65 });
        Assert.Equal(new byte[] { 255, 254, 0, 255, 252, 0 }, await Read(wire, 6));
        Assert.Equal(new byte[] { 13, 65 }, await Read(output, 2));
    }

    [Fact]
    public async Task MalformedSingleConnectionAndConnectFailureLeaveSiblingWorking()
    {
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        using var listener = Listen();
        await using var bad = Create(manager, listener);
        await using var good = Create(manager, listener);
        await bad.ConnectAsync();
        using var badPeer = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        await good.ConnectAsync();
        using var goodPeer = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        var ended = new TaskCompletionSource<Exception?>();
        bad.Disconnected += ex => ended.TrySetResult(ex);
        await badPeer.GetStream().WriteAsync(new byte[] { 255, 250 }.Concat(new byte[4097]).ToArray());
        Assert.NotNull(await ended.Task.WaitAsync(Deadline));
        Assert.True(good.IsConnected);
        await goodPeer.GetStream().WriteAsync(new byte[] { 88 });
        Assert.Equal(new byte[] { 88 }, await Read(Observe(good), 1));
        using var refused = Listen();
        int port = ((IPEndPoint)refused.LocalEndpoint).Port;
        refused.Stop();
        await using var failed = new GoProtocolFactory(manager.GetClientAsync).CreateTelnet(Guid.NewGuid(), new() { Host = "127.0.0.1", Port = port });
        await Assert.ThrowsAsync<GoRpcException>(() => failed.ConnectAsync());
        Assert.False(failed.IsConnected);
        Assert.True((await (await manager.GetClientAsync()).CallAsync("daemon.ping")).GetProperty("pong").GetBoolean());
    }

    [Fact]
    public async Task DisposeUnblocksWaitAndCanceledSendDoesNotWriteOrKillSibling()
    {
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        using var listener = Listen();
        var session = Create(manager, listener);
        int count = 0;
        var ended = new TaskCompletionSource<Exception?>();
        session.Disconnected += ex => { Interlocked.Increment(ref count); ended.TrySetResult(ex); };
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendInputAsync(new byte[] { 65 }, new CancellationToken(true)));
        await session.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Null(await ended.Task.WaitAsync(Deadline));
        Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(Deadline));
        await session.DisposeAsync();
        Assert.Equal(1, count);
        Assert.True((await (await manager.GetClientAsync()).CallAsync("daemon.ping")).GetProperty("pong").GetBoolean());
    }

    [Fact]
    public async Task SharedIpcFailureDisconnectsEveryProtocolSession()
    {
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        using var listener = Listen();
        await using var first = Create(manager, listener);
        await using var second = Create(manager, listener);
        var ended1 = new TaskCompletionSource<Exception?>();
        var ended2 = new TaskCompletionSource<Exception?>();
        first.Disconnected += ex => ended1.TrySetResult(ex);
        second.Disconnected += ex => ended2.TrySetResult(ex);
        await first.ConnectAsync();
        using var peer1 = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        await second.ConnectAsync();
        using var peer2 = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        await (await manager.GetClientAsync()).CallAsync("daemon.shutdown");
        Assert.NotNull(await ended1.Task.WaitAsync(Deadline));
        Assert.NotNull(await ended2.Task.WaitAsync(Deadline));
        Assert.False(first.IsConnected);
        Assert.False(second.IsConnected);
    }

    [Fact]
    public async Task CancelledConnectReclaimsLateResponseWithoutClosingSharedIpcOrSendingSshFields()
    {
        using var listener = Listen();
        using var socket = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await accepting;
        await using var client = new GoIpcClient(socket.GetStream());
        await using var session = new GoProtocolFactory(client).CreateTelnet(Guid.NewGuid(), new() { Host = "127.0.0.1" });
        using var cancel = new CancellationTokenSource();
        var connecting = session.ConnectAsync(cancel.Token);
        var wire = server.GetStream();
        var request = await ReadRpc(wire);
        Assert.Equal("telnet.connect", request.GetProperty("method").GetString());
        var args = request.GetProperty("params");
        foreach (string field in new[] { "auth", "hostKeySha256", "insecureIgnoreHostKey", "jumpHosts", "user" })
            Assert.False(args.TryGetProperty(field, out _));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting.WaitAsync(Deadline));
        Assert.False(session.IsConnected);
        await Reply(wire, request, new { sessionId = "late", streamId = 7 });
        var disconnect = await ReadRpc(wire);
        Assert.Equal("protocol.disconnect", disconnect.GetProperty("method").GetString());
        Assert.Equal("late", disconnect.GetProperty("params").GetProperty("sessionId").GetString());
        await Reply(wire, disconnect, true);
        byte[] header = await Read(wire, 9);
        Assert.Equal(3, header[4]);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(header));
        await wire.WriteAsync(header);
        var ping = client.CallAsync("daemon.ping");
        var pingRequest = await ReadRpc(wire);
        await Reply(wire, pingRequest, new { pong = true });
        Assert.True((await ping.WaitAsync(Deadline)).GetProperty("pong").GetBoolean());
    }

    [Fact]
    public async Task SerialPtyUsesSameIpcWithoutSshAndControlFailureDoesNotDisconnect()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("此测试需要 Linux socat PTY，不替代真机验证。");
        await using var pty = await ProtocolPty.StartAsync();
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        await using var session = new GoProtocolFactory(manager.GetClientAsync).CreateSerial(Guid.NewGuid(), new() { PortName = pty.First });
        var output = Observe(session);
        await session.ConnectAsync();
        await using var peer = new FileStream(pty.Second, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        Assert.False(session.Capabilities.CanResize);
        Assert.False(session.Capabilities.HasFileSystem);
        var controls = Assert.IsAssignableFrom<ISerialControl>(session);
        Assert.True(controls.CanSetRts);
        await peer.WriteAsync(new byte[] { 65, 0, 255 });
        Assert.Equal(new byte[] { 65, 0, 255 }, await Read(output, 3));
        await session.SendInputAsync(new byte[] { 66, 0, 254 });
        Assert.Equal(new byte[] { 66, 0, 254 }, await Read(peer, 3));
        // PTY 不支持调制解调器引脚；错误必须明确且只作用于这次控制调用。
        await Assert.ThrowsAsync<GoRpcException>(() => controls.SetDtrAsync(true));
        await Assert.ThrowsAsync<GoRpcException>(() => controls.SetRtsAsync(true));
        await controls.SetBreakStateAsync(true);
        await controls.SetBreakStateAsync(false);
        Assert.True(session.IsConnected);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendInputAsync(new byte[] { 67 }, new CancellationToken(true)));
        var ended = new TaskCompletionSource<Exception?>();
        session.Disconnected += ex => ended.TrySetResult(ex);
        await controls.DisconnectAsync().WaitAsync(Deadline);
        Assert.Null(await ended.Task.WaitAsync(Deadline));
        Assert.True((await (await manager.GetClientAsync()).CallAsync("daemon.ping")).GetProperty("pong").GetBoolean());
    }

    [Fact]
    public async Task SerialPtyCanReopenSamePortAfterCloseAndExchangeFreshBytes()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("此测试需要 Linux socat PTY，不替代真机验证。");
        await using var pty = await ProtocolPty.StartAsync();
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        var factory = new GoProtocolFactory(manager.GetClientAsync);
        await using var peer = new FileStream(pty.Second, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        await using var first = factory.CreateSerial(Guid.NewGuid(), new() { PortName = pty.First });
        int firstDisconnects = 0;
        first.Disconnected += _ => Interlocked.Increment(ref firstDisconnects);
        await first.ConnectAsync();
        await peer.WriteAsync(new byte[] { 65, 0, 255 });
        Assert.Equal(new byte[] { 65, 0, 255 }, await Read(Observe(first), 3));
        await first.SendInputAsync(new byte[] { 66, 0, 254 });
        Assert.Equal(new byte[] { 66, 0, 254 }, await Read(peer, 3));
        await Assert.IsAssignableFrom<ISerialControl>(first).DisconnectAsync().WaitAsync(Deadline);
        Assert.False(first.IsConnected);
        Assert.Equal(1, firstDisconnects);

        // 重连按公开工厂契约创建新会话，端口路径及共享 daemon 保持不变。
        await using var reopened = factory.CreateSerial(Guid.NewGuid(), new() { PortName = pty.First });
        var output = Observe(reopened);
        await reopened.ConnectAsync().WaitAsync(Deadline);
        await peer.WriteAsync(new byte[] { 67, 0, 253 });
        Assert.Equal(new byte[] { 67, 0, 253 }, await Read(output, 3));
        await reopened.SendInputAsync(new byte[] { 68, 0, 252 });
        Assert.Equal(new byte[] { 68, 0, 252 }, await Read(peer, 3));
        Assert.True(reopened.IsConnected);
        Assert.Equal(1, firstDisconnects);
        Assert.True((await (await manager.GetClientAsync()).CallAsync("daemon.ping")).GetProperty("pong").GetBoolean());
    }

    [Fact]
    public async Task SharedIpcEndDisconnectsConcurrentSerialAndTelnetExactlyOnce()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("此测试需要 Linux socat PTY，不替代真机验证。");
        await using var pty = await ProtocolPty.StartAsync();
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        await using var serial = new GoProtocolFactory(manager.GetClientAsync).CreateSerial(Guid.NewGuid(), new() { PortName = pty.First });
        using var listener = Listen();
        await using var telnet = Create(manager, listener);
        var serialEnd = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var telnetEnd = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int serialCount = 0, telnetCount = 0;
        serial.Disconnected += error => { Interlocked.Increment(ref serialCount); serialEnd.TrySetResult(error); };
        telnet.Disconnected += error => { Interlocked.Increment(ref telnetCount); telnetEnd.TrySetResult(error); };
        await serial.ConnectAsync();
        await telnet.ConnectAsync();
        using var tcpPeer = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        await using var serialPeer = new FileStream(pty.Second, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        var serialOutput = Observe(serial);
        var telnetOutput = Observe(telnet);
        await serialPeer.WriteAsync(new byte[] { 71 });
        Assert.Equal(new byte[] { 71 }, await Read(serialOutput, 1));
        await tcpPeer.GetStream().WriteAsync(new byte[] { 72 });
        Assert.Equal(new byte[] { 72 }, await Read(telnetOutput, 1));
        Assert.True(serial.IsConnected);
        Assert.True(telnet.IsConnected);
        var client = await manager.GetClientAsync();
        await client.CallAsync("daemon.shutdown").WaitAsync(Deadline);
        Assert.NotNull(await serialEnd.Task.WaitAsync(Deadline));
        Assert.NotNull(await telnetEnd.Task.WaitAsync(Deadline));
        Assert.False(client.IsConnected);
        Assert.False(serial.IsConnected);
        Assert.False(telnet.IsConnected);
        await Task.WhenAll(serial.DisposeAsync().AsTask(), telnet.DisposeAsync().AsTask()).WaitAsync(Deadline);
        Assert.Equal(1, serialCount);
        Assert.Equal(1, telnetCount);
    }

    [Fact]
    public async Task SerialPtyLossEndsOnlyItsConnectionWithError()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("此测试需要 Linux socat PTY，不替代真机验证。");
        await using var pty = await ProtocolPty.StartAsync();
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        await using var session = new GoProtocolFactory(manager.GetClientAsync).CreateSerial(Guid.NewGuid(), new() { PortName = pty.First, FlowControl = SerialFlowControl.RtsCts });
        await session.ConnectAsync();
        var controls = Assert.IsAssignableFrom<ISerialControl>(session);
        Assert.False(controls.CanSetRts);
        await Assert.ThrowsAsync<GoRpcException>(() => controls.SetRtsAsync(true));
        var ended = new TaskCompletionSource<Exception?>();
        session.Disconnected += ex => ended.TrySetResult(ex);
        pty.Stop();
        Assert.NotNull(await ended.Task.WaitAsync(Deadline));
        Assert.False(session.IsConnected);
        Assert.True((await (await manager.GetClientAsync()).CallAsync("daemon.ping")).GetProperty("pong").GetBoolean());
    }

    [Fact]
    public async Task SerialWriteBudgetFailureDisconnectsOnlySerialAndSiblingTcpKeepsWorking()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("此测试需要 Linux socat PTY，不替代真机验证。");
        await using var pty = await ProtocolPty.StartAsync();
        await using var manager = new GoSidecarProcessManager(_binary.Path);
        await using var serial = new GoProtocolFactory(manager.GetClientAsync).CreateSerial(Guid.NewGuid(), new()
        {
            PortName = pty.First, ReadTimeoutMilliseconds = 20, WriteTimeoutMilliseconds = 80
        });
        await serial.ConnectAsync();
        // 打开但不消费接收端，让真实内核 PTY 缓冲填满，触发 Go 写预算而非 Mock。
        await using var blockedPeer = new FileStream(pty.Second, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        using var listener = Listen();
        await using var tcp = Create(manager, listener);
        await tcp.ConnectAsync();
        using var tcpPeer = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        var ended = new TaskCompletionSource<Exception?>();
        serial.Disconnected += ex => ended.TrySetResult(ex);
        var writing = serial.SendInputAsync(new byte[4 * 1024 * 1024]);
        var error = await ended.Task.WaitAsync(Deadline);
        Assert.NotNull(error);
        Assert.Contains("write budget exceeded", error.ToString());
        await writing.WaitAsync(Deadline);
        Assert.False(serial.IsConnected);
        Assert.True(tcp.IsConnected);
        await tcpPeer.GetStream().WriteAsync(new byte[] { 89 });
        Assert.Equal(new byte[] { 89 }, await Read(Observe(tcp), 1));
        await tcp.SendInputAsync(new byte[] { 90 });
        Assert.Equal(new byte[] { 90 }, await Read(tcpPeer.GetStream(), 1));
        Assert.True((await (await manager.GetClientAsync()).CallAsync("daemon.ping")).GetProperty("pong").GetBoolean());
    }

    private sealed class ProtocolPty(string directory, Process process) : IAsyncDisposable
    {
        public string First => Path.Combine(directory, "first");
        public string Second => Path.Combine(directory, "second");
        public static async Task<ProtocolPty> StartAsync()
        {
            string directory = Path.Combine("/tmp/opencode", "keiterm-go-protocol-pty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var start = new ProcessStartInfo("socat") { UseShellExecute = false };
            start.ArgumentList.Add("pty,raw,echo=0,link=" + Path.Combine(directory, "first"));
            start.ArgumentList.Add("pty,raw,echo=0,link=" + Path.Combine(directory, "second"));
            var pair = new ProtocolPty(directory, Process.Start(start)!);
            try { await Eventually(() => File.Exists(pair.First) && File.Exists(pair.Second)); return pair; }
            catch { await pair.DisposeAsync(); throw; }
        }
        public void Stop() { if (!process.HasExited) process.Kill(); }
        public async ValueTask DisposeAsync()
        {
            Stop(); await process.WaitForExitAsync(); process.Dispose(); Directory.Delete(directory, true);
        }
    }

    private static async Task<JsonElement> ReadRpc(Stream wire)
    {
        byte[] header = await Read(wire, 9);
        Assert.Equal(5, header[4]);
        using var doc = JsonDocument.Parse(await Read(wire, (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5))));
        return doc.RootElement.Clone();
    }
    private static async Task Reply(Stream wire, JsonElement request, object result)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = request.GetProperty("id").GetInt64(), result });
        byte[] header = new byte[9]; header[4] = 5;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), (uint)payload.Length);
        await wire.WriteAsync(header); await wire.WriteAsync(payload);
    }

    private static ITerminalSession Create(GoSidecarProcessManager manager, TcpListener listener)
        => new GoProtocolFactory(manager.GetClientAsync).CreateTelnet(Guid.NewGuid(), new()
        { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, TerminalType = "vt100", Timeout = Deadline });
    private static TcpListener Listen() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return l; }
    private static ChannelReader<byte> Observe(ITerminalSession session)
    {
        var channel = Channel.CreateUnbounded<byte>();
        session.OutputReceived += data => { foreach (byte b in data) channel.Writer.TryWrite(b); };
        return channel.Reader;
    }
    private static async Task<byte[]> Read(Stream stream, int length)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        var data = new byte[length]; await stream.ReadExactlyAsync(data, timeout.Token); return data;
    }
    private static async Task<byte[]> Read(ChannelReader<byte> reader, int length)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        var data = new byte[length]; for (int i = 0; i < length; i++) data[i] = await reader.ReadAsync(timeout.Token); return data;
    }
    private static async Task Eventually(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}

public sealed class GoProtocolDaemonBinary : IAsyncLifetime
{
    public string Path { get; private set; } = "";
    public async Task InitializeAsync()
    {
        // testhost 的 cwd 和产物均可能位于仓库外，源码路径是第三个稳定候选。
        DirectoryInfo? root = null;
        foreach (string candidate in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), System.IO.Path.GetDirectoryName(SourcePath())! })
        {
            var directory = new DirectoryInfo(candidate);
            while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Kei.Term.slnx"))) directory = directory.Parent;
            if (directory is not null) { root = directory; break; }
        }
        if (root is null) throw new IOException("Repository root not found");
        string output = System.IO.Path.Combine(Directory.Exists("/tmp/opencode") ? "/tmp/opencode" : System.IO.Path.GetTempPath(), "keiterm-lane06-daemon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        Path = System.IO.Path.Combine(output, OperatingSystem.IsWindows() ? "daemon.exe" : "daemon");
        var start = new ProcessStartInfo("go") { WorkingDirectory = System.IO.Path.Combine(root.FullName, "src/Kei.Term.Transport.Go/engine"), RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "build", "-o", Path, "./cmd/daemon" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        Assert.True(process.ExitCode == 0, await stderr + await stdout);
    }
    private static string SourcePath([CallerFilePath] string path = "") => path;
    public Task DisposeAsync() { if (Path.Length != 0) Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, true); return Task.CompletedTask; }
}
