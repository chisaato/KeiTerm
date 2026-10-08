namespace Kei.Term.Ssh.Services;

using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Ssh.Abstractions;

// 一个逻辑 SSH 目标：主机密钥校验、日志均以逻辑地址为准（经跳板时实际拨号的是本机回环转发口）
internal sealed record SshTarget(
    string Host,
    int Port,
    string Username,
    AuthenticationMethod[] AuthMethods,
    string? Socks5Host = null,
    int Socks5Port = 0,
    string? Socks5Username = null,
    string? Socks5Password = null);

// 连接级公共参数（跳板与目标共用）
internal sealed record SshClientOptions(
    TimeSpan ConnectTimeout,
    TimeSpan KeepAliveInterval,
    IHostKeyVerifier? HostKeyVerifier);

// 主机密钥闸门：挂到 SSH.NET 的 HostKeyReceived 事件，拒绝时记录结论以便抛出可识别的异常
internal sealed class HostKeyGate
{
    private readonly string _host;
    private readonly int _port;
    private readonly IHostKeyVerifier? _verifier;
    private readonly CancellationToken _ct;

    public HostKeyGate(string host, int port, IHostKeyVerifier? verifier, CancellationToken ct)
    {
        _host = host;
        _port = port;
        _verifier = verifier;
        _ct = ct;
    }

    public HostKeyCheckOutcome? Rejection { get; private set; }

    public void Attach(BaseClient client)
    {
        if (_verifier != null)
        {
            client.HostKeyReceived += OnHostKeyReceived;
        }
    }

