namespace Kei.Term.Ssh.Abstractions;

using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;

// SSH 终端会话：在协议无关会话之上暴露 SSH 专属能力（文件通道借用跳板链等）
public interface ISshSession : ITerminalSession
{
    // 获取底层 SshClient，供 Subsystem 多路复用通道使用
    object? UnderlyingClient { get; }

    // 启动失败的转发说明。空 = 全部起来了，或没有转发。
    IReadOnlyList<string> ForwardStartErrors => [];
}

// 跳板链中的一跳：该跳自身的连接配置与已物化的认证材料
public sealed record SshHop(
    ResolvedSessionConfig Config,
    IReadOnlyList<MaterializedAuthMethod> Methods,
    string? Socks5Host = null,
    int Socks5Port = 0);

// 建连选项：集中承载超时、保活、主机密钥校验、Agent 通道与跳板链，避免工厂方法参数无限膨胀
public sealed record SshConnectOptions
{
    public static SshConnectOptions Default { get; } = new();

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    // <= 0 关闭；NAT/防火墙会静默回收空闲 TCP，保活可避免"挂着的终端突然失联"
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.Zero;

    // keyboard-interactive 真交互回调；为空时用预收集密码应答
    public Func<string, Task<string?>>? InteractivePrompt { get; init; }

    // 主机密钥校验（握手前调整算法顺序；密钥交换回调内裁决，不可弹窗）；为空则不校验（仅供测试/旧调用方）
    public IHostKeyVerifier? HostKeyVerifier { get; init; }

    // 自定义 Agent：空 = 系统默认（SSH_AUTH_SOCK / OpenSSH 命名管道）；"pageant" = PuTTY Pageant；其它 = socket/管道路径
    public string? AgentSocketPath { get; init; }

    // 跳板链（由外到内：第一跳为本机直连的跳板）；空 = 直连
    public IReadOnlyList<SshHop> JumpHosts { get; init; } = [];

    // 本会话最外层直连的无认证 SOCKS5。空主机 = 不走代理。不用于 127.0.0.1 转发口。
    public string? Socks5Host { get; init; }

    public int Socks5Port { get; init; }

    // 挂在目标会话 SSH 客户端上的端口转发。与出站 SOCKS5 无关。
    public IReadOnlyList<PortForward> PortForwards { get; init; } = [];
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
