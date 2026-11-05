using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Kei.Term.Transport.Go;

public sealed class GoRpcException(int code, string message) : IOException(message)
{
    public int Code { get; } = code;
}

// 单个读取循环分发所有响应；流队列有界，不让未消费流堵住控制面。
public sealed class GoIpcClient : IAsyncDisposable
{
    public const int MaxPayload = 4 << 20;
    private readonly Stream _connection;
    private readonly SemaphoreSlim _write = new(1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<uint, GoIpcStream> _streams = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private long _nextId;
    private int _disposed;
    public bool IsConnected => !_reader.IsCompleted && Volatile.Read(ref _disposed) == 0;

    public GoIpcClient(Stream connection)
    {
        _connection = connection;
        _reader = ReadLoopAsync();
    }

    public static async Task<GoIpcClient> ConnectAsync(string endpoint, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", endpoint.StartsWith(@"\\.\pipe\") ? endpoint[9..] : endpoint,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(ct); return new(pipe); }
            catch { await pipe.DisposeAsync(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), ct);
            return new(new NetworkStream(socket, ownsSocket: true));
        }
        catch { socket.Dispose(); throw; }
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(!IsConnected, this);
        long id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await SendFrameAsync(0, 5, JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters }), ct);
            return await completion.Task.WaitAsync(ct);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public GoIpcStream GetStream(uint id) => _streams.TryGetValue(id, out var stream)
        ? stream : throw new IOException("Sidecar 返回了未知流");

    internal async Task SendFrameAsync(uint id, byte type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxPayload) throw new ArgumentOutOfRangeException(nameof(payload));
        await _write.WaitAsync(ct);
        try
        {
            byte[] header = new byte[9];
            BinaryPrimitives.WriteUInt32BigEndian(header, id);
            header[4] = type;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), (uint)payload.Length);
            // 开始发送后必须完整写完帧；取消只能发生在帧边界，否则会破坏其他流。
            await _connection.WriteAsync(header, _stop.Token);
            await _connection.WriteAsync(payload, _stop.Token);
            await _connection.FlushAsync(_stop.Token);
        }
        catch
        {
            _stop.Cancel();
            _connection.Dispose();
            throw;
        }
        finally { _write.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        Exception failure = new IOException("Sidecar 已断开");
        try
        {
            byte[] header = new byte[9];
            while (true)
            {
                await _connection.ReadExactlyAsync(header, _stop.Token);
                uint id = BinaryPrimitives.ReadUInt32BigEndian(header);
                byte type = header[4];
                uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5));
                if (length > MaxPayload || type is < 1 or > 5) throw new IOException("无效 IPC 帧头");
                byte[] payload = new byte[(int)length];
                await _connection.ReadExactlyAsync(payload, _stop.Token);
                if (type == 5)
                {
                    if (id != 0) throw new IOException("JSON-RPC 必须使用流零");
                    using var document = JsonDocument.Parse(payload);
                    var root = document.RootElement;
                    if (root.GetProperty("jsonrpc").GetString() != "2.0") throw new IOException("无效 JSON-RPC 版本");
                    if (!root.TryGetProperty("id", out var rpcId) || !rpcId.TryGetInt64(out long requestId)) continue;
                    if (root.TryGetProperty("error", out var error))
                    {
                        if (_pending.TryGetValue(requestId, out var rejected))
                            rejected.TrySetException(new GoRpcException(error.GetProperty("code").GetInt32(), error.GetProperty("message").GetString() ?? "RPC 失败"));
                        continue;
                    }
                    var result = root.GetProperty("result").Clone();
                    // 注册必须先于唤醒调用方及读取后续数据帧，避免快速输出丢失。
                    if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("streamId", out var streamId))
                        _streams.TryAdd(streamId.GetUInt32(), new GoIpcStream(this, streamId.GetUInt32()));
                    if (_pending.TryGetValue(requestId, out var completed)) completed.TrySetResult(result);
                }
                else if (_streams.TryGetValue(id, out var stream)) stream.Receive(type, payload);
                else throw new IOException("收到未知流的数据");
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _connection.Dispose();
            foreach (var pending in _pending.Values) pending.TrySetException(failure);
            foreach (var stream in _streams.Values) stream.Abort(failure);
        }
    }

    internal void Forget(uint id) => _streams.TryRemove(id, out _);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        await _connection.DisposeAsync();
        await _reader;
        _stop.Dispose();
    }
}

public sealed class GoIpcStream : Stream
{
    private readonly GoIpcClient _client;
    private readonly uint _id;
    private readonly Channel<byte[]> _input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _current;
    private int _offset;
    private int _closed;
    private int _disposed;
    private int _ended;
    private int _buffered;
    internal GoIpcStream(GoIpcClient client, uint id) { _client = client; _id = id; }
    internal void Receive(byte type, byte[] payload)
    {
        if (type == 1)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (Interlocked.Add(ref _buffered, payload.Length) > 4 * GoIpcClient.MaxPayload || !_input.Writer.TryWrite(payload))
                Abort(new IOException("IPC 流接收缓冲区已满"));
        }
        else if (type == 3)
        {
            Interlocked.Exchange(ref _ended, 1);
            _input.Writer.TryComplete();
            _finished.TrySetResult();
            if (Volatile.Read(ref _disposed) != 0) _client.Forget(_id);
        }
        else if (type == 4) Abort(new IOException(Encoding.UTF8.GetString(payload)));
        else Abort(new IOException("无效数据流帧"));
    }
    internal void Abort(Exception error) { _input.Writer.TryComplete(error); _finished.TrySetException(error); }
    public Task ResizeAsync(int columns, int rows, CancellationToken ct = default)
        => _client.SendFrameAsync(_id, 2, JsonSerializer.SerializeToUtf8Bytes(new { cols = columns, rows }), ct);
    public async Task CompleteWriteAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) await _client.SendFrameAsync(_id, 3, ReadOnlyMemory<byte>.Empty, ct);
        await _finished.Task.WaitAsync(ct);
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.Length == 0) return 0;
        while (_current == null || _offset == _current.Length)
        {
            if (!await _input.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (!_input.Reader.TryRead(out _current)) continue;
            Interlocked.Add(ref _buffered, -_current.Length);
            _offset = 0;
        }
        int count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _closed) != 0) throw new IOException("流写入已结束");
        while (!buffer.IsEmpty)
        {
            int count = Math.Min(buffer.Length, 32 << 10);
            await _client.SendFrameAsync(_id, 1, buffer[..count], cancellationToken);
            buffer = buffer[count..];
        }
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0 && _client.IsConnected)
                await _client.SendFrameAsync(_id, 3, ReadOnlyMemory<byte>.Empty, default);
        }
        finally
        {
            _input.Writer.TryComplete();
            if (Volatile.Read(ref _ended) != 0) _client.Forget(_id);
            GC.SuppressFinalize(this);
        }
        // 保留接收端直到对端 Close，允许晚到的完成确认和错误被正确处理。
    }
    protected override void Dispose(bool disposing) { if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult(); base.Dispose(disposing); }
    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanWrite => CanRead && Volatile.Read(ref _closed) == 0;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
