using System.IO.Ports;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

namespace Kei.Term.Infrastructure.Protocols;

public sealed class SerialTerminalSession : ITerminalSession, ISerialControl
{
    private readonly SerialOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Connection? _connection;
    private bool _disposed;

    public SerialTerminalSession(SerialOptions options) : this(Guid.NewGuid(), options)
    {
    }

    public SerialTerminalSession(Guid sessionId, SerialOptions options)
    {
        options.Validate();
        SessionId = sessionId;
        _options = options;
    }

    public Guid SessionId { get; }
    public bool CanSetRts => _options.FlowControl != SerialFlowControl.RtsCts;
    public TerminalSessionCapabilities Capabilities => TerminalSessionCapabilities.None;
    public bool IsConnected => Volatile.Read(ref _connection) is { } connection && !connection.Stop.IsCancellationRequested;
    public event Action<byte[]>? OutputReceived;
    public event Action<Exception?>? Disconnected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsConnected) throw new InvalidOperationException("串口已经连接。");
            if (_connection is { } previous) await previous.Completed.Task.ConfigureAwait(false);
            var port = CreatePort();
            try
            {
                // SerialPort 的同步打开也不占用 UI 线程；取消后必须先收回已打开的资源。
                await Task.Run(port.Open, CancellationToken.None).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                await Task.Run(port.Dispose).ConfigureAwait(false);
                if (ex is IOException or UnauthorizedAccessException)
                    throw ClassifyOpenFailure(ex);
                throw;
            }
            var connection = new Connection(port);
            Volatile.Write(ref _connection, connection);
            _ = Task.Factory.StartNew(() => ReadLoop(connection), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        finally { _lifecycle.Release(); }
    }

    public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var bytes = data.ToArray();
        return OperateAsync(port => port.Write(bytes, 0, bytes.Length), false, ct);
    }

    public Task SetDtrAsync(bool enabled, CancellationToken ct = default) =>
        OperateAsync(port => port.DtrEnable = enabled, true, ct);

    public Task SetRtsAsync(bool enabled, CancellationToken ct = default)
    {
        if (_options.FlowControl == SerialFlowControl.RtsCts)
            return Task.FromException(new InvalidOperationException("自动 RTS/CTS 已占用 RTS，不能手动操作。"));
        return OperateAsync(port => port.RtsEnable = enabled, true, ct);
    }

    public Task SetBreakStateAsync(bool enabled, CancellationToken ct = default) =>
        OperateAsync(port => port.BreakState = enabled, true, ct);

    // 不实现动态尺寸能力；兼容旧 ITerminalSession 的方法不能假装完成窗口协商。
    public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("串口不支持终端窗口尺寸协商。"));

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is not { } connection) return;
            connection.RequestStop(null);
            // 取消等待不取消资源回收，读线程仍负责关闭自己的这一代端口。
            await connection.Completed.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (_connection is not { } connection) return;
            connection.RequestStop(null);
            await connection.Completed.Task.ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task OperateAsync(Action<SerialPort> action, bool pinOperation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var connection = Volatile.Read(ref _connection);
        if (connection is null || connection.Stop.IsCancellationRequested)
            throw new InvalidOperationException("串口尚未连接。");
        await Task.Run(async () =>
        {
            await connection.Operations.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (connection.Stop.IsCancellationRequested) throw new InvalidOperationException("串口已经断开。");
                try { action(connection.Port); }
                // 不支持的引脚不能伪装为成功，也不代表字节流连接已经断开。
                catch (IOException ex) when (pinOperation && IsUnsupportedIoctl(ex))
                { throw new NotSupportedException("当前设备或驱动不支持此串口引脚操作。", ex); }
                // 正在执行的同步写最多等待 WriteTimeout；取消可发生在部分字节已发出之后。
                catch (TimeoutException) when (ct.IsCancellationRequested)
                { throw new OperationCanceledException(ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
                {
                    connection.RequestStop(ex);
                    throw;
                }
                ct.ThrowIfCancellationRequested();
            }
            finally { connection.Operations.Release(); }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private void ReadLoop(Connection connection)
    {
        var buffer = new byte[4096];
        try
        {
            while (!connection.Stop.IsCancellationRequested)
            {
                int count;
                try { count = connection.Port.Read(buffer, 0, buffer.Length); }
                catch (TimeoutException) { continue; }
                if (count == 0) throw new IOException("串口读取已结束。");
                if (!connection.Stop.IsCancellationRequested) OutputReceived?.Invoke(buffer[..count]);
            }
        }
        catch (Exception ex) { connection.RequestStop(ex); }
        finally
        {
            connection.RequestStop(null);
            connection.Operations.Wait();
            try { connection.Port.Dispose(); }
            catch (Exception ex) { connection.Error ??= ex; }
            finally { connection.Operations.Release(); }
            // 每一代只由其读线程发布一次通知；主动关闭先标记停止，不被随后读异常改写。
            try { Disconnected?.Invoke(connection.Error); }
            finally { connection.Completed.TrySetResult(); }
        }
    }

    private SerialPort CreatePort()
    {
        var port = new SerialPort(_options.PortName, _options.BaudRate, (Parity)_options.Parity,
            _options.DataBits, _options.StopBits switch
            {
                SerialStopBits.One => StopBits.One,
                SerialStopBits.Two => StopBits.Two,
                SerialStopBits.OnePointFive => StopBits.OnePointFive,
                _ => throw new ArgumentOutOfRangeException()
            })
        {
            ReadTimeout = _options.ReadTimeoutMilliseconds,
            WriteTimeout = _options.WriteTimeoutMilliseconds,
            Handshake = _options.FlowControl switch
            {
                SerialFlowControl.None => Handshake.None,
                SerialFlowControl.RtsCts => Handshake.RequestToSend,
                SerialFlowControl.XonXoff => Handshake.XOnXOff,
                _ => throw new ArgumentOutOfRangeException()
            }
        };
        if (_options.DtrEnable is { } dtr) port.DtrEnable = dtr;
        if (_options.RtsEnable is { } rts) port.RtsEnable = rts;
        return port;
    }

    // 不把 Unix EPERM(1) 误判成 Windows ERROR_INVALID_FUNCTION(1)。
    private static bool IsUnsupportedIoctl(IOException error) =>
        OperatingSystem.IsWindows()
            ? (error.HResult & 0xffff) is 1 or 50
            : (error.HResult & 0xffff) is 25 or 95 or 45;

    private SerialConnectionException ClassifyOpenFailure(Exception error)
    {
        var native = error;
        while (native.InnerException is { } inner) native = inner;
        var code = native.HResult & 0xffff;
        var failure = code switch
        {
            2 => SerialConnectionFailure.DeviceNotFound,
            3 when OperatingSystem.IsWindows() => SerialConnectionFailure.DeviceNotFound,
            6 or 19 when !OperatingSystem.IsWindows() => SerialConnectionFailure.DeviceNotFound,
            1 when !OperatingSystem.IsWindows() => SerialConnectionFailure.AccessDenied,
            13 when !OperatingSystem.IsWindows() => SerialConnectionFailure.AccessDenied,
            5 when OperatingSystem.IsWindows() => SerialConnectionFailure.AccessDenied,
            16 when !OperatingSystem.IsWindows() => SerialConnectionFailure.DeviceBusy,
            32 when OperatingSystem.IsWindows() => SerialConnectionFailure.DeviceBusy,
            _ => SerialConnectionFailure.IoFailure
        };
        return new SerialConnectionException(failure, _options.PortName, error);
    }

    private sealed class Connection(SerialPort port)
    {
        private readonly object _stopGate = new();
        public SerialPort Port { get; } = port;
        public CancellationTokenSource Stop { get; } = new();
        public SemaphoreSlim Operations { get; } = new(1, 1);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Error;

        public void RequestStop(Exception? error)
        {
            lock (_stopGate)
            {
                if (Stop.IsCancellationRequested) return;
                Error = error;
                Stop.Cancel();
            }
        }
    }
}
