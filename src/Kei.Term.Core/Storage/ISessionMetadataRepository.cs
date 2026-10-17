namespace Kei.Term.Core.Storage;

using Kei.Term.Core.Transfers;

// 会话的连接元数据。按键读写，调用方只看到强类型；不改 revision，也不参与同步。
public interface ISessionMetadataRepository
{
    Task<TransferProbeResult?> GetTransferProbeAsync(Guid sessionId, CancellationToken cancellationToken = default);

    // 没有 session_details 行时返回 false，调用方改为只放内存。写入成功返回 true。
    Task<bool> SetTransferProbeAsync(Guid sessionId, TransferProbeResult result, CancellationToken cancellationToken = default);

    Task ClearTransferProbeAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
