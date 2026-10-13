namespace Kei.Term.Infrastructure.Vault;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Kei.Term.Core.Vault;

// 数据清单（IDataInventory）实现：固定七项的条数统计，以及已迁移旧身份密文的清理入口。
// 计数在单连接内读完、不取写锁、不解密；表缺失按 0 计，不抛。
// 清理与材料库迁移共用 _secretWriteLock，锁内重判标记后单事务删除，失败整体回滚。
public partial class InternalVaultManager
{
    public async Task<IReadOnlyList<DataInventoryItem>> LoadAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        using SqliteConnection conn = await CreateConnectionAsync(ct);

        // 会话只计 tree_nodes.node_type = 1（NodeType.Session），文件夹不算
        int sessions = await CountWhereAsync(conn, "tree_nodes", "node_type = 1", ct);
        int identities = await CountTableAsync(conn, "identities", ct);
        // 材料库与列表一致：不过滤 deleted_at
        int secrets = await CountTableAsync(conn, "vault_secrets", ct);

        (LegacySecretState legacyState, LegacySecretCleanup legacyCleanup, int legacyTotal, int legacyDeletable) =
            await ReadLegacySecretStateAsync(conn, ct);

        int proxies = await CountTableAsync(conn, "proxies", ct);
        int proxySecrets = await CountTableAsync(conn, "proxy_secrets", ct);

        int knownHosts = await CountTableAsync(conn, "known_hosts", ct);

        int editors = await CountTableAsync(conn, "external_editors", ct);
        int editorPaths = await CountTableAsync(conn, "external_editor_paths", ct);
        int associations = await CountTableAsync(conn, "file_associations", ct);

