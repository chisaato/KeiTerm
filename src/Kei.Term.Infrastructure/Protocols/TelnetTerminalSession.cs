using System.Net.Sockets;
using System.Text;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

namespace Kei.Term.Infrastructure.Protocols;

// 单次连接实例。重连应新建会话，避免旧世代输出进入新连接。
public sealed class TelnetTerminalSession : ITerminalSession
{
    private readonly TelnetConnectionOptions _options;
    private readonly TcpClient _client = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wire = new(1, 1);
    private readonly object _events = new();
    private readonly Queue<byte[]> _earlyOutput = new();
    private readonly bool[] _local = new bool[256];
    private readonly bool[] _remote = new bool[256];
    private readonly bool[] _localRejected = new bool[256];
    private readonly bool[] _remoteRejected = new bool[256];
    private readonly List<byte> _sub = new();
    private Action<byte[]>? _output;
    private Action<Exception?>? _disconnected;
    private Task? _reader;
    private Exception? _endError;
    private int _started, _ended, _disposed, _earlyBytes;
    private volatile bool _connected;
    private int _state;
    private byte _command;
    private bool _receiveCr;
    private int _columns = 80, _rows = 24;

    public TelnetTerminalSession(Guid sessionId, TelnetConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        if (options.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options.Port));
        if (options.Timeout <= TimeSpan.Zero || options.Timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options.Timeout));
        if (string.IsNullOrWhiteSpace(options.TerminalType) || options.TerminalType.Length > 255 ||
            options.TerminalType.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException("TerminalType must be printable ASCII (1-255 bytes).", nameof(options));
        SessionId = sessionId;
        _options = options;
    }

    public Guid SessionId { get; }
    public bool IsConnected => _connected;
    // CanResize 表示能接收尺寸更新；未协商/拒绝 NAWS 时仅缓存，接受后发送最新值。
    public TerminalSessionCapabilities Capabilities => new(CanResize: Volatile.Read(ref _ended) == 0);

    // 首次订阅前保留有界输出，避免工厂 ConnectAsync 后绑定视图时丢失欢迎语。
    public event Action<byte[]>? OutputReceived
    {
        add
        {
            lock (_events)
            {
                _output += value;
                if (_output is null) return;
                while (_earlyOutput.TryDequeue(out var data)) _output(data);
                _earlyBytes = 0;
            }
        }
        remove { lock (_events) _output -= value; }
    }

    public event Action<Exception?>? Disconnected
    {
        add
        {
            lock (_events)
            {
                _disconnected += value;
                if (_ended != 0) value?.Invoke(_endError);
            }
        }
        remove { lock (_events) _disconnected -= value; }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Create a new Telnet session to reconnect.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            await _client.ConnectAsync(_options.Host, _options.Port, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            lock (_events)
            {
                timeout.Token.ThrowIfCancellationRequested();
                _client.NoDelay = true;
                _connected = true;
                _reader = ReadLoopAsync();
            }
        }
        catch (Exception error)
        {
            End(error);
            throw;
        }
    }

    public async Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        List<byte>? echo = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _wire.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            var bytes = new List<byte>(data.Length * 2);
            for (var i = 0; i < data.Length; i++)
            {
                var b = data.Span[i];
                bytes.Add(b);
                if (b == 255) bytes.Add(255);
                // 每次输入立即完整发送，单独按 Enter 不能等待下次键盘输入。
                // 跨调用的 CR + LF 编为 CR NUL + LF，仍保持同样的终端字节语义。
                if (!_local[0] && b == 13 && (i + 1 == data.Length || data.Span[i + 1] != 10)) bytes.Add(0);
            }
            await WriteAsync(bytes.ToArray(), linked.Token).ConfigureAwait(false);
            // RFC 857：远端未承担 ECHO 时由客户端回显原始输入字节。
            // 这里只提供字节回显，不提供熟模式行编辑或密码提示识别。
            // 对端必须在密码提示前 WILL ECHO，才能禁止本地回显。
            if (!_remote[1]) echo = new List<byte>(data.ToArray());
        }
        finally { _wire.Release(); }
        if (echo is not null) Publish(echo);
    }

    public async Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default)
    {
        if (columns is < 1 or > 65535 || rows is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(columns));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _wire.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            _columns = columns;
            _rows = rows;
            if (_connected && _local[31]) await SendWindowAsync(linked.Token).ConfigureAwait(false);
        }
        finally { _wire.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            var buffer = new byte[8192];
            while (true)
            {
                var count = await _client.GetStream().ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (count == 0) break;
                var output = new List<byte>(count);
                await _wire.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    for (var i = 0; i < count; i++) await ParseAsync(buffer[i], output).ConfigureAwait(false);
                }
                finally { _wire.Release(); }
                Publish(output);
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or IOException or SocketException) { }
        catch (Exception error) { failure = error; }
        finally { End(failure); }
    }

    private async Task ParseAsync(byte b, List<byte> output)
    {
        switch (_state)
        {
            case 0:
                if (b == 255) _state = 1;
                else Data(b, output);
                break;
            case 1:
                if (b == 255) { Data(b, output); _state = 0; }
                else if (b is >= 251 and <= 254) { _command = b; _state = 2; }
                else if (b == 250) { _sub.Clear(); _state = 3; }
                else _state = 0;
                break;
            case 2:
                _state = 0;
                await NegotiateAsync(_command, b).ConfigureAwait(false);
                break;
            case 3:
                if (b == 255) _state = 4;
                else AddSub(b);
                break;
            case 4:
                if (b == 255) { AddSub(255); _state = 3; }
                else if (b == 240) { _state = 0; await SubnegotiateAsync().ConfigureAwait(false); }
                else _state = 3;
                break;
        }
    }

    private void Data(byte b, List<byte> output)
    {
        if (_receiveCr)
        {
            _receiveCr = false;
            if (b == 0) return;
        }
        output.Add(b);
        if (!_remote[0] && b == 13) _receiveCr = true;
    }

    private async Task NegotiateAsync(byte command, byte option)
    {
        // 不主动发起选项请求，因此不存在未决请求或交叉请求；每方向独立记录启用及拒绝状态。
        var local = command is 253 or 254;
        var positive = command is 251 or 253;
        var enabled = local ? _local : _remote;
        var rejected = local ? _localRejected : _remoteRejected;
        var supported = local ? option is 0 or 3 or 24 or 31 : option is 0 or 1 or 3;
        if (positive)
        {
            if (enabled[option]) return;
            if (!supported)
            {
                if (!rejected[option]) await ReplyAsync(local ? (byte)252 : (byte)254, option).ConfigureAwait(false);
                rejected[option] = true;
                return;
            }
            if (option == 0)
            {
                if (!local) _receiveCr = false;
            }
            enabled[option] = true;
            rejected[option] = false;
            await ReplyAsync(local ? (byte)251 : (byte)253, option).ConfigureAwait(false);
            if (local && option == 31) await SendWindowAsync(_stop.Token).ConfigureAwait(false);
        }
        else
        {
            rejected[option] = false;
            if (!enabled[option]) return;
            enabled[option] = false;
            await ReplyAsync(local ? (byte)252 : (byte)254, option).ConfigureAwait(false);
        }
    }

    private void AddSub(byte b)
    {
        if (_sub.Count >= 4096) throw new InvalidDataException("Telnet subnegotiation exceeds 4096 bytes.");
        _sub.Add(b);
    }

    private Task SubnegotiateAsync() => _sub.Count == 2 && _sub[0] == 24 && _sub[1] == 1 && _local[24]
        ? SendSubAsync(24, new byte[] { 0 }.Concat(Encoding.ASCII.GetBytes(_options.TerminalType)).ToArray(), _stop.Token)
        : Task.CompletedTask;

    private Task ReplyAsync(byte command, byte option) => WriteAsync(new byte[] { 255, command, option }, _stop.Token);

    private Task SendWindowAsync(CancellationToken ct) => SendSubAsync(31,
        new byte[] { (byte)(_columns >> 8), (byte)_columns, (byte)(_rows >> 8), (byte)_rows }, ct);

    private Task SendSubAsync(byte option, byte[] payload, CancellationToken ct)
    {
        var bytes = new List<byte> { 255, 250, option };
        foreach (var b in payload) { bytes.Add(b); if (b == 255) bytes.Add(255); }
        bytes.AddRange(new byte[] { 255, 240 });
        return WriteAsync(bytes.ToArray(), ct);
    }

    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        try { await _client.GetStream().WriteAsync(data, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
        {
            End(error);
            throw;
        }
    }

    private void EnsureConnected()
    {
        if (!_connected) throw new InvalidOperationException("Telnet session is not connected.");
    }

    private void Publish(List<byte> output)
    {
        if (output.Count == 0) return;
        lock (_events)
        {
            var data = output.ToArray();
            if (_output is not null) _output(data);
            else
            {
                if (_earlyBytes + data.Length > 1024 * 1024) throw new IOException("Unconsumed Telnet output exceeds 1 MiB.");
                _earlyBytes += data.Length;
                _earlyOutput.Enqueue(data);
            }
        }
    }

    private void End(Exception? error)
    {
        lock (_events)
        {
            if (_ended != 0) return;
            _connected = false;
            _endError = error;
            _ended = 1;
            _stop.Cancel();
            _client.Dispose();
            _disconnected?.Invoke(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        End(null);
        if (_reader is not null) await _reader.ConfigureAwait(false);
    }
}
