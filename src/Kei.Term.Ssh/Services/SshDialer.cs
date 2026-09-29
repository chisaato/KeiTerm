namespace Kei.Term.Ssh.Services;

using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Kei.Term.Core.Security;
using Kei.Term.Ssh.Abstractions;

// 一个逻辑 SSH 目标：主机密钥校验、日志均以逻辑地址为准（经跳板时实际拨号的是本机回环转发口）
internal sealed record SshTarget(string Host, int Port, string Username, AuthenticationMethod[] AuthMethods);

// 连接级公共参数（跳板与目标共用）
internal sealed record SshClientOptions(
    TimeSpan ConnectTimeout,
    TimeSpan KeepAliveInterval,
    Func<PresentedHostKey, CancellationToken, Task<HostKeyCheckOutcome>>? HostKeyValidator);

// 主机密钥闸门：挂到 SSH.NET 的 HostKeyReceived 事件，拒绝时记录结论以便抛出可识别的异常
internal sealed class HostKeyGate
{
    private readonly string _host;
    private readonly int _port;
    private readonly Func<PresentedHostKey, CancellationToken, Task<HostKeyCheckOutcome>>? _validator;
    private readonly CancellationToken _ct;

    public HostKeyGate(
        string host,
        int port,
        Func<PresentedHostKey, CancellationToken, Task<HostKeyCheckOutcome>>? validator,
        CancellationToken ct)
    {
        _host = host;
        _port = port;
        _validator = validator;
        _ct = ct;
    }

    public HostKeyCheckOutcome? Rejection { get; private set; }

    public void Attach(BaseClient client)
    {
        if (_validator != null)
        {
            client.HostKeyReceived += OnHostKeyReceived;
        }
    }

    public async Task ConnectAsync(BaseClient client, CancellationToken ct)
    {
        try
        {
            await client.ConnectAsync(ct);
        }
        catch (Exception ex) when (Rejection != null)
        {
            // SSH.NET 对拒绝的主机密钥只抛笼统的密钥交换失败，这里还原真实原因
            throw new HostKeyRejectedException(Rejection, ex);
        }
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var presented = new PresentedHostKey(_host, _port, e.HostKeyName, e.HostKey);
        try
        {
            // 回调运行于 SSH.NET 后台线程；校验只查库不弹窗，同步等待安全
            HostKeyCheckOutcome outcome = _validator!(presented, _ct).GetAwaiter().GetResult();
            e.CanTrust = outcome.Accepted;
            Rejection = outcome.Accepted ? null : outcome;
        }
        catch (Exception)
        {
            // 校验自身故障（如库不可用）一律拒绝：安全检查失败不能退化为放行
            e.CanTrust = false;
            Rejection = new HostKeyCheckOutcome(
                false,
                new HostKeyEvaluation(HostKeyVerdict.Unknown, presented, []),
                HostKeyDecision.Reject);
        }
    }
}

// 跳板链：逐跳建立 SSH 连接，后一跳经前一跳的本地端口转发（direct-tcpip）拨号。
// 注：SSH.NET 公共 API 不支持在已有会话的通道上直接承载新 SSH 连接，故以 127.0.0.1 临时端口转发实现；
// 转发口仅绑定回环且随链释放。
internal sealed class SshJumpChain : IAsyncDisposable
{
    private readonly List<SshClient> _clients = [];
    private readonly List<ForwardedPortLocal> _forwards = [];
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private int _disposed;