        return
        [
            new DataInventoryItem("sessions", sessions, [], null, LegacySecretCleanup.None, 0),
            new DataInventoryItem("identities", identities, [], null, LegacySecretCleanup.None, 0),
            new DataInventoryItem("secrets", secrets, [], null, LegacySecretCleanup.None, 0),
            // 仅此项带迁移状态与清理范围
            new DataInventoryItem("legacy-secrets", legacyTotal, [], legacyState, legacyCleanup, legacyDeletable),
            new DataInventoryItem("proxies", proxies, [proxySecrets], null, LegacySecretCleanup.None, 0),
            new DataInventoryItem("known-hosts", knownHosts, [], null, LegacySecretCleanup.None, 0),
            new DataInventoryItem("editors", editors, [editorPaths, associations], null, LegacySecretCleanup.None, 0),
        ];
    }

    public async Task<int> PurgeLegacyIdentitySecretsAsync(CancellationToken ct = default)
    {
        int deleted = 0;
        await WithSecretWriteLockAsync(async () =>
        {
            deleted = await PurgeLegacyIdentitySecretsCoreAsync(ct);
        }, ct);
        return deleted;
    }

    // 调用方持有 _secretWriteLock。锁内重新读标记与集合，按当时状态单事务删除。
    private async Task<int> PurgeLegacyIdentitySecretsCoreAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        using SqliteConnection conn = await CreateConnectionAsync(ct);

        string? marker = await ReadMetadataValueAsync(conn, LegacySecretMigrator.MarkerKey, ct);
        bool complete = marker == "1";
        // 已完成时全部行都是旧副本，含无法解析的 Id；否则只处理已迁移集合内的行
        HashSet<Guid> migrated = complete ? [] : await ReadMigratedIdentitySetAsync(conn, ct);

        using SqliteTransaction tx = conn.BeginTransaction();
        int deleted;
        if (complete)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM identity_secrets;";
            deleted = await cmd.ExecuteNonQueryAsync(ct);
        }
        else if (migrated.Count == 0)
        {
            // 未开始，或集合缺失/空/损坏：一条都不删
            deleted = 0;
        }
        else
        {
            deleted = await DeleteMigratedRowsAsync(conn, tx, migrated, ct);
        }

        tx.Commit();
        // 只记模式与行数，绝不记录密文
        _logger.LogInformation("清理旧身份密文 模式={Mode} 删除行数={Count}", complete ? "全部" : "仅已迁移", deleted);
        return deleted;
    }

    // 只有身份 Id 落在已迁移集合内的行才删；集合外的行与无法解析的 Id 留下
    private static async Task<int> DeleteMigratedRowsAsync(
        SqliteConnection conn, SqliteTransaction tx, HashSet<Guid> migrated, CancellationToken ct)
    {
        string[] ids = migrated.Select(id => id.ToString()).ToArray();
        string[] names = new string[ids.Length];
        var sql = new StringBuilder("DELETE FROM identity_secrets WHERE identity_id IN (");
        for (int i = 0; i < ids.Length; i++)
        {
            names[i] = "$id" + i.ToString(CultureInfo.InvariantCulture);
            if (i > 0)
            {
                sql.Append(", ");
            }
            sql.Append(names[i]);
        }
        sql.Append(");");

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql.ToString();
        for (int i = 0; i < ids.Length; i++)
        {
            cmd.Parameters.AddWithValue(names[i], ids[i]);
        }
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // 旧身份密文状态：完成标记 "1" → 全部可删；否则集合非空 → 只删已迁移行；否则不删
    private static async Task<(LegacySecretState State, LegacySecretCleanup Cleanup, int Total, int Deletable)>
        ReadLegacySecretStateAsync(SqliteConnection conn, CancellationToken ct)
    {
        int total = await CountTableAsync(conn, "identity_secrets", ct);
        string? marker = await ReadMetadataValueAsync(conn, LegacySecretMigrator.MarkerKey, ct);

        if (marker == "1")
        {
            return (LegacySecretState.Complete, LegacySecretCleanup.AllRows, total, total);
        }

        HashSet<Guid> migrated = await ReadMigratedIdentitySetAsync(conn, ct);
        if (migrated.Count == 0)
        {
            return (LegacySecretState.NotStarted, LegacySecretCleanup.None, total, 0);
        }

        // 表为空时无需再扫；否则统计能解析且落在集合内的行
        int deletable = total == 0 ? 0 : await CountMigratedRowsAsync(conn, migrated, ct);
        return (LegacySecretState.Partial, LegacySecretCleanup.MigratedRowsOnly, total, deletable);
    }

    private static async Task<int> CountMigratedRowsAsync(SqliteConnection conn, HashSet<Guid> migrated, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT identity_id FROM identity_secrets;";
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        int count = 0;
        while (await reader.ReadAsync(ct))
        {
            if (Guid.TryParse(reader.GetString(0), out Guid id) && migrated.Contains(id))
            {
                count++;
            }
        }
        return count;
    }

    // 读取已迁移身份集合；键缺失、空或 JSON 损坏一律按空集（不得据此删行）
    private static async Task<HashSet<Guid>> ReadMigratedIdentitySetAsync(SqliteConnection conn, CancellationToken ct)
    {
        string? json = await ReadMetadataValueAsync(conn, LegacySecretMigrator.MigratedIdentitiesKey, ct);
        var set = new HashSet<Guid>();
        if (string.IsNullOrEmpty(json))
        {
            return set;
        }

        try
        {
            List<string>? ids = JsonSerializer.Deserialize<List<string>>(json);
            if (ids != null)
            {
                foreach (string value in ids)
                {
                    if (Guid.TryParse(value, out Guid identityId))
                    {
                        set.Add(identityId);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 集合损坏：按空集处理，宁可少删也不误删
        }

        return set;
    }

    private static async Task<int> CountTableAsync(SqliteConnection conn, string table, CancellationToken ct)
        => await CountWhereAsync(conn, table, null, ct);

    // 表名与过滤条件均为内部常量。半残库缺表时按 0 计，不阻断清单。
    private static async Task<int> CountWhereAsync(SqliteConnection conn, string table, string? predicate, CancellationToken ct)
    {
        try
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = predicate == null
                ? $"SELECT COUNT(*) FROM {table};"
                : $"SELECT COUNT(*) FROM {table} WHERE {predicate};";
            object? value = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
    }

    private static async Task<string?> ReadMetadataValueAsync(SqliteConnection conn, string key, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM vault_metadata WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
