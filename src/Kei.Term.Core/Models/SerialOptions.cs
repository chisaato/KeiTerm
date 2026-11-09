namespace Kei.Term.Core.Models;

public enum SerialFlowControl { None, RtsCts, XonXoff }
public enum SerialParity { None, Odd, Even, Mark, Space }
public enum SerialStopBits { One, Two, OnePointFive }

// 已解析的运行参数；端口绑定的设备归属和覆盖解析由配置服务负责。
public sealed record SerialOptions
{
    public required string PortName { get; init; }
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public SerialParity Parity { get; init; }
    public SerialStopBits StopBits { get; init; }
    public SerialFlowControl FlowControl { get; init; }
    // null 表示保留驱动默认值，不对不支持调制解调器引脚的 PTY 发出 ioctl。
    public bool? DtrEnable { get; init; }
    public bool? RtsEnable { get; init; }
    public int ReadTimeoutMilliseconds { get; init; } = 100;
    public int WriteTimeoutMilliseconds { get; init; } = 500;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PortName);
        if (BaudRate <= 0) throw new ArgumentOutOfRangeException(nameof(BaudRate));
        if (DataBits is < 5 or > 8) throw new ArgumentOutOfRangeException(nameof(DataBits));
        if (!Enum.IsDefined(Parity)) throw new ArgumentOutOfRangeException(nameof(Parity));
        if (!Enum.IsDefined(StopBits)) throw new ArgumentOutOfRangeException(nameof(StopBits));
        if (!Enum.IsDefined(FlowControl)) throw new ArgumentOutOfRangeException(nameof(FlowControl));
        if (ReadTimeoutMilliseconds is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(ReadTimeoutMilliseconds));
        if (WriteTimeoutMilliseconds is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(WriteTimeoutMilliseconds));
        if (FlowControl == SerialFlowControl.RtsCts && RtsEnable.HasValue)
            throw new ArgumentException("自动 RTS/CTS 已占用 RTS，不能同时设置手动 RTS。", nameof(RtsEnable));
    }
}
