namespace Kei.Term.Ssh.Services;

using Renci.SshNet;
using Renci.SshNet.Common;
using SshNet.Agent;
using System.Text;
using System.Threading;
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
        TimeSpan? connectTimeout = null,
        Func<string, Task<string?>>? interactivePrompt = null,
        CancellationToken ct = default)
    {
        var authMethods = new List<AuthenticationMethod>();
        string? fallbackPassword = null;

        // 按物化顺序注册，顺序即认证尝试优先级
        foreach (var material in methods ?? [])
        {
            switch (material.Kind)
            {
                case AuthMaterialKind.Password:
                    if (string.IsNullOrEmpty(material.Secret?.Password))
                    {
                        break;
                    }

                    // 首个密码作为 keyboard-interactive 的预收集应答
                    fallbackPassword ??= material.Secret.Password;
                    authMethods.Add(new PasswordAuthenticationMethod(config.Username, material.Secret.Password));
                    break;

                case AuthMaterialKind.PrivateKey:
                    if (string.IsNullOrEmpty(material.Secret?.PrivateKeyContent))
                    {
                        break;
                    }

                    using (var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(material.Secret.PrivateKeyContent)))
                    {
                        var keyFile = string.IsNullOrEmpty(material.Secret.Passphrase)
                            ? new PrivateKeyFile(keyStream)
                            : new PrivateKeyFile(keyStream, material.Secret.Passphrase);

                        authMethods.Add(new PrivateKeyAuthenticationMethod(config.Username, keyFile));
                    }

                    break;

                case AuthMaterialKind.Agent:
                    // Agent 全量身份：一期 SshNet.Agent 无法对 SK/FIDO 身份取公钥算指纹
                    // （SshAgentPrivateKey.Key 对 sk-* 为 null），故 Fingerprint 字段预留、暂不过滤
                    try
                    {
                        var agent = new SshAgent();
                        var identities = agent.RequestIdentities();
                        if (identities.Any())
                        {
                            authMethods.Add(new PrivateKeyAuthenticationMethod(config.Username, identities.ToArray()));
                        }
                    }
                    catch
                    {
                        // Agent 不可用或未启动时忽略，继续后续方法
                    }

                    break;
            }
        }

        // 任一密码材料或存在交互回调 → 注册 keyboard-interactive
        // （纯交互式认证：零密码材料 + interactivePrompt 也必须注册，否则 KI 永远不触发）
        var hasPassword = methods?.Any(m => m.Kind == AuthMaterialKind.Password
                                           && !string.IsNullOrEmpty(m.Secret?.Password)) == true;
        if (hasPassword || interactivePrompt != null)
        {
            var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(config.Username);
            keyboardInteractive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    if (interactivePrompt != null)
                    {
                        // 注意：连接必须运行于非 UI 线程；事件处理器同步阻塞等待 UI 经 Dispatcher 回传输入
                        prompt.Response = interactivePrompt(prompt.Request).GetAwaiter().GetResult() ?? string.Empty;
                    }
                    else
                    {
                        // 无交互回调时用预收集密码应答全部提示，避免无 UI 环境等待输入造成死锁
                        prompt.Response = fallbackPassword ?? string.Empty;
                    }
                }
            };
            authMethods.Add(keyboardInteractive);
        }

        var connectionInfo = new ConnectionInfo(
            config.Host,
            config.Port,
            config.Username,
            authMethods.ToArray()
        );

        // 仅记录注册的认证方法类型（不记录任何凭据值）
        _logger.LogInformation(
            "注册 SSH 认证方法 host={Host}:{Port} 用户名={Username} 数量={Count} 类型={Types}",
            config.Host,
            config.Port,
            config.Username,
            authMethods.Count,
            string.Join(",", authMethods.Select(m => m.GetType().Name)));

        // 认证/连接超时，未指定时默认 15 秒
        connectionInfo.Timeout = connectTimeout ?? TimeSpan.FromSeconds(15);

        var session = new SshNetSession(config.SessionId, connectionInfo, config.TerminalType, _logger);
        return Task.FromResult<ISshSession>(session);
    }

    public Task<IRemoteFileSystem> CreateFileSystemAsync(
        ResolvedSessionConfig config,
        IReadOnlyList<MaterializedAuthMethod> methods,
        ISshSession? activeSession = null,
        TimeSpan? connectTimeout = null,
        CancellationToken ct = default)
    {
        var authMethods = new List<AuthenticationMethod>();
        foreach (var material in methods ?? [])
        {
            switch (material.Kind)
            {
                case AuthMaterialKind.Password:
                    if (!string.IsNullOrEmpty(material.Secret?.Password))
                    {
                        authMethods.Add(new PasswordAuthenticationMethod(config.Username, material.Secret.Password));
                    }
                    break;

                case AuthMaterialKind.PrivateKey:
                    if (!string.IsNullOrEmpty(material.Secret?.PrivateKeyContent))
                    {
                        using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(material.Secret.PrivateKeyContent));
                        var keyFile = string.IsNullOrEmpty(material.Secret.Passphrase)
                            ? new PrivateKeyFile(keyStream)
                            : new PrivateKeyFile(keyStream, material.Secret.Passphrase);
                        authMethods.Add(new PrivateKeyAuthenticationMethod(config.Username, keyFile));
                    }
                    break;

                case AuthMaterialKind.Agent:
                    try
                    {
                        var agent = new SshAgent();
                        var identities = agent.RequestIdentities();
                        if (identities.Any())
                        {
                            authMethods.Add(new PrivateKeyAuthenticationMethod(config.Username, identities.ToArray()));
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                    break;
            }
        }

        var connectionInfo = new ConnectionInfo(config.Host, config.Port, config.Username, authMethods.ToArray())
        {
            Timeout = connectTimeout ?? TimeSpan.FromSeconds(15)
        };

        IRemoteFileSystem fileSystem;
        if (config.FileTransferProtocol == FileTransferProtocol.Scp)
        {
            fileSystem = new ScpRemoteFileSystem(connectionInfo, _logger);
        }
        else
        {
            fileSystem = new SftpRemoteFileSystem(connectionInfo, config.SftpMode, activeSession, _logger);
        }

        return Task.FromResult(fileSystem);
    }
}

