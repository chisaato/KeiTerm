using Kei.Term.Core.Abstractions;

namespace Kei.Term.Transport.Go;

// 单连接拥有流，但绝不释放共享 IPC。Telnet 不宣称串口控制能力。
public class GoProtocolTerminalSession : ITerminalSession
{
    private readonly Func<CancellationToken, Task<GoIpcClient>> _provider;
    private readonly string _connectMethod;
    private readonly object _options;
    private readonly bool _canResize;
    private readonly object _events = new();
    private readonly Queue<byte[]> _early = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _resize = new(1, 1);
    private int _columns = 80, _rows = 24;
    private GoIpcClient? _client;
    private GoIpcStream? _stream;
    private string? _remoteId;
    private Task? _reader;
    private readonly object _cleanupGate = new();
    private Task? _remoteCleanup;
    private Task? _drainCleanup;
    internal Task CleanupCompletion { get { lock (_cleanupGate) return _drainCleanup ?? _remoteCleanup ?? Task.CompletedTask; } }
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(5);
    private Action<byte[]>? _output;
    private Action<Exception?>? _disconnected;
    private Exception? _error;
    private int _started, _ended, _disposed, _earlyBytes;
    private volatile bool _connected;

    internal GoProtocolTerminalSession(Guid id, Func<CancellationToken, Task<GoIpcClient>> provider,
        string connectMethod, object options, bool canResize)
        => (SessionId, _provider, _connectMethod, _options, _canResize) = (id, provider, connectMethod, options, canResize);

