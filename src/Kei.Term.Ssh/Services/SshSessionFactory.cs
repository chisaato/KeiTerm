namespace Kei.Term.Ssh.Services;

using System.Text;
using System.Threading.Channels;
using Renci.SshNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Ssh.Abstractions;

public class SshSessionFactory : ISshSessionFactory
{
    private readonly ILogger<SshSessionFactory> _logger;

    public SshSessionFactory(ILogger<SshSessionFactory>? logger = null)
    {
        _logger = logger ?? NullLogger<SshSessionFactory>.Instance;
    }

    public Task<ISshSession> CreateSessionAsync(
        ResolvedSessionConfig config,
        IReadOnlyList<MaterializedAuthMethod> methods,
        SshConnectOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= SshConnectOptions.Default;
        SshDialer dialer = CreateDialer(config, methods, options, borrowedChain: null);
        var session = new SshNetSession(config.SessionId, dialer, config.TerminalType, config.StartupScript, _logger);
        return Task.FromResult<ISshSession>(session);
    }

    public Task<IRemoteFileSystem> CreateFileSystemAsync(
        ResolvedSessionConfig config,
        IReadOnlyList<MaterializedAuthMethod> methods,
        ISshSession? activeSession = null,
        SshConnectOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= SshConnectOptions.Default;

        // 终端会话已建立跳板链时直接借用：文件通道不再重复登录每一跳
        SshJumpChain? borrowed = (activeSession as SshNetSession)?.JumpChain;
        SshDialer dialer = CreateDialer(config, methods, options, borrowed);

        IRemoteFileSystem fileSystem = config.FileTransferProtocol == FileTransferProtocol.Scp
            ? new ScpRemoteFileSystem(dialer, _logger)
            : new SftpRemoteFileSystem(dialer, config.SftpMode, _logger);
        return Task.FromResult(fileSystem);
    }

    private SshDialer CreateDialer(
        ResolvedSessionConfig config,
        IReadOnlyList<MaterializedAuthMethod> methods,
        SshConnectOptions options,
        SshJumpChain? borrowedChain)
    {
        SshTarget target = BuildTarget(
            config,
            methods,
            options,
            options.InteractivePrompt,
            options.Socks5Host,
            options.Socks5Port,
            options.Socks5Username,
            options.Socks5Password);

        // 借用现成链时无需再构建各跳认证
        List<SshTarget> hops = borrowedChain != null
            ? []
            : options.JumpHosts.Select(h => BuildTarget(
                h.Config,
                h.Methods,
                options,
                options.InteractivePrompt,
                h.Socks5Host,
                h.Socks5Port,
                h.Socks5Username,
                h.Socks5Password)).ToList();

        var clientOptions = new SshClientOptions(options.ConnectTimeout, options.KeepAliveInterval, options.HostKeyVerifier);
        return new SshDialer(target, hops, borrowedChain, clientOptions, _logger, options.PortForwards);
    }

    private SshTarget BuildTarget(
        ResolvedSessionConfig config,
        IReadOnlyList<MaterializedAuthMethod> methods,
        SshConnectOptions options,
        Func<string, Task<string?>>? interactivePrompt,
        string? socks5Host = null,
        int socks5Port = 0,
        string? socks5Username = null,
        string? socks5Password = null)
    {
        AuthenticationMethod[] authMethods = SshAuthMethodBuilder.Build(
            config.Username,
            methods,
            interactivePrompt,
            options.AgentSocketPath,
            _logger);

        // 零认证方法无法连接：提前失败，与 SSH.NET ConnectionInfo 的约束一致
        if (authMethods.Length == 0)
        {
            throw new ArgumentException($"没有可用的认证方法: {config.Username}@{config.Host}:{config.Port}", nameof(methods));
        }

        // 仅记录注册的认证方法类型（不记录任何凭据值）
        _logger.LogInformation(
            "注册 SSH 认证方法 host={Host}:{Port} 用户名={Username} 数量={Count} 类型={Types}",
            config.Host,
            config.Port,
            config.Username,
            authMethods.Length,
            string.Join(",", authMethods.Select(m => m.GetType().Name)));

        return new SshTarget(
            config.Host,
            config.Port,
            config.Username,
            authMethods,
            socks5Host,
            socks5Port,
            socks5Username,
            socks5Password);
    }
}

