using System.Text.Json;
using Dapper;
using Kei.Term.Core.Models;
using Kei.Term.Core.Sync;

namespace Kei.Term.Infrastructure.Storage;

public sealed class SqliteSyncSnapshotStore(SqliteConnectionFactory factory) : ISyncSnapshotStore
{
    public async Task<List<SyncEntity>> ExportAsync(CancellationToken ct = default)
    {
        using var conn = await factory.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        // 由仓储在同一读取事务中导出内容与版本戳，避免撕裂快照。
        var repository = new SqliteTreeRepository(factory);
        List<SyncEntity> entities = await repository.ExportSyncEntitiesAsync(conn, tx, ct);
        tx.Commit();
        return entities;
    }

    public async Task<List<SyncEntity>> ImportAsync(IReadOnlyList<SyncEntity> entities, CancellationToken ct = default)
    {
        using var conn = await factory.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        // 父子节点可能以任意顺序到达，外键仍开启，仅延迟到提交检查。
        await conn.ExecuteAsync(new CommandDefinition("PRAGMA defer_foreign_keys = ON;", transaction: tx, cancellationToken: ct));
        var repository = new SqliteTreeRepository(factory);
        var current = await repository.ExportSyncEntitiesAsync(conn, tx, ct);
        // 网络往返期间仍允许用户编辑。事务内再次合并，不能用早先导出的版本覆盖刚保存的内容。
        var merged = SyncCoordinator.Merge(current, entities);
        var existing = current.ToDictionary(e => (e.EntityType, e.EntityId));
        foreach (SyncEntity entity in merged)
        {
            if (entity.EntityType != SyncSnapshot.TreeEntityType)
                throw new InvalidOperationException($"不支持的同步实体类型：{entity.EntityType}，请升级应用");
            var data = entity.Data.Deserialize<SyncTreeData>() ?? throw new JsonException("无效会话树节点");
            if (data.Node.Id.ToString() != entity.EntityId) throw new JsonException("同步实体标识不匹配");
            if (existing.TryGetValue((entity.EntityType, entity.EntityId), out var old) &&
                JsonSerializer.Serialize(old) == JsonSerializer.Serialize(entity)) continue;
            await repository.SaveNodeCoreAsync(conn, tx, data.Node, ct, entity);
            await conn.ExecuteAsync(new CommandDefinition(@"
                UPDATE tree_nodes SET deleted_at = @deleted WHERE id = @id;
                UPDATE session_details SET deleted_at = @deleted WHERE node_id = @id;",
                new { id = entity.EntityId, deleted = entity.DeletedAtUtc is { } date ? SqliteValue.FormatUtc(date) : null }, tx, cancellationToken: ct));
        }
        tx.Commit();
        return merged;
    }

    public Task PurgeTombstonesBeforeAsync(DateTime cutoffUtc, CancellationToken ct = default)
        => new SqliteLocalChangeTracker(factory).PurgeTombstonesBeforeAsync(cutoffUtc, ct);
}