public class SshNetSession : ISshSession
{
    private readonly SshClient _client;
    private readonly string _terminalType;
    private readonly ILogger _logger;
    private ShellStream? _shellStream;
    private CancellationTokenSource? _readCts;
    private Task? _readLoopTask;

    // 释放标志：0 = 未释放，1 = 已释放（幂等 + 线程安全）
    private int _disposed;

    public Guid SessionId { get; }

    // 获取底层 SshClient，供 Subsystem 多路复用通道使用
    public object? UnderlyingClient => _client;

    // 已释放后直接返回 false，避免 getter 访问已释放的 SshClient 抛 ObjectDisposedException
    public bool IsConnected => _disposed != 0 ? false : _client.IsConnected && _shellStream != null;

    public event Action<byte[]>? OutputReceived;
    public event Action<Exception?>? Disconnected;

    public SshNetSession(Guid sessionId, ConnectionInfo connectionInfo, string terminalType, ILogger? logger = null)
    {
        SessionId = sessionId;
        _terminalType = terminalType;
        _logger = logger ?? NullLogger.Instance;
        _client = new SshClient(connectionInfo);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _client.ConnectAsync(ct);
        _logger.LogInformation("SSH 会话已连接 SessionId={SessionId}", SessionId);

        // 初始化 PTY 尺寸默认为 80x24，由 UI 端挂载后自动触发真实尺寸的 Resize
        _shellStream = _client.CreateShellStream(
            _terminalType,
            80, 24, 800, 600, 1024);

        _readCts = new CancellationTokenSource();
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readCts.Token));
    }

    public async Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_shellStream == null || !IsConnected)
        {
            return;
        }

        await _shellStream.WriteAsync(data, ct);
        await _shellStream.FlushAsync(ct);
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
            while (!ct.IsCancellationRequested && _shellStream != null && _client.IsConnected)
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

        // 1) 先发出取消信号
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

        // 释放路径的防御：SSH.NET 的 BaseClient 属性访问器会 CheckDisposed，
        // 已释放后再查询/断开会抛 ObjectDisposedException，这里全部吞掉保证 Dispose 不抛
        bool connected;
        try
        {
            connected = _client.IsConnected;
        }
        catch (ObjectDisposedException)
        {
            connected = false;
        }
        catch
        {
            // 释放路径的防御：连接状态查询的异常一律视为未连接
            connected = false;
        }

        if (connected)
        {
            try
            {
                _client.Disconnect();
            }
            catch
            {
                // 释放路径的防御：断开连接时的网络异常吞掉，不影响释放
            }
        }

        // 3) 等待读取循环任务退出，设置 500ms 超时保护避免极端情况下死等挂起
        if (_readLoopTask != null)
        {
            try
            {
                await Task.WhenAny(_readLoopTask, Task.Delay(500));
            }
            catch
            {
                // 忽略等待退出时的异常
            }
        }

        try
        {
            _client.Dispose();
        }
        catch
        {
            // 释放路径的防御：重复 Dispose 时的异常吞掉，保证释放路径绝不抛
        }

        _readCts?.Dispose();
        _logger.LogInformation("SSH 会话已释放 SessionId={SessionId}", SessionId);
    }
}