public class SshNetSession : ISshSession
{
    private readonly SshDialer _dialer;
    private readonly string _terminalType;
    private readonly string? _startupScript;
    private readonly ILogger _logger;
    private SshClient? _client;
    private ShellStream? _shellStream;
    private CancellationTokenSource? _readCts;
    private Task? _readLoopTask;

    // 输入发送队列：ShellStream 的 Write/Flush 非线程安全，且其 FlushAsync 沿用 Stream 基类实现（在线程池上执行 Flush）。
    // 连续发送时多个 Flush 会并发读写同一写缓冲区，造成字节重复或丢失——
    // 实测 tmux 收到重复的终端查询应答，把第二份当按键转进了面板。所有输入经单消费者队列按序写出。
    private readonly Channel<PendingInput> _inputQueue =
        Channel.CreateUnbounded<PendingInput>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _writeLoopTask;

    private sealed record PendingInput(byte[] Data, TaskCompletionSource Sent);

    // 释放标志：0 = 未释放，1 = 已释放（幂等 + 线程安全）
    private int _disposed;

    public Guid SessionId { get; }

    // 获取底层 SshClient（连接前为 null）
    public object? UnderlyingClient => _client;

    // 本会话注册的认证方法（诊断/测试用，不含明文材料的读取入口）
    public IReadOnlyList<AuthenticationMethod> AuthenticationMethods => _dialer.Target.AuthMethods;

    // 跳板链（直连为 null），供文件通道借用
    internal SshJumpChain? JumpChain => _dialer.Chain;

    public IReadOnlyList<string> ForwardStartErrors => _dialer.ForwardStartErrors;

    // 已释放后直接返回 false，避免 getter 访问已释放的 SshClient 抛 ObjectDisposedException
    public bool IsConnected => _disposed == 0 && _client is { IsConnected: true } && _shellStream != null;

    public event Action<byte[]>? OutputReceived;
    public event Action<Exception?>? Disconnected;

    // 直连兼容构造：沿用既有 ConnectionInfo（不做主机密钥校验、无跳板）
    public SshNetSession(Guid sessionId, ConnectionInfo connectionInfo, string terminalType, ILogger? logger = null)
        : this(
            sessionId,
            new SshDialer(
                new SshTarget(connectionInfo.Host, connectionInfo.Port, connectionInfo.Username, connectionInfo.AuthenticationMethods.ToArray()),
                [],
                null,
                new SshClientOptions(connectionInfo.Timeout, TimeSpan.Zero, null),
                logger ?? NullLogger.Instance),
            terminalType,
            null,
            logger)
    {
    }

