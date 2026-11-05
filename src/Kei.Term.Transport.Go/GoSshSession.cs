using Kei.Term.Core.Models;
using Kei.Term.Ssh.Abstractions;

namespace Kei.Term.Transport.Go;

public sealed class GoSshSession : ISshSession
{
    private readonly Func<CancellationToken, Task<(GoIpcClient Client, string Id, GoIpcStream Stream)>> _connect;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _stop = new();
    private GoIpcStream? _stream;
    private Task? _pump;
    private int _disposed;
    internal GoIpcClient Client { get; private set; } = null!;
    internal string RemoteId { get; private set; } = "";
    public Guid SessionId { get; }
    public bool IsConnected => _stream != null && Client.IsConnected && Volatile.Read(ref _disposed) == 0 && _pump?.IsCompleted == false;
    public object? UnderlyingClient => null;
    public event Action<byte[]>? OutputReceived;
    public event Action<Exception?>? Disconnected;
    internal GoSshSession(ResolvedSessionConfig config, Func<CancellationToken, Task<(GoIpcClient, string, GoIpcStream)>> connect)
    { SessionId = config.SessionId; _connect = connect; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (IsConnected) return;
            if (_stream != null) throw new IOException("Go 会话已断开，请创建新会话");
            (Client, RemoteId, _stream) = await _connect(ct);
            _pump = PumpAsync();
        }
        finally { _gate.Release(); }
    }
    private async Task PumpAsync()
    {
        Exception? failure = null;
        try
        {
            byte[] buffer = new byte[32768];
            int count;
            while ((count = await _stream!.ReadAsync(buffer, _stop.Token)) > 0) OutputReceived?.Invoke(buffer[..count]);
        }
        catch (Exception ex) { if (!_stop.IsCancellationRequested) failure = ex; }
        finally { Disconnected?.Invoke(failure); }
    }
    public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => IsConnected ? _stream!.WriteAsync(data, ct).AsTask() : Task.FromException(new IOException("Go 会话未连接"));
    public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        return IsConnected ? _stream!.ResizeAsync(columns, rows, ct) : Task.FromException(new IOException("Go 会话未连接"));
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        await _gate.WaitAsync();
        try
        {
            if (_stream != null)
            {
                if (Client.IsConnected)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await Client.CallAsync("session.disconnect", new { sessionId = RemoteId }, timeout.Token); }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
                }
                await _stream.DisposeAsync();
            }
            if (_pump != null) await _pump;
        }
        finally { _gate.Release(); _stop.Dispose(); }
    }
}
