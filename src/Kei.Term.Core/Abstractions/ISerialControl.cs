namespace Kei.Term.Core.Abstractions;

// 可选的串口控制能力，不使普通终端依赖串口或平台库。
public interface ISerialControl
{
    // 根据已解析的实际流控模式裁决，而非会话通用配置。
    bool CanSetRts => true;

    Task SetDtrAsync(bool enabled, CancellationToken ct = default);
    Task SetRtsAsync(bool enabled, CancellationToken ct = default);
    Task SetBreakStateAsync(bool enabled, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
}
