using Kei.Term.Core.Models;

namespace Kei.Term.Core.Abstractions;

// 名称发现不隐含连接或设备认领，调用者仍可手填未列出的端点。
public interface ISerialPortCatalog
{
    Task<IReadOnlyList<string>> GetPortNamesAsync(TransportEngine engine, CancellationToken ct = default);
}
