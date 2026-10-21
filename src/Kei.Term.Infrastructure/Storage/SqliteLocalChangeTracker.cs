namespace Kei.Term.Infrastructure.Storage;

using Dapper;
using Kei.Term.Core.Storage;
using Microsoft.Data.Sqlite;

public sealed class SqliteLocalChangeTracker(SqliteConnectionFactory factory) : ILocalChangeTracker, ILocalPersistenceReadiness
{
    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        using var conn = await factory.OpenAsync(ct);
        await SyncFileRecovery.EnsureReadyAsync(conn, null, ct);
    }

    public async Task TrackAsync(string entityType, string entityId, bool deleted = false, CancellationToken cancellationToken = default)
    {
        using var conn = await factory.OpenAsync(cancellationToken);
        await TrackCoreAsync(conn, null, entityType, entityId, factory.DeviceId, deleted, cancellationToken);
    }

    internal static Task TrackCoreAsync(SqliteConnection conn, SqliteTransaction? tx, string entityType, string entityId, string? deviceId, bool deleted, CancellationToken ct)
    {
        string now = SqliteValue.FormatUtc(DateTime.UtcNow);
        return conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO local_entity_stamps(entity_type, entity_id, updated_at, device_id, deleted_at)
            VALUES (@entityType, @entityId, @now, @deviceId, @deletedAt)
            ON CONFLICT(entity_type, entity_id) DO UPDATE SET
                updated_at = excluded.updated_at, device_id = excluded.device_id,
                deleted_at = excluded.deleted_at, revision = local_entity_stamps.revision + 1;
        ", new { entityType, entityId, now, deviceId, deletedAt = deleted ? now : null }, tx, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<LocalEntityStamp>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var conn = await factory.OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<StampRow>(new CommandDefinition("SELECT * FROM local_entity_stamps;", cancellationToken: cancellationToken));
        return rows.Select(r => new LocalEntityStamp(r.EntityType, r.EntityId, SqliteValue.ParseUtc(r.UpdatedAt), r.DeviceId,
            r.DeletedAt == null ? null : SqliteValue.ParseUtc(r.DeletedAt), r.Revision)).ToList();
    }

    public async Task PurgeTombstonesBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        using var conn = await factory.OpenAsync(cancellationToken);
        using var tx = conn.BeginTransaction();
        var args = new { cutoff = SqliteValue.FormatUtc(cutoffUtc) };
        // 从叶子开始清理，且不让旧父墓碑的 FK 级联删掉较新的子实体。
        foreach (string table in new[] { "external_editor_paths", "file_associations", "identity_secrets", "proxy_secrets", "port_forwards", "session_details", "known_hosts" })
            await conn.ExecuteAsync(new CommandDefinition($"DELETE FROM {table} WHERE deleted_at < @cutoff;", args, tx, cancellationToken: cancellationToken));
        await conn.ExecuteAsync(new CommandDefinition(@"
            DELETE FROM external_editors WHERE deleted_at < @cutoff
                AND NOT EXISTS(SELECT 1 FROM external_editor_paths WHERE editor_id = external_editors.id)
                AND NOT EXISTS(SELECT 1 FROM file_associations WHERE editor_id = external_editors.id);
            DELETE FROM identities WHERE deleted_at < @cutoff AND NOT EXISTS(SELECT 1 FROM identity_secrets WHERE identity_id = identities.id);
            DELETE FROM proxies WHERE deleted_at < @cutoff AND NOT EXISTS(SELECT 1 FROM proxy_secrets WHERE proxy_id = proxies.id);
            UPDATE tree_nodes SET parent_id = NULL WHERE parent_id IN (SELECT id FROM tree_nodes WHERE deleted_at < @cutoff)
                AND (deleted_at IS NULL OR deleted_at >= @cutoff);
        ", args, tx, cancellationToken: cancellationToken));
        // 有未过期子节点/会话配置/转发的父墓碑保留到下次，避免级联误删。
        int removed;
        do
        {
            removed = await conn.ExecuteAsync(new CommandDefinition(@"
                DELETE FROM tree_nodes WHERE deleted_at < @cutoff
                  AND NOT EXISTS(SELECT 1 FROM tree_nodes c WHERE c.parent_id = tree_nodes.id)
                  AND NOT EXISTS(SELECT 1 FROM session_details WHERE node_id = tree_nodes.id)
                  AND NOT EXISTS(SELECT 1 FROM port_forwards WHERE session_id = tree_nodes.id);", args, tx, cancellationToken: cancellationToken));
        } while (removed > 0);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM local_entity_stamps WHERE deleted_at < @cutoff;", args, tx, cancellationToken: cancellationToken));
        tx.Commit();
    }

    private sealed class StampRow
    {
        public string EntityType { get; set; } = "";
        public string EntityId { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string? DeviceId { get; set; }
        public string? DeletedAt { get; set; }
        public long Revision { get; set; }
    }
}
