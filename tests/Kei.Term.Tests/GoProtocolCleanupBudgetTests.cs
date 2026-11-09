using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Kei.Term.Core.Models;
using Kei.Term.Transport.Go;
using Xunit;

namespace Kei.Term.Tests;

public sealed class GoProtocolCleanupBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedDisconnectOrClose_ReturnsWithinTotalBudgetWithoutBreakingSibling(bool blockClose)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var socket = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var peer = await accepting;
        var gate = new GatedWrites(socket.GetStream(), blockClose);
        await using var client = new GoIpcClient(gate);
        using var stop = new CancellationTokenSource();
        var serving = ServeAsync(peer.GetStream(), stop.Token);
        var factory = new GoProtocolFactory(client);
        var session = factory.CreateTelnet(Guid.NewGuid(), new TelnetConnectionOptions { Host = "test" });
        await using var sibling = factory.CreateTelnet(Guid.NewGuid(), new TelnetConnectionOptions { Host = "sibling" });
        int disconnected = 0;
        session.Disconnected += _ => Interlocked.Increment(ref disconnected);
        await session.ConnectAsync();
        await sibling.ConnectAsync();
        gate.Armed = true;
        var disposing = session.DisposeAsync().AsTask();
        try
        {
            await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(6)));
            Assert.True(disposing.IsCompleted, "Dispose exceeded the total cleanup budget");
            await Assert.ThrowsAsync<TimeoutException>(() => disposing);
            Assert.Equal(1, disconnected);
            Assert.True(client.IsConnected);
        }
        finally
        {
            gate.Release.TrySetResult();
            try { await disposing.WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
        }
        // 完整解析后才会回复；帧截断会使此请求失败或共享 reader 退出。
        Assert.True((await client.CallAsync("daemon.ping").WaitAsync(TimeSpan.FromSeconds(3))).GetProperty("pong").GetBoolean());
        await ((GoProtocolTerminalSession)session).CleanupCompletion.WaitAsync(TimeSpan.FromSeconds(3));
        var output = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        sibling.OutputReceived += data => output.TrySetResult(data);
        await sibling.SendInputAsync(new byte[] { 65, 66 });
        Assert.Equal(new byte[] { 65, 66 }, await output.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        await session.DisposeAsync();
        Assert.Equal(1, disconnected);
        await sibling.DisposeAsync();
        stop.Cancel();
        await serving;
    }

    private static async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        uint next = 0;
        try
        {
            while (true)
            {
                var header = new byte[9];
                await stream.ReadExactlyAsync(header, ct);
                uint id = BinaryPrimitives.ReadUInt32BigEndian(header);
                byte type = header[4];
                var bytes = new byte[BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5))];
                await stream.ReadExactlyAsync(bytes, ct);
                if (type == 5)
                {
                    using var doc = JsonDocument.Parse(bytes);
                    var root = doc.RootElement;
                    string method = root.GetProperty("method").GetString()!;
                    object result = method switch
                    {
                        "telnet.connect" => new { sessionId = $"session-{++next}", streamId = next },
                        "daemon.ping" => new { pong = true },
                        _ => true
                    };
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result });
                }
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), (uint)bytes.Length);
                await stream.WriteAsync(header, ct);
                await stream.WriteAsync(bytes, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private sealed class GatedWrites(Stream inner, bool blockClose) : Stream
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            bool block = blockClose
                ? bytes.Length == 9 && bytes.Span[4] == 3
                : bytes.Length > 9 && System.Text.Encoding.UTF8.GetString(bytes.Span).Contains("protocol.disconnect", StringComparison.Ordinal);
            if (Armed && block)
            {
                Armed = false;
                Blocked.TrySetResult();
                await Release.Task;
            }
            await inner.WriteAsync(bytes, ct);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken ct = default) => inner.ReadAsync(bytes, ct);
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