    internal SshNetSession(Guid sessionId, SshDialer dialer, string terminalType, string? startupScript, ILogger? logger = null)
    {
        SessionId = sessionId;
        _dialer = dialer;
        _terminalType = terminalType;
        _startupScript = startupScript;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        _client = await _dialer.ConnectClientAsync(info => new SshClient(info), ct);
        _logger.LogInformation(
            "SSH 会话已连接 SessionId={SessionId} 经跳板={ViaJump}",
            SessionId,
            _dialer.UsesJumpHosts);

        // 初始化 PTY 尺寸默认为 80x24，由 UI 端挂载后自动触发真实尺寸的 Resize
        _shellStream = _client.CreateShellStream(_terminalType, 80, 24, 800, 600, 1024);

        _readCts = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readCts.Token));
        _writeLoopTask = Task.Run(() => WriteLoopAsync(_readCts.Token));

        await SendStartupScriptAsync(ct);
    }

    // 登录后自动执行：逐行以回车提交（PTY 会把 \r 转成换行），由远端 shell 自行缓冲
    private async Task SendStartupScriptAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_startupScript))
        {
            return;
        }

        string normalized = _startupScript.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        string payload = string.Join('\r', normalized.Split('\n')) + "\r";
        await SendInputAsync(Encoding.UTF8.GetBytes(payload), ct);
        _logger.LogInformation("已发送登录脚本 SessionId={SessionId} 行数={Lines}", SessionId, normalized.Split('\n').Length);
    }

    // 入队即返回的任务在数据真正写出后完成；调用方可不等待（键盘输入），顺序由队列保证
    public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_shellStream == null || !IsConnected || data.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var pending = new PendingInput(data.ToArray(), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_inputQueue.Writer.TryWrite(pending))
        {
            // 会话释放中：队列已关闭
            return Task.CompletedTask;
        }

        return pending.Sent.Task.WaitAsync(ct);
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (PendingInput input in _inputQueue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    ShellStream stream = _shellStream!;
                    stream.Write(input.Data);
                    stream.Flush();
                }
                catch (Exception ex)
                {
                    // 写失败即连接已坏，断开由读取循环上报；这里不把异常抛给多为"发出即不管"的调用方（避免未观察异常）
                    _logger.LogWarning(ex, "SSH 输入写出失败 SessionId={SessionId} 字节数={Bytes}", SessionId, input.Data.Length);
                }

                input.Sent.TrySetResult();
            }
        }
        catch (OperationCanceledException)
        {
            // 释放
        }

        // 释放后仍在队列中的输入不再发送
        while (_inputQueue.Reader.TryRead(out PendingInput? left))
        {
            left.Sent.TrySetCanceled();
        }
    }

    public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default)
    {
        if (_shellStream != null && IsConnected)
        {
            try
            {
                _shellStream.ChangeWindowSize((uint)columns, (uint)rows, (uint)widthPx, (uint)heightPx);
            }
            catch
            {
                // 忽略 resize 异常
            }
        }

        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && _shellStream != null && _client is { IsConnected: true })
            {
                int read = await _shellStream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read <= 0)
                {
                    break;
                }

                var chunk = new byte[read];
                Array.Copy(buffer, 0, chunk, 0, read);
                OutputReceived?.Invoke(chunk);
            }
            Disconnected?.Invoke(null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "SSH 会话读取循环异常 SessionId={SessionId}", SessionId);
            Disconnected?.Invoke(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 幂等保护：重复或并发释放只执行一次清理
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // 1) 关闭输入队列并发出取消信号
        _inputQueue.Writer.TryComplete();
        _readCts?.Cancel();

        // 2) 立即释放 ShellStream 与断开底层连接，打断阻塞在 ReadAsync 上的底层网络读取
        try
        {
            _shellStream?.Dispose();
        }
        catch
        {
            // 释放路径防御：忽略流关闭异常
        }

        SshClient? client = _client;
        if (client != null)
        {
            // SSH.NET 的 BaseClient 属性访问器会 CheckDisposed，异常一律视为未连接
            bool connected;
            try
            {
                connected = client.IsConnected;
            }
            catch
            {
                connected = false;
            }

            if (connected)
            {
                try
                {
                    client.Disconnect();
                }
                catch
                {
                    // 释放路径的防御：断开连接时的网络异常吞掉，不影响释放
                }
            }
        }

        // 3) 等待读写循环退出，设置 500ms 超时保护避免极端情况下死等挂起
        if (_readLoopTask != null || _writeLoopTask != null)
        {
            try
            {
                await Task.WhenAny(
                    Task.WhenAll(_readLoopTask ?? Task.CompletedTask, _writeLoopTask ?? Task.CompletedTask),
                    Task.Delay(500));
            }
            catch
            {
                // 忽略等待退出时的异常
            }
        }

        try
        {
            client?.Dispose();
        }
        catch
        {
            // 释放路径的防御：重复 Dispose 时的异常吞掉，保证释放路径绝不抛
        }

        // 4) 目标连接断开后再拆跳板链
        await _dialer.DisposeAsync();

        _readCts?.Dispose();
        _logger.LogInformation("SSH 会话已释放 SessionId={SessionId}", SessionId);
    }
}
