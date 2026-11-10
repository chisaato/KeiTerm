namespace Kei.Term.Core.Models;

public enum SerialConnectionFailure { DeviceNotFound, AccessDenied, DeviceBusy, IoFailure }

// 保留原生异常作为 InnerException，便于诊断驱动/平台细节，不靠本地化消息判断错误。
public sealed class SerialConnectionException(SerialConnectionFailure failure, string portName, Exception innerException)
    : IOException($"串口 {portName} 打开失败：{failure}。", innerException)
{
    public SerialConnectionFailure Failure { get; } = failure;
    public string PortName { get; } = portName;
}
