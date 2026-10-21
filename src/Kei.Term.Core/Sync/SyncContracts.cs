using System.Text.Json;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;

namespace Kei.Term.Core.Sync;

public sealed record SyncObject(byte[] Content, string? VersionTag);
public sealed record SyncHistoryEntry(string Name, DateTime WrittenAtUtc);

public interface ISyncLocation
{
    bool SupportsConditionalWrites { get; }
    Task TestConnectionAsync(CancellationToken ct = default);
    Task<SyncObject?> ReadAsync(CancellationToken ct = default);
    // 条件写冲突时抛 SyncWriteConflictException；null 标签表示要求对象不存在。
    Task WriteAsync(byte[] content, string? expectedVersionTag = null, CancellationToken ct = default);
    Task<IReadOnlyList<SyncHistoryEntry>> ListHistoryAsync(CancellationToken ct = default);
    Task<byte[]?> ReadHistoryAsync(string name, CancellationToken ct = default);
    Task WriteHistoryAsync(string name, byte[] content, CancellationToken ct = default);
    Task DeleteHistoryAsync(string name, CancellationToken ct = default);
    Task DeleteAsync(CancellationToken ct = default) => throw new NotSupportedException("同步位置不支持删除主包");
}

public sealed class SyncWriteConflictException : IOException
{
    public SyncWriteConflictException() : base("同步包已被其他设备修改") { }
}

// 按类型分组的信封；新增实体类型无需改变合并算法，也不把数据库行模型泄漏给 Core。
public sealed record SyncEntity(string EntityType, string EntityId, DateTime UpdatedAtUtc,
    string? DeviceId, DateTime? DeletedAtUtc, JsonElement Data);

public sealed record SyncTreeData(FolderNode? Folder, SessionNode? Session)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TreeNodeBase Node => (TreeNodeBase?)Folder ?? Session ?? throw new JsonException("缺少会话树节点");
}

public sealed record SyncSnapshot(int FormatVersion, string WriterDeviceId, DateTime WrittenAtUtc, List<SyncEntity> Entities)
{
    public const int CurrentFormatVersion = 1;
    public const string TreeEntityType = "tree";
}

public interface ISyncSnapshotStore
{
    Task<List<SyncEntity>> ExportAsync(CancellationToken ct = default);
    // 所有已注册实体类型在同一事务中导入，保留远端版本戳；不得按本机修改重新盖章。
    Task<List<SyncEntity>> ImportAsync(IReadOnlyList<SyncEntity> entities, CancellationToken ct = default);
    Task<List<SyncEntity>> ImportGuardedAsync(IReadOnlyList<SyncEntity> entities, IReadOnlyList<SyncEntity> expectedLocal, CancellationToken ct = default)
        => throw new InvalidOperationException("快照存储不支持安全预览校验");
    Task PurgeTombstonesBeforeAsync(DateTime cutoffUtc, CancellationToken ct = default);
}

public interface ISyncDeviceId : ICurrentDeviceId
{
    string EnsureCreated();
}

public interface ISyncPassphraseStore
{
    bool IsUnlocked { get; }
    Task<char[]?> GetPassphraseAsync(CancellationToken ct = default);
    Task SavePassphraseAsync(ReadOnlyMemory<char> passphrase, CancellationToken ct = default);
    Task DeletePassphraseAsync(CancellationToken ct = default) => throw new NotSupportedException();
}

public enum SyncJoinChoice { Merge, Remote, Local }
public enum SyncConfirmationKind { FirstJoin, BulkDeletion }
public sealed record SyncConfirmation(string Token, SyncConfirmationKind Kind, int DeletedCount, int LocalLiveCount);
public sealed record SyncApproval(string Token, SyncJoinChoice Choice = SyncJoinChoice.Merge);
public sealed record SyncResult(bool Success, string Message, DateTime? WrittenAtUtc = null, string? WriterDeviceId = null,
    SyncConfirmation? Confirmation = null);

public sealed record SyncLocalBackup(string Id, DateTime CreatedAtUtc);
public interface ISyncSafetyStore
{
    Task EnsureSyncMasterPasswordAsync(CancellationToken ct = default);
    Task<bool> HasJoinedAsync(CancellationToken ct = default);
    Task SetJoinedAsync(bool joined, CancellationToken ct = default);
    Task SaveBackupAsync(IReadOnlyList<SyncEntity> entities, CancellationToken ct = default);
    Task<IReadOnlyList<SyncLocalBackup>> ListBackupsAsync(CancellationToken ct = default);
    Task<List<SyncEntity>> ReadBackupAsync(string id, CancellationToken ct = default);
    Task DeleteSyncBackupsAsync(CancellationToken ct = default);
    // 在任何远端写入之前持久化新旧口令，网络结果不确定时仍可恢复。
    Task SavePendingPassphraseAsync(ReadOnlyMemory<char> passphrase, CancellationToken ct = default);
    Task<char[]?> GetPendingPassphraseAsync(CancellationToken ct = default);
    Task<char[]?> GetPreviousPassphraseAsync(CancellationToken ct = default);
    Task ClearPendingPassphraseAsync(CancellationToken ct = default);
}

public sealed class SyncPreviewChangedException() : InvalidOperationException("本机数据已变化，请重新预览确认");
