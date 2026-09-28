namespace Kei.Term.Ssh.Abstractions;

using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

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

public interface ISshSessionFactory
{
    // methods 已按身份方法序列物化并排序；interactivePrompt 为 keyboard-interactive 真交互回调，
    // 提供时逐提示询问（2FA 可用），为空则用预收集密码应答。
    Task<ISshSession> CreateSessionAsync(
        ResolvedSessionConfig config,
        IReadOnlyList<MaterializedAuthMethod> methods,
        TimeSpan? connectTimeout = null,
        Func<string, Task<string?>>? interactivePrompt = null,
        CancellationToken ct = default);

    // 根据配置与认证材料构建 SFTP / SCP 远程文件系统实例
    Task<IRemoteFileSystem> CreateFileSystemAsync(
        ResolvedSessionConfig config,
        IReadOnlyList<MaterializedAuthMethod> methods,
        ISshSession? activeSession = null,
        TimeSpan? connectTimeout = null,
        CancellationToken ct = default);
}