    public Guid SessionId { get; }
    public bool IsConnected => _connected;
    public TerminalSessionCapabilities Capabilities => new(CanResize: _canResize && Volatile.Read(ref _ended) == 0 && Volatile.Read(ref _disposed) == 0);
    public event Action<byte[]>? OutputReceived
    {
        add { lock (_events) { _output += value; if (_output is null) return; while (_early.TryDequeue(out var data)) _output(data); _earlyBytes = 0; } }
        remove { lock (_events) _output -= value; }
    }
    public event Action<Exception?>? Disconnected
    {
        add { lock (_events) { _disconnected += value; if (_ended != 0) value?.Invoke(_error); } }
        remove { lock (_events) _disconnected -= value; }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Create a new protocol session to reconnect.");
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try
        {
            _client = await _provider(opening.Token).ConfigureAwait(false);
            // 取消只结束当前调用；晚到的连接响应单独回收，不终止共享 IPC。
            opening.Token.ThrowIfCancellationRequested();
            var connecting = _client.CallAsync(_connectMethod, _options);
            System.Text.Json.JsonElement response;
            try { response = await connecting.WaitAsync(opening.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _ = ReleaseLateConnectionAsync(_client, connecting);
                throw;
            }
            bool abandoned;
            lock (_cleanupGate)
            {
                abandoned = _disposed != 0 || opening.IsCancellationRequested;
                if (!abandoned)
                {
                    _remoteId = response.GetProperty("sessionId").GetString()!;
                    _stream = _client.GetStream(response.GetProperty("streamId").GetUInt32());
                }
            }
            if (abandoned)
            {
                _ = ReleaseLateConnectionAsync(_client, Task.FromResult(response));
                throw new OperationCanceledException(opening.Token);
            }
            opening.Token.ThrowIfCancellationRequested();
            await _resize.WaitAsync(opening.Token).ConfigureAwait(false);
            try
            {
                if (_canResize)
                    await _client.CallAsync("protocol.resize", new { sessionId = _remoteId, cols = _columns, rows = _rows }, opening.Token).ConfigureAwait(false);
                lock (_events)
                {
                    opening.Token.ThrowIfCancellationRequested();
                    _connected = true;
                    _reader = ReadLoopAsync();
                }
            }
            finally { _resize.Release(); }
        }
        catch (Exception error)
        {
            End(error);
            await ReleaseRemoteAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_connected || _stream is null) throw new InvalidOperationException("Protocol session is not connected.");
        await _stream.WriteAsync(data, ct).ConfigureAwait(false);
    }

    public async Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default)
    {
        if (!_canResize) throw new NotSupportedException("Serial does not support terminal resize.");
        if (columns is < 1 or > 65535 || rows is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(columns));
        await _resize.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ended != 0 || _disposed != 0) throw new InvalidOperationException("Protocol session has ended.");
            _columns = columns;
            _rows = rows;
            // Endpoint 先 Attach 再 Connect；连接前同样接受尺寸，NAWS 在 Go 内决定是否上 wire。
            if (_connected)
                await CallControlAsync("protocol.resize", new { sessionId = _remoteId, cols = columns, rows }, ct).ConfigureAwait(false);
        }
        finally { _resize.Release(); }
    }

    protected async Task CallControlAsync(string method, object? args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_connected || _client is null) throw new InvalidOperationException("Protocol session is not connected.");
        await _client.CallAsync(method, args, ct).ConfigureAwait(false);
    }
    protected Task SetPinAsync(string method, bool enabled, CancellationToken ct)
        => CallControlAsync(method, new { sessionId = _remoteId, enabled }, ct);

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            var bytes = new byte[8192];
            while (true)
            {
                int count = await _stream!.ReadAsync(bytes, _stop.Token).ConfigureAwait(false);
                if (count == 0) break;
                var data = bytes.AsSpan(0, count).ToArray();
                lock (_events)
                {
                    if (_output is not null) _output(data);
                    else
                    {
                        if (_earlyBytes + count > 1024 * 1024) throw new IOException("Unconsumed protocol output exceeds 1 MiB.");
                        _earlyBytes += count;
                        _early.Enqueue(data);
                    }
                }
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or IOException) { }
        catch (Exception ex) { error = ex; }
        finally { End(error); await ReleaseRemoteAsync().ConfigureAwait(false); }
    }

    private void End(Exception? error)
    {
        lock (_events)
        {
            if (_ended != 0) return;
            _ended = 1;
            _connected = false;
            _error = error;
            _stop.Cancel();
            _disconnected?.Invoke(error);
        }
    }

    private async Task ReleaseRemoteAsync()
    {
        try { await GetRemoteCleanup().WaitAsync(CleanupBudget).ConfigureAwait(false); }
        catch (TimeoutException) { _client?.Report("protocol-cleanup-timeout", reason: "cleanup-budget"); }
    }

    private Task GetRemoteCleanup()
    {
        lock (_cleanupGate) return _remoteCleanup ??= ReleaseRemoteCoreAsync();
    }

    // 此任务拥有晚完成的清理；只在帧边界取消 RPC，绝不截断共享写帧。
    // 所有异常在这里被消费，超出前台预算后仍会安全完成 Close。
    private async Task ReleaseRemoteCoreAsync()
    {
        string? id = Interlocked.Exchange(ref _remoteId, null);
        if (id is not null && _client?.IsConnected == true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _client.CallAsync("protocol.disconnect", new { sessionId = id }, timeout.Token).ConfigureAwait(false); }
            catch (Exception) { /* 结束状态已经传播；共享 IPC 仍由进程管理器拥有。 */ }
        }
        if (_stream is not null)
        {
            try { await _stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* 单连接回收失败不关闭共享 IPC。 */ }
        }
    }

    private static async Task ReleaseLateConnectionAsync(GoIpcClient client, Task<System.Text.Json.JsonElement> connecting)
    {
        try
        {
            var response = await connecting.ConfigureAwait(false);
            var stream = client.GetStream(response.GetProperty("streamId").GetUInt32());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await client.CallAsync("protocol.disconnect", new { sessionId = response.GetProperty("sessionId").GetString() }, timeout.Token).ConfigureAwait(false); }
            finally { await stream.DisposeAsync().ConfigureAwait(false); }
        }
        catch (Exception) { /* 连接取消后的独立清理不得影响其他会话。 */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        End(null);
        // 一个总预算覆盖 disconnect RPC、流 Close 及 reader 排水，而不是每步重新计时。
        Task cleanup;
        lock (_cleanupGate) cleanup = _drainCleanup = DrainAsync();
        _ = ObserveCleanupAsync(cleanup);
        try { await cleanup.WaitAsync(CleanupBudget).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _client?.Report("protocol-cleanup-timeout", reason: "dispose-budget");
            throw new TimeoutException("协议连接清理超出 5 秒预算，后台仍在安全回收；共享 IPC 未关闭。");
        }
    }

    private async Task DrainAsync()
    {
        await GetRemoteCleanup().ConfigureAwait(false);
        if (_reader is not null) await _reader.ConfigureAwait(false);
    }

    private static async Task ObserveCleanupAsync(Task cleanup)
    {
        try { await cleanup.ConfigureAwait(false); }
        catch (Exception) { /* 预算到期后晚完成的任务也必须被观察。 */ }
    }
}

internal sealed class GoSerialTerminalSession(Guid id, Func<CancellationToken, Task<GoIpcClient>> provider, object options, bool canSetRts)
    : GoProtocolTerminalSession(id, provider, "serial.connect", options, canResize: false), ISerialControl
{
    public bool CanSetRts => canSetRts;
    public Task SetDtrAsync(bool enabled, CancellationToken ct = default) => SetPinAsync("serial.setDtr", enabled, ct);
    public Task SetRtsAsync(bool enabled, CancellationToken ct = default) => SetPinAsync("serial.setRts", enabled, ct);
    public Task SetBreakStateAsync(bool enabled, CancellationToken ct = default) => SetPinAsync("serial.setBreak", enabled, ct);
    public Task DisconnectAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return DisposeAsync().AsTask(); }
}