    public async Task ConnectAsync(BaseClient client, CancellationToken ct)
    {
        await PreferKnownAlgorithmsAsync(client.ConnectionInfo, ct);
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

    // 已记录某类密钥时把对应算法排到协商列表最前，避免协商到未记录的另一把而误报未知主机
    private async Task PreferKnownAlgorithmsAsync(ConnectionInfo info, CancellationToken ct)
    {
        if (_verifier == null)
        {
            return;
        }

        IReadOnlyList<string> knownTypes = await _verifier.GetKnownKeyTypesAsync(_host, _port, ct);
        IReadOnlyList<string> ordered = HostKeyAlgorithmPreference.Order(info.HostKeyAlgorithms.Keys.ToList(), knownTypes.ToHashSet());
        for (int i = 0; i < ordered.Count; i++)
        {
            info.HostKeyAlgorithms.SetPosition(ordered[i], i);
        }
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        // HostKeyName 是签名算法（如 rsa-sha2-512），信任库按密钥类型（ssh-rsa）记录：以 blob 自描述类型为准
        string keyType = HostKeyFingerprint.ReadKeyType(e.HostKey) ?? e.HostKeyName;
        var presented = new PresentedHostKey(_host, _port, keyType, e.HostKey);
        try
        {
            // 回调运行于 SSH.NET 后台线程；校验只查库不弹窗，同步等待安全
            HostKeyCheckOutcome outcome = _verifier!.VerifyAsync(presented, _ct).GetAwaiter().GetResult();
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

    // 跳板链上的 SSH 客户端。会话转发不挂在这里。
    internal IReadOnlyList<SshClient> Clients => _clients;

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

                if (chain._clients.Count == 0
                    && !string.IsNullOrWhiteSpace(hop.Socks5Host)
                    && !string.Equals(dialHost, IPAddressLoopback, StringComparison.Ordinal))
                {
                    // 只记端点和是否带认证。口令与用户名都不进日志。
                    logger.LogInformation(
                        "跳板最外层经 SOCKS5 {ProxyHost}:{ProxyPort} 拨号 {Host}:{Port} 认证={HasAuth}",
                        hop.Socks5Host,
                        hop.Socks5Port,
                        hop.Host,
                        hop.Port,
                        HasSocks5Auth(hop.Socks5Username, hop.Socks5Password));
                }

                var client = new SshClient(BuildConnectionInfo(hop, dialHost, dialPort, options));
                var gate = new HostKeyGate(hop.Host, hop.Port, options.HostKeyVerifier, ct);
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
    {
        // 127.0.0.1 是跳板链的本地转发口，不能再套一层代理
        bool forwarded = string.Equals(dialHost, IPAddressLoopback, StringComparison.Ordinal);
        if (!forwarded
            && !string.IsNullOrWhiteSpace(target.Socks5Host)
            && target.Socks5Port is >= 1 and <= 65535)
        {
            return new ConnectionInfo(
                dialHost,
                dialPort,
                target.Username,
                ProxyTypes.Socks5,
                target.Socks5Host,
                target.Socks5Port,
                target.Socks5Username ?? string.Empty,
                target.Socks5Password ?? string.Empty,
                target.AuthMethods)
            {
                Timeout = options.ConnectTimeout
            };
        }

        return new ConnectionInfo(dialHost, dialPort, target.Username, target.AuthMethods)
        {
            Timeout = options.ConnectTimeout
        };
    }

    internal static bool HasSocks5Auth(string? username, string? password)
        => !string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password);

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
    private readonly IReadOnlyList<PortForward> _portForwards;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _chainLock = new(1, 1);
    private readonly List<ForwardedPort> _sessionForwards = [];
    private readonly List<string> _forwardErrors = [];
    private SshJumpChain? _ownedChain;
    private SshClient? _forwardClient;
    private (string Host, int Port)? _dialEndpoint;

    public SshDialer(
        SshTarget target,
        IReadOnlyList<SshTarget> hops,
        SshJumpChain? borrowedChain,
        SshClientOptions options,
        ILogger logger,
        IReadOnlyList<PortForward>? portForwards = null)
    {
        _target = target;
        _hops = hops;
        _borrowedChain = borrowedChain;
        _options = options;
        _logger = logger;
        _portForwards = portForwards ?? [];
    }

    public IReadOnlyList<string> ForwardStartErrors => _forwardErrors;

    public SshTarget Target => _target;

    // 当前可供他人借用的跳板链（自有或借来的）
    public SshJumpChain? Chain => _ownedChain ?? _borrowedChain;

    public bool UsesJumpHosts => _hops.Count > 0 || _borrowedChain != null;

    public async Task<T> ConnectClientAsync<T>(Func<ConnectionInfo, T> create, CancellationToken ct)
        where T : BaseClient
    {
        (string host, int port) = await ResolveDialEndpointAsync(ct);
        if (!string.Equals(host, SshJumpChain.IPAddressLoopback, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(_target.Socks5Host))
        {
            _logger.LogInformation(
                "经 SOCKS5 拨号 {ProxyHost}:{ProxyPort} 目标={Host}:{Port} 认证={HasAuth}",
                _target.Socks5Host,
                _target.Socks5Port,
                _target.Host,
                _target.Port,
                SshJumpChain.HasSocks5Auth(_target.Socks5Username, _target.Socks5Password));
        }

        T client = create(SshJumpChain.BuildConnectionInfo(_target, host, port, _options));
        var gate = new HostKeyGate(_target.Host, _target.Port, _options.HostKeyVerifier, ct);
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

        // 只挂在目标会话的 SshClient 上。跳板客户端走 SshJumpChain，不会进这里。
        if (client is SshClient ssh)
        {
            StartSessionForwards(ssh);
        }

        return client;
    }

    private void StartSessionForwards(SshClient client)
    {
        if (_sessionForwards.Count > 0 || _portForwards.Count == 0)
        {
            return;
        }

        _forwardClient = client;
        foreach (PortForward rule in _portForwards)
        {
            ForwardedPort? port = null;
            try
            {
                port = CreateForward(rule);
                client.AddForwardedPort(port);
                port.Start();
                _sessionForwards.Add(port);
                _logger.LogInformation(
                    "端口转发已启动 名称={Name} 模式={Mode} {Bind}:{Listen}",
                    rule.Name,
                    rule.Mode,
                    rule.BindAddress,
                    rule.ListenPort);
            }
            catch (Exception ex)
            {
                // 一条失败不拆掉已经连上的 SSH
                string label = string.IsNullOrWhiteSpace(rule.Name) ? rule.Mode.ToString() : rule.Name;
                string message = $"端口转发未启动 {label} {rule.BindAddress}:{rule.ListenPort}";
                _forwardErrors.Add(message);
                _logger.LogWarning(ex, "端口转发启动失败 {Detail}", message);
                if (port != null)
                {
                    try
                    {
                        client.RemoveForwardedPort(port);
                    }
                    catch (Exception removeEx)
                    {
                        _logger.LogWarning(removeEx, "移除失败的端口转发时出错");
                    }
                }
            }
        }
    }

    private static ForwardedPort CreateForward(PortForward rule)
    {
        string bind = string.IsNullOrWhiteSpace(rule.BindAddress) ? "127.0.0.1" : rule.BindAddress.Trim();
        uint listen = (uint)rule.ListenPort;
        return rule.Mode switch
        {
            PortForwardMode.Remote => new ForwardedPortRemote(bind, listen, rule.DestinationHost!, (uint)rule.DestinationPort!.Value),
            PortForwardMode.Dynamic => new ForwardedPortDynamic(bind, listen),
            _ => new ForwardedPortLocal(bind, listen, rule.DestinationHost!, (uint)rule.DestinationPort!.Value)
        };
    }

    private void StopSessionForwards()
    {
        foreach (ForwardedPort port in _sessionForwards)
        {
            try
            {
                if (port.IsStarted)
                {
                    port.Stop();
                }

                _forwardClient?.RemoveForwardedPort(port);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "停止端口转发失败");
            }
        }

        _sessionForwards.Clear();
        _forwardClient = null;
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
        StopSessionForwards();

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
