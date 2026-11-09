using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Kei.Term.Core.Models;
using Kei.Term.Infrastructure.Protocols;

namespace Kei.Term.Tests;

public sealed class TelnetProtocolTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task FragmentedCommandsAndNvtAreFilteredAndEarlyOutputIsRetained()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = Create(listener);
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync();
        var stream = peer.GetStream();
        foreach (var value in new byte[] { 65, 255, 255, 13, 0, 66, 13, 10, 255, 250, 99, 1, 255, 255, 255, 240, 67 })
            await stream.WriteAsync(new byte[] { value });
        peer.Client.Shutdown(SocketShutdown.Send);
        await Eventually(() => !session.IsConnected);
        var output = Channel.CreateUnbounded<byte>();
        session.OutputReceived += data => { foreach (var b in data) output.Writer.TryWrite(b); };
        Assert.Equal(new byte[] { 65, 255, 13, 66, 13, 10, 67 }, await Read(output.Reader, 7));
    }

    [Fact]
    public async Task NegotiationIsDirectionalAndRepeatedUnknownOffersDoNotLoop()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = Create(listener);
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync();
        var stream = peer.GetStream();
        await stream.WriteAsync(new byte[] { 255, 251, 1, 255, 251, 1, 255, 253, 1, 255, 251, 77, 255, 251, 77, 255, 253, 77, 255, 253, 77, 255, 253, 3 });
        Assert.Equal(new byte[] { 255, 253, 1, 255, 252, 1, 255, 254, 77, 255, 252, 77, 255, 251, 3 }, await Read(stream, 15));
        await session.SendInputAsync(new byte[] { 65, 255, 13 });
        await session.SendInputAsync(new byte[] { 10, 66, 13, 67 });
        Assert.Equal(new byte[] { 65, 255, 255, 13, 0, 10, 66, 13, 0, 67 }, await Read(stream, 10));
        Assert.False(stream.DataAvailable);
    }

    [Fact]
    public async Task TerminalTypeAndWindowRequireAcceptanceAndEscapeIac()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = Create(listener);
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync();
        var stream = peer.GetStream();
        Assert.True(session.Capabilities.CanResize);
        if (session.Capabilities.CanResize) await session.ResizeTerminalAsync(255, 24, 0, 0);
        Assert.False(session.Capabilities.HasFileSystem);
        Assert.False(stream.DataAvailable);
        await stream.WriteAsync(new byte[] { 255, 253, 31, 255, 253, 24, 255, 250, 24, 1, 255, 240 });
        Assert.Equal(new byte[] { 255, 251, 31, 255, 250, 31, 0, 255, 255, 0, 24, 255, 240, 255, 251, 24, 255, 250, 24, 0, 118, 116, 49, 48, 48, 255, 240 }, await Read(stream, 27));
        Assert.True(session.Capabilities.CanResize);
        await stream.WriteAsync(new byte[] { 255, 254, 31, 255, 253, 3 });
        Assert.Equal(new byte[] { 255, 252, 31, 255, 251, 3 }, await Read(stream, 6));
        Assert.True(session.Capabilities.CanResize);
        if (session.Capabilities.CanResize) await session.ResizeTerminalAsync(120, 45, 0, 0);
        Assert.False(stream.DataAvailable);
        await stream.WriteAsync(new byte[] { 255, 253, 31 });
        Assert.Equal(new byte[] { 255, 251, 31, 255, 250, 31, 0, 120, 0, 45, 255, 240 }, await Read(stream, 12));
    }

    [Fact]
    public async Task InitialViewSizeCanBeCachedBeforeOrchestratorConnects()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = Create(listener);
        Assert.True(session.Capabilities.CanResize);
        if (session.Capabilities.CanResize) await session.ResizeTerminalAsync(132, 40, 0, 0);
        Assert.False(listener.Pending());
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync();
        await peer.GetStream().WriteAsync(new byte[] { 255, 253, 31 });
        Assert.Equal(new byte[] { 255, 251, 31, 255, 250, 31, 0, 132, 0, 40, 255, 240 }, await Read(peer.GetStream(), 12));
        await session.DisposeAsync();
        Assert.False(session.Capabilities.CanResize);
    }

    [Fact]
    public async Task BinaryDirectionsAreIndependentAndDisablingRestoresNvt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = Create(listener);
        var output = Channel.CreateUnbounded<byte>();
        session.OutputReceived += data => { foreach (var b in data) output.Writer.TryWrite(b); };
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync();
        var stream = peer.GetStream();
        await stream.WriteAsync(new byte[] { 255, 251, 1, 255, 251, 0, 13, 0, 255, 253, 0 });
        Assert.Equal(new byte[] { 255, 253, 1, 255, 253, 0, 255, 251, 0 }, await Read(stream, 9));
        Assert.Equal(new byte[] { 13, 0 }, await Read(output.Reader, 2));
        await session.SendInputAsync(new byte[] { 13, 0, 255 });
        Assert.Equal(new byte[] { 13, 0, 255, 255 }, await Read(stream, 4));
        await stream.WriteAsync(new byte[] { 255, 252, 0, 255, 254, 0, 13, 0, 65 });
        Assert.Equal(new byte[] { 255, 254, 0, 255, 252, 0 }, await Read(stream, 6));
        Assert.Equal(new byte[] { 13, 65 }, await Read(output.Reader, 2));
    }

    [Fact]
    public async Task DisposalUnblocksReadAndRaisesExactlyOneDisconnectAndCancelledSendWritesNothing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var session = Create(listener);
        var ended = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        session.Disconnected += error => { Interlocked.Increment(ref count); ended.TrySetResult(error); };
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendInputAsync(new byte[] { 65 }, new CancellationToken(true)));
        await session.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Null(await ended.Task.WaitAsync(Deadline));
        Assert.False(session.IsConnected);
        Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(Deadline));
        await session.DisposeAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CancelledConnectDoesNotOpenSocket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = Create(listener);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync(new CancellationToken(true)));
        Assert.False(session.IsConnected);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task EchoDefaultsToLocalFallsBackAfterWontAndNeverDuplicatesRemoteEcho()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = Create(listener);
        var output = Channel.CreateUnbounded<byte>();
        session.OutputReceived += data => { foreach (var b in data) output.Writer.TryWrite(b); };
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync();
        var stream = peer.GetStream();

        await session.SendInputAsync(new byte[] { 65, 255 });
        Assert.Equal(new byte[] { 65, 255, 255 }, await Read(stream, 3));
        Assert.Equal(new byte[] { 65, 255 }, await Read(output.Reader, 2));

        await stream.WriteAsync(new byte[] { 255, 251, 1 });
        Assert.Equal(new byte[] { 255, 253, 1 }, await Read(stream, 3));
        await session.SendInputAsync(new byte[] { 66 });
        Assert.Equal(new byte[] { 66 }, await Read(stream, 1));
        await stream.WriteAsync(new byte[] { 66, 33 });
        Assert.Equal(new byte[] { 66, 33 }, await Read(output.Reader, 2));

        await stream.WriteAsync(new byte[] { 255, 252, 1 });
        Assert.Equal(new byte[] { 255, 254, 1 }, await Read(stream, 3));
        await session.SendInputAsync(new byte[] { 67 });
        Assert.Equal(new byte[] { 67 }, await Read(stream, 1));
        Assert.Equal(new byte[] { 67 }, await Read(output.Reader, 1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendInputAsync(new byte[] { 68 }, new CancellationToken(true)));
        peer.Client.Shutdown(SocketShutdown.Send);
        await Eventually(() => !session.IsConnected);
        Assert.False(output.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RemoteCloseReplaysDisconnectToLateSubscriberAndCrIsNotHeldBack()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = Create(listener);
        var output = Channel.CreateUnbounded<byte>();
        session.OutputReceived += data => { foreach (var b in data) output.Writer.TryWrite(b); };
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync();
        await peer.GetStream().WriteAsync(new byte[] { 13 });
        Assert.Equal(new byte[] { 13 }, await Read(output.Reader, 1));
        await session.SendInputAsync(new byte[] { 13 });
        Assert.Equal(new byte[] { 13, 0 }, await Read(peer.GetStream(), 2));
        peer.Client.Shutdown(SocketShutdown.Send);
        await Eventually(() => !session.IsConnected);
        var ended = new TaskCompletionSource<Exception?>();
        session.Disconnected += error => ended.TrySetResult(error);
        Assert.Null(await ended.Task.WaitAsync(Deadline));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task SubnegotiationAndEscapingSurviveEverySplit(int split)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = Create(listener);
        var output = Channel.CreateUnbounded<byte>();
        session.OutputReceived += data => { foreach (var b in data) output.Writer.TryWrite(b); };
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync();
        peer.NoDelay = true;
        var stream = peer.GetStream();
        await stream.WriteAsync(new byte[] { 255, 253, 24 });
        Assert.Equal(new byte[] { 255, 251, 24 }, await Read(stream, 3));
        var command = new byte[] { 255, 250, 24, 1, 255, 240, 255, 255 };
        await stream.WriteAsync(command.AsMemory(0, split));
        // 有意让前半段到达读取循环，断言不依赖 TCP 包边界或计时阈值。
        await Task.Delay(10);
        await stream.WriteAsync(command.AsMemory(split));
        Assert.Equal(new byte[] { 255, 250, 24, 0, 118, 116, 49, 48, 48, 255, 240 }, await Read(stream, 11));
        Assert.Equal(new byte[] { 255 }, await Read(output.Reader, 1));
    }

    [Fact]
    public async Task OversizedSubnegotiationEndsConnectionWithError()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = Create(listener);
        var ended = new TaskCompletionSource<Exception?>();
        session.Disconnected += error => ended.TrySetResult(error);
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync();
        await peer.GetStream().WriteAsync(new byte[] { 255, 250 }.Concat(new byte[4097]).ToArray());
        Assert.IsType<InvalidDataException>(await ended.Task.WaitAsync(Deadline));
        Assert.False(session.IsConnected);
    }

    private static TelnetTerminalSession Create(TcpListener listener) => new(Guid.NewGuid(), new TelnetConnectionOptions
    {
        Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, TerminalType = "vt100", Timeout = Deadline
    });

    private static async Task<byte[]> Read(NetworkStream stream, int count)
    {
        var data = new byte[count];
        using var timeout = new CancellationTokenSource(Deadline);
        await stream.ReadExactlyAsync(data, timeout.Token);
        return data;
    }

    private static async Task<byte[]> Read(ChannelReader<byte> reader, int count)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        var data = new byte[count];
        for (var i = 0; i < count; i++) data[i] = await reader.ReadAsync(timeout.Token);
        return data;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
