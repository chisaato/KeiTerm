namespace Kei.Term.Ssh.Abstractions;

using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;

public interface ISshSession : IAsyncDisposable
{
    Guid SessionId { get; }
    bool IsConnected { get; }

    event Action<byte[]>? OutputReceived;
    event Action<Exception?>? Disconnected;

    Task ConnectAsync(CancellationToken ct = default);
    Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default);

    // 获取底层 SshClient，供 Subsystem 多路复用通道使用
    object? UnderlyingClient { get; }
}

// 跳板链中的一跳：该跳自身的连接配置与已物化的认证材料
public sealed record SshHop(ResolvedSessionConfig Config, IReadOnlyList<MaterializedAuthMethod> Methods);

// 建连选项：集中承载超时、保活、主机密钥校验、Agent 通道与跳板链，避免工厂方法参数无限膨胀
public sealed record SshConnectOptions
{
    public static SshConnectOptions Default { get; } = new();

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    // <= 0 关闭；NAT/防火墙会静默回收空闲 TCP，保活可避免"挂着的终端突然失联"
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.Zero;

    // keyboard-interactive 真交互回调；为空时用预收集密码应答
    public Func<string, Task<string?>>? InteractivePrompt { get; init; }

    // 主机密钥校验（在密钥交换回调内调用，必须快速返回、不可弹窗）；为空则不校验（仅供测试/旧调用方）
    public Func<PresentedHostKey, CancellationToken, Task<HostKeyCheckOutcome>>? HostKeyValidator { get; init; }

    // 自定义 Agent：空 = 系统默认（SSH_AUTH_SOCK / OpenSSH 命名管道）；"pageant" = PuTTY Pageant；其它 = socket/管道路径
    public string? AgentSocketPath { get; init; }

    // 跳板链（由外到内：第一跳为本机直连的跳板）；空 = 直连
    public IReadOnlyList<SshHop> JumpHosts { get; init; } = [];
}

public interface ISshSessionFactory
{
    // methods 已按身份方法序列物化并排序
    Task<ISshSession> CreateSessionAsync(
        ResolvedSessionConfig config,
        IReadOnlyList<MaterializedAuthMethod> methods,
        SshConnectOptions? options = null,
        CancellationToken ct = default);

    // 根据配置与认证材料构建 SFTP / SCP 远程文件系统实例；
    // 传入 activeSession 时复用其跳板链（不再重复登录跳板机）
    Task<IRemoteFileSystem> CreateFileSystemAsync(
        ResolvedSessionConfig config,
        IReadOnlyList<MaterializedAuthMethod> methods,
        ISshSession? activeSession = null,
        SshConnectOptions? options = null,
        CancellationToken ct = default);
}