    private SshJumpChain(ILogger logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _clients.Count > 0 && _clients.TrueForAll(c => c.IsConnected);

    public static async Task<SshJumpChain> OpenAsync(
        IReadOnlyList<SshTarget> hops,
        SshClientOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        if (hops.Count == 0)
        {
            throw new ArgumentException("跳板链至少需要一跳", nameof(hops));
        }

        var chain = new SshJumpChain(logger);
        try
        {
            foreach (SshTarget hop in hops)
            {
                (string dialHost, int dialPort) = chain._clients.Count == 0
                    ? (hop.Host, hop.Port)
                    : chain.ForwardTo(hop.Host, hop.Port);

                var client = new SshClient(BuildConnectionInfo(hop, dialHost, dialPort, options));
                var gate = new HostKeyGate(hop.Host, hop.Port, options.HostKeyValidator, ct);
                gate.Attach(client);
                ApplyKeepAlive(client, options);
                chain._clients.Add(client);

                await gate.ConnectAsync(client, ct);
                logger.LogInformation("跳板已连接 {Host}:{Port} 层级={Level}", hop.Host, hop.Port, chain._clients.Count);
            }

            return chain;
        }
        catch
        {
            await chain.DisposeAsync();
            throw;
        }
    }

    // 在链尾跳板上开一个 127.0.0.1 临时端口 → host:port 的转发，返回本地拨号地址
    public (string Host, int Port) ForwardTo(string host, int port)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            SshClient last = _clients[^1];
            var forward = new ForwardedPortLocal(IPAddressLoopback, 0, host, (uint)port);
            last.AddForwardedPort(forward);
            forward.Start();
            _forwards.Add(forward);
            return (IPAddressLoopback, (int)forward.BoundPort);
        }
    }

    // 关闭单个转发口（借用方释放时归还，避免在所有者链上累积）
    public void CloseForward(int boundPort)
    {
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            ForwardedPortLocal? forward = _forwards.Find(f => f.BoundPort == (uint)boundPort);
            if (forward == null)
            {
                return;
            }

            _forwards.Remove(forward);
            try
            {
                forward.Stop();
                _clients[^1].RemoveForwardedPort(forward);
            }
            catch
            {
                // 所有者连接已断开时移除可能失败，忽略
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        lock (_sync)
        {
            foreach (ForwardedPortLocal forward in _forwards)
            {
                try
                {
                    forward.Stop();
                }
                catch
                {
                    // 释放路径防御：转发口停止失败不影响后续清理
                }
            }

            // 由内向外断开：先断最深的一跳，再断直连跳板
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                try
                {
                    _clients[i].Dispose();
                }
                catch
                {
                    // 释放路径防御
                }
            }

            _forwards.Clear();
            _clients.Clear();
        }

        _logger.LogInformation("跳板链已释放");
        return ValueTask.CompletedTask;
    }

    internal const string IPAddressLoopback = "127.0.0.1";

    internal static ConnectionInfo BuildConnectionInfo(SshTarget target, string dialHost, int dialPort, SshClientOptions options)
        => new(dialHost, dialPort, target.Username, target.AuthMethods)
        {
            Timeout = options.ConnectTimeout
        };

    internal static void ApplyKeepAlive(BaseClient client, SshClientOptions options)
    {
        if (options.KeepAliveInterval > TimeSpan.Zero)
        {
            client.KeepAliveInterval = options.KeepAliveInterval;
        }
    }
}

// 拨号器：把「如何到达目标」（直连 / 自有跳板链 / 借用会话的跳板链）与主机密钥、保活、超时封装在一起，
// 终端会话、SFTP、SCP 共用，保证三者安全语义一致
internal sealed class SshDialer : IAsyncDisposable
{
    private readonly SshTarget _target;
    private readonly IReadOnlyList<SshTarget> _hops;
    private readonly SshJumpChain? _borrowedChain;
    private readonly SshClientOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _chainLock = new(1, 1);
    private SshJumpChain? _ownedChain;
    private (string Host, int Port)? _dialEndpoint;

    public SshDialer(
        SshTarget target,
        IReadOnlyList<SshTarget> hops,
        SshJumpChain? borrowedChain,
        SshClientOptions options,
        ILogger logger)
    {
        _target = target;
        _hops = hops;
        _borrowedChain = borrowedChain;
        _options = options;
        _logger = logger;
    }

    public SshTarget Target => _target;

    // 当前可供他人借用的跳板链（自有或借来的）
    public SshJumpChain? Chain => _ownedChain ?? _borrowedChain;

    public bool UsesJumpHosts => _hops.Count > 0 || _borrowedChain != null;

    public async Task<T> ConnectClientAsync<T>(Func<ConnectionInfo, T> create, CancellationToken ct)
        where T : BaseClient
    {
        (string host, int port) = await ResolveDialEndpointAsync(ct);
        T client = create(SshJumpChain.BuildConnectionInfo(_target, host, port, _options));
        var gate = new HostKeyGate(_target.Host, _target.Port, _options.HostKeyValidator, ct);
        gate.Attach(client);
        SshJumpChain.ApplyKeepAlive(client, _options);

        try
        {
            await gate.ConnectAsync(client, ct);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return client;
    }

    private async Task<(string Host, int Port)> ResolveDialEndpointAsync(CancellationToken ct)
    {
        if (!UsesJumpHosts)
        {
            return (_target.Host, _target.Port);
        }

        await _chainLock.WaitAsync(ct);
        try
        {
            if (_dialEndpoint is { } cached)
            {
                return cached;
            }

            SshJumpChain chain = _borrowedChain ?? (_ownedChain = await SshJumpChain.OpenAsync(_hops, _options, _logger, ct));
            // 同一转发口可承载多条连接（SCP 需要 scp + exec 两条），每个拨号器只开一个
            _dialEndpoint = chain.ForwardTo(_target.Host, _target.Port);
            return _dialEndpoint.Value;
        }
        finally
        {
            _chainLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 借用的链由其所有者释放，这里只归还自己开的转发口
        if (_borrowedChain != null && _dialEndpoint is { } endpoint)
        {
            _borrowedChain.CloseForward(endpoint.Port);
        }

        if (_ownedChain != null)
        {
            await _ownedChain.DisposeAsync();
            _ownedChain = null;
        }
    }
}
