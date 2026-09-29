namespace Kei.Term.Core.Abstractions;

// 协议无关的终端会话：终端标签只依赖这一层。
// SSH 为首个实现（ISshSession）；本地 Shell / 串口 / Telnet 等后续协议实现同一接口即可接入标签、撰写栏与广播。
public interface ITerminalSession : IAsyncDisposable
{
    Guid SessionId { get; }
    bool IsConnected { get; }

    // 远端/子进程输出（可能来自后台线程，订阅方负责切回 UI 线程）
    event Action<byte[]>? OutputReceived;

    // 会话结束：null 为正常退出，非 null 为异常断开
    event Action<Exception?>? Disconnected;

    Task ConnectAsync(CancellationToken ct = default);
    Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default);
}

// 会话协议标识（tree_nodes.protocol 列取值）
public static class SessionProtocols
{
    public const string Ssh = "ssh";
    // 以下为预留：接入时新增对应的会话提供者与协议配置
    public const string Local = "local";
    public const string Serial = "serial";
    public const string Telnet = "telnet";
}
