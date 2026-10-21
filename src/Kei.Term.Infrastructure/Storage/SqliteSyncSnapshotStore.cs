using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Vault;
using Microsoft.Data.Sqlite;

namespace Kei.Term.Infrastructure.Storage;

// 只有这里列出的内容列可以进入包；版本戳在信封中，revision 与本机数据绝不混入。
public sealed partial class SqliteSyncSnapshotStore(SqliteConnectionFactory factory, InternalVaultManager? vault = null,
    string? settingsPath = null, string? profilesPath = null) : ISyncSnapshotStore
{
    // 事务与持久化锁释放后通知宿主更新内存设置/Profile/树；自动与手动同步共用。
    public event Func<Task>? Imported;
    internal Action<SyncFileRecoveryCheckpoint>? RecoveryCheckpoint { get; set; }

    public const string IdentityType = "identity", ProxyType = "proxy", PortForwardType = "port-forward",
        EditorType = "editor", AssociationType = "association", KnownHostType = "known-host";

    private sealed record Table(string Type, string Name, string Columns);
    private static readonly Table[] Tables =
    [
        new(IdentityType, "identities", "id,name,description,username,methods_json,created_at"),
        new(ProxyType, "proxies", "id,name,sort_order,config_json,created_at"),
        new(EditorType, "external_editors", "id,name,arguments_template,is_default,created_at"),
        new(AssociationType, "file_associations", "id,pattern,editor_id,priority,created_at"),
        new(PortForwardType, "port_forwards", "id,session_id,name,mode,bind_address,listen_port,destination_host,destination_port"),
        new(KnownHostType, "known_hosts", "host,port,key_type,public_key,fingerprint_sha256,status,source,comment,created_at,last_seen_at")
    ];

    // 明确的内容 DTO：字段仅取自上面的白名单；路径与秘密随其所属主实体做一次 LWW。
    public sealed record RecordData(Dictionary<string, JsonElement> Fields, List<EditorPath>? Paths = null, byte[]? Secret = null);
    public sealed record EditorPath(string Os, string Path);

    public async Task<List<SyncEntity>> ExportAsync(CancellationToken ct = default)
    {
        using var persistence = await Kei.Term.Core.Storage.LocalPersistenceGate.EnterAsync(ct);
        return vault == null ? await ExportCoreAsync(ct) : await vault.WithSnapshotSecretsAsync(() => ExportCoreAsync(ct), ct);
    }

    private async Task<List<SyncEntity>> ExportCoreAsync(CancellationToken ct)
    {
        using var conn = await factory.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        await SyncFileRecovery.EnsureReadyAsync(conn, tx, ct);
        var entities = await ExportAllAsync(conn, tx, ct);
        tx.Commit();
        return entities;
    }

    private async Task<List<SyncEntity>> ExportAllAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        var entities = await new SqliteTreeRepository(factory).ExportSyncEntitiesAsync(conn, tx, ct);
        foreach (var table in Tables)
        {
            var rows = await conn.QueryAsync(new CommandDefinition($"SELECT {table.Columns}, updated_at, device_id, deleted_at FROM {table.Name};", transaction: tx, cancellationToken: ct));
            foreach (IDictionary<string, object> row in rows)
            {
                var fields = table.Columns.Split(',').ToDictionary(c => c, c => JsonSerializer.SerializeToElement(row[c]));
                string id = table.Type == KnownHostType ? KnownHostId(fields) : fields["id"].GetString()!;
                byte[]? secret = null;
                try
                {
                    if (table.Type is IdentityType or ProxyType && row["deleted_at"] == null)
                    {
                        if (vault != null) secret = await vault.ExportSnapshotSecretAsync(conn, tx, table.Type == IdentityType, id, ct);
                        else if (await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                            $"SELECT COUNT(*) FROM {(table.Type == IdentityType ? "identity_secrets" : "proxy_secrets")} WHERE {(table.Type == IdentityType ? "identity_id" : "proxy_id")} = @id AND deleted_at IS NULL;", new { id }, tx, cancellationToken: ct)) > 0)
                            throw new InvalidOperationException("同步秘密需要本机保险库");
                    }
                    List<EditorPath>? paths = table.Type == EditorType
                        ? (await conn.QueryAsync<EditorPath>(new CommandDefinition("SELECT os, path FROM external_editor_paths WHERE editor_id = @id AND deleted_at IS NULL ORDER BY os;", new { id }, tx, cancellationToken: ct))).ToList() : null;
                    entities.Add(new(table.Type, id, SqliteValue.ParseUtc((string)row["updated_at"]), row["device_id"] as string,
                        row["deleted_at"] is string deleted ? SqliteValue.ParseUtc(deleted) : null,
                        JsonSerializer.SerializeToElement(new RecordData(fields, paths, secret))));
                }
                finally { if (secret != null) CryptographicOperations.ZeroMemory(secret); }
            }
        }
        await ExportTrackedAsync(conn, tx, entities, ct);
        return entities;
    }

    public async Task<List<SyncEntity>> ImportAsync(IReadOnlyList<SyncEntity> entities, CancellationToken ct = default)
        => await ImportWithGuardAsync(entities, null, ct);

    public Task<List<SyncEntity>> ImportGuardedAsync(IReadOnlyList<SyncEntity> entities, IReadOnlyList<SyncEntity> expectedLocal, CancellationToken ct = default)
        => ImportWithGuardAsync(entities, expectedLocal, ct);

    private async Task<List<SyncEntity>> ImportWithGuardAsync(IReadOnlyList<SyncEntity> entities, IReadOnlyList<SyncEntity>? expectedLocal, CancellationToken ct)
    {
        List<SyncEntity> result;
        using (await Kei.Term.Core.Storage.LocalPersistenceGate.EnterAsync(ct))
            result = vault == null ? await ImportCoreAsync(entities, expectedLocal, ct) : await vault.WithSnapshotSecretsAsync(() => ImportCoreAsync(entities, expectedLocal, ct), ct);
        if (Imported != null)
            foreach (Func<Task> handler in Imported.GetInvocationList()) await handler();
        return result;
    }

    private async Task<List<SyncEntity>> ImportCoreAsync(IReadOnlyList<SyncEntity> entities, IReadOnlyList<SyncEntity>? expectedLocal, CancellationToken ct)
    {
        using var conn = await factory.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        await SyncFileRecovery.EnsureReadyAsync(conn, tx, ct);
        await conn.ExecuteAsync(new CommandDefinition("PRAGMA defer_foreign_keys = ON;", transaction: tx, cancellationToken: ct));
        var current = await ExportAllAsync(conn, tx, ct);
        if (expectedLocal != null && !SyncCoordinator.SameEntities(current, expectedLocal)) throw new SyncPreviewChangedException();
        var merged = SyncCoordinator.Merge(current, entities.Where(e => e.EntityType != "setting" || !SyncLocalOnly.IsSettingExcluded(e.EntityId)).ToList());
        var existing = current.ToDictionary(e => (e.EntityType, e.EntityId));
        var changed = merged.Where(e => !existing.TryGetValue((e.EntityType, e.EntityId), out var old) || JsonSerializer.Serialize(old) != JsonSerializer.Serialize(e)).ToList();
        // 先应用墓碑，释放端口等存活实体唯一键，再应用新版本；父子外键延迟检查。
        foreach (var entity in changed.OrderBy(e => e.DeletedAtUtc == null ? 1 : 0)
            .ThenBy(e => e.EntityType == "metadata" ? 2 : e.EntityType == SyncSnapshot.TreeEntityType ? 0 : 1))
        {
            if (entity.EntityType == SyncSnapshot.TreeEntityType)
            {
                var data = entity.Data.Deserialize<SyncTreeData>() ?? throw new JsonException("无效会话树节点");
                if (data.Node.Id.ToString() != entity.EntityId) throw new JsonException("同步实体标识不匹配");
                await new SqliteTreeRepository(factory).SaveNodeCoreAsync(conn, tx, data.Node, ct, entity);
                await conn.ExecuteAsync(new CommandDefinition("UPDATE tree_nodes SET deleted_at = @deleted WHERE id = @id; UPDATE session_details SET deleted_at = @deleted WHERE node_id = @id;",
                    new { id = entity.EntityId, deleted = Date(entity.DeletedAtUtc) }, tx, cancellationToken: ct));
            }
            else if (Tables.FirstOrDefault(t => t.Type == entity.EntityType) is { } table)
                await ImportRecordAsync(conn, tx, table, entity, ct);
            else if (entity.EntityType is "setting" or "profile" or "metadata")
                await ImportTrackedAsync(conn, tx, entity, ct);
            else throw new InvalidOperationException($"不支持的同步实体类型：{entity.EntityType}，请升级应用");
        }
        // 实体/版本和 redo 同事务提交；提交后文件失败保留 journal，不能声称已回滚。
        await CommitFilesAsync(conn, tx, changed, ct);
        return merged;
    }

    private async Task ImportRecordAsync(SqliteConnection conn, SqliteTransaction tx, Table table, SyncEntity entity, CancellationToken ct)
    {
        var data = entity.Data.Deserialize<RecordData>() ?? throw new JsonException("无效同步内容");
        try
        {
            string[] columns = table.Columns.Split(',');
            if (data.Fields.Count != columns.Length || columns.Any(c => !data.Fields.ContainsKey(c))) throw new JsonException("无效同步内容列");
            string id = table.Type == KnownHostType ? KnownHostId(data.Fields) : data.Fields["id"].GetString()!;
            if (id != entity.EntityId) throw new JsonException("同步实体标识不匹配");
            if (table.Type != KnownHostType && !Guid.TryParse(id, out _)) throw new JsonException("无效同步实体标识");
            var args = new DynamicParameters();
            foreach (string column in columns) args.Add(column, Scalar(data.Fields[column]));
            args.Add("updated_at", Date(entity.UpdatedAtUtc)); args.Add("device_id", entity.DeviceId); args.Add("deleted_at", Date(entity.DeletedAtUtc));
            string insert = table.Columns;
            string conflict = "id";
            if (table.Type == KnownHostType)
            {
                // 数据库 surrogate id 不参与身份；复用本机行，否则使用确定性自然键摘要。
                args.Add("id", new Guid(Convert.FromHexString(id)[..16]).ToString());
                insert = "id," + insert;
                conflict = "host,port,public_key";
            }
            string all = insert + ",updated_at,device_id,deleted_at";
            string updates = string.Join(',', (table.Columns + ",updated_at,device_id,deleted_at").Split(',').Where(c => c != "id").Select(c => $"{c}=excluded.{c}"));
            await conn.ExecuteAsync(new CommandDefinition($"INSERT INTO {table.Name} ({all},revision) VALUES ({string.Join(',', all.Split(',').Select(c => "@" + c))},1) ON CONFLICT({conflict}) DO UPDATE SET {updates},revision={table.Name}.revision+1;", args, tx, cancellationToken: ct));
            if (table.Type == EditorType)
            {
                await conn.ExecuteAsync(new CommandDefinition("UPDATE external_editor_paths SET deleted_at = @deleted WHERE editor_id = @id;", new { id, deleted = Date(entity.DeletedAtUtc ?? entity.UpdatedAtUtc) }, tx, cancellationToken: ct));
                foreach (var path in data.Paths ?? [])
                    await conn.ExecuteAsync(new CommandDefinition("INSERT INTO external_editor_paths(id,editor_id,os,path,deleted_at) VALUES (@pathId,@id,@os,@path,@deleted) ON CONFLICT(editor_id,os) DO UPDATE SET path=excluded.path,deleted_at=excluded.deleted_at;",
                        new { pathId = Guid.NewGuid().ToString(), id, os = path.Os, path = path.Path, deleted = Date(entity.DeletedAtUtc) }, tx, cancellationToken: ct));
            }
            if (table.Type is IdentityType or ProxyType)
            {
                if (vault != null) await vault.ImportSnapshotSecretAsync(conn, tx, table.Type == IdentityType, id, data.Secret, Date(entity.UpdatedAtUtc)!, Date(entity.DeletedAtUtc), ct);
                else if (data.Secret != null) throw new InvalidOperationException("同步秘密需要本机保险库");
            }
        }
        finally { if (data.Secret != null) CryptographicOperations.ZeroMemory(data.Secret); }
    }

    private static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null, JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetInt64(),
        _ => throw new JsonException("无效数据库同步值")
    };

    private static string KnownHostId(Dictionary<string, JsonElement> fields)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object?[]
            { fields["host"].GetString(), fields["port"].GetInt64(), fields["public_key"].GetString() }))));

    private static string? Date(DateTime? date) => date is { } d ? SqliteValue.FormatUtc(d) : null;

    public Task PurgeTombstonesBeforeAsync(DateTime cutoffUtc, CancellationToken ct = default)
        => new SqliteLocalChangeTracker(factory).PurgeTombstonesBeforeAsync(cutoffUtc, ct);
}
