namespace Kei.Term.Infrastructure.Storage;

using Microsoft.Data.Sqlite;
using System.Text.Json;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;

public class SqliteTreeRepository : ITreeRepository
{
    private readonly string _connectionString;

    public SqliteTreeRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    private async Task<SqliteConnection> CreateConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(ct);

        return conn;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);

        // 1. 基础节点表：新库直接带 protocol 列；旧库 IF NOT EXISTS 不生效，下面补列
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS tree_nodes (
                id TEXT PRIMARY KEY NOT NULL,
                parent_id TEXT,
                node_type INTEGER NOT NULL,
                name TEXT NOT NULL,
                description TEXT,
                sort_order INTEGER NOT NULL DEFAULT 0,
                protocol TEXT NOT NULL DEFAULT 'ssh',
                is_expanded INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(parent_id) REFERENCES tree_nodes(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_tree_nodes_parent ON tree_nodes(parent_id);

            CREATE TABLE IF NOT EXISTS identities (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                description TEXT,
                username TEXT,
                methods_json TEXT NOT NULL DEFAULT '[]',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS identity_secrets (
                identity_id TEXT PRIMARY KEY NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                secrets_blob BLOB NOT NULL,
                encryption_algorithm TEXT NOT NULL DEFAULT 'AES-256-GCM',
                nonce BLOB,
                tag BLOB
            );

            CREATE TABLE IF NOT EXISTS vault_metadata (
                key TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            );
        ";
        await cmd.ExecuteNonQueryAsync(ct);

        // 2. tree_nodes.protocol 与 is_expanded 幂等补列
        if (!await ColumnExistsAsync(conn, "tree_nodes", "protocol", ct))
        {
            var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE tree_nodes ADD COLUMN protocol TEXT NOT NULL DEFAULT 'ssh';";
            await alter.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(conn, "tree_nodes", "is_expanded", ct))
        {
            var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE tree_nodes ADD COLUMN is_expanded INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(ct);
        }

        // 3. 旧 credentials -> identities 迁移（迁移后 DROP 旧表）
        if (await TableExistsAsync(conn, "credentials", ct))
        {
            await MigrateCredentialsAsync(conn, ct);
        }

        // 4. session_details：新建（新库）或重建迁移（旧库 credential_id -> identity_id）
        if (!await TableExistsAsync(conn, "session_details", ct))
        {
            await CreateSessionDetailsAsync(conn, "session_details", ct);
        }
        else if (await ColumnExistsAsync(conn, "session_details", "credential_id", ct))
        {
            await RebuildSessionDetailsAsync(conn, ct);
        }

        // 4.1 幂等补列：terminal_profile_id
        if (await TableExistsAsync(conn, "session_details", ct) &&
            !await ColumnExistsAsync(conn, "session_details", "terminal_profile_id", ct))
        {
            var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE session_details ADD COLUMN terminal_profile_id TEXT;";
            await alter.ExecuteNonQueryAsync(ct);
        }

        // 5. 迁移：目录组配置继承已废弃，旧 folder_configs 表直接清除
        var dropCmd = conn.CreateCommand();
        dropCmd.CommandText = "DROP TABLE IF EXISTS folder_configs;";
        await dropCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateSessionDetailsAsync(SqliteConnection conn, string tableName, CancellationToken ct)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE {tableName} (
                node_id TEXT PRIMARY KEY NOT NULL,
                host TEXT NOT NULL,
                port INTEGER,
                username TEXT,
                identity_id TEXT,
                terminal_type TEXT NOT NULL DEFAULT 'xterm-256color',
                startup_script TEXT,
                jump_host_id TEXT,
                env_vars_json TEXT,
                terminal_profile_id TEXT,
                FOREIGN KEY(node_id) REFERENCES tree_nodes(id) ON DELETE CASCADE,
                FOREIGN KEY(identity_id) REFERENCES identities(id) ON DELETE SET NULL
            );
        ";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // 旧表 credential_id 改 identity_id（数据搬迁）+ 挂 FK；已迁移则调用方跳过
    private static async Task RebuildSessionDetailsAsync(SqliteConnection conn, CancellationToken ct)
    {
        // 先清理可能残留的中间表，保证迁移可重入
        var cleanup = conn.CreateCommand();
        cleanup.CommandText = "DROP TABLE IF EXISTS session_details_migrated;";
        await cleanup.ExecuteNonQueryAsync(ct);

        await CreateSessionDetailsAsync(conn, "session_details_migrated", ct);

        // 数据搬迁 + 换表在事务内完成
        using var work = conn.BeginTransaction();
        var copyCmd = conn.CreateCommand();
        copyCmd.Transaction = work;
        copyCmd.CommandText = @"
            INSERT INTO session_details_migrated
                (node_id, host, port, username, identity_id, terminal_type, startup_script, jump_host_id, env_vars_json)
            SELECT node_id, host, port, username, credential_id, terminal_type, startup_script, jump_host_id, env_vars_json
            FROM session_details;
        ";
        await copyCmd.ExecuteNonQueryAsync(ct);

        var drop = conn.CreateCommand();
        drop.Transaction = work;
        drop.CommandText = "DROP TABLE session_details;";
        await drop.ExecuteNonQueryAsync(ct);

        var rename = conn.CreateCommand();
        rename.Transaction = work;
        rename.CommandText = "ALTER TABLE session_details_migrated RENAME TO session_details;";
        await rename.ExecuteNonQueryAsync(ct);

        work.Commit();
    }

    // 旧凭据表仅有元数据、无秘密材料：逐行重建为「单方法」身份
    private static async Task MigrateCredentialsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var rows = new List<LegacyCredentialRow>();
        using (var select = conn.CreateCommand())
        {
            select.CommandText = @"
                SELECT id, name, description, cred_type, username, key_file_path, key_fingerprint, created_at, updated_at
                FROM credentials;
            ";
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new LegacyCredentialRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? DateTime.UtcNow.ToString("O") : reader.GetString(7),
                    reader.IsDBNull(8) ? DateTime.UtcNow.ToString("O") : reader.GetString(8)));
            }
        }

        using (var tx = conn.BeginTransaction())
        {
            foreach (var row in rows)
            {
                var method = MapLegacyMethod(row.CredType, row.KeyFilePath, row.KeyFingerprint);
                var methodsJson = JsonSerializer.Serialize(new List<AuthMethodEntry> { method });

                var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = @"
                    INSERT INTO identities (id, name, description, username, methods_json, created_at, updated_at)
                    VALUES ($id, $name, $description, $username, $methods, $createdAt, $updatedAt)
                    ON CONFLICT(id) DO NOTHING;
                ";
                insert.Parameters.AddWithValue("$id", row.Id);
                insert.Parameters.AddWithValue("$name", row.Name);
                insert.Parameters.AddWithValue("$description", (object?)row.Description ?? DBNull.Value);
                insert.Parameters.AddWithValue("$username", (object?)row.Username ?? DBNull.Value);
                insert.Parameters.AddWithValue("$methods", methodsJson);
                insert.Parameters.AddWithValue("$createdAt", row.CreatedAt);
                insert.Parameters.AddWithValue("$updatedAt", row.UpdatedAt);
                await insert.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }

        // 旧表从未实现密钥材料，无秘密可迁，迁移后直接删除
        var drop = conn.CreateCommand();
        drop.CommandText = "DROP TABLE credentials;";
        await drop.ExecuteNonQueryAsync(ct);
    }

    private static AuthMethodEntry MapLegacyMethod(long credType, string? keyFilePath, string? keyFingerprint) => credType switch
    {
        0 => new VaultPasswordMethod(),
        1 => string.IsNullOrWhiteSpace(keyFilePath)
            ? new VaultPrivateKeyMethod()
            : new FilePrivateKeyMethod { KeyFilePath = keyFilePath, PassphraseMode = PassphrasePersistence.AlwaysAsk },
        2 => new AgentMethod { AgentFingerprint = keyFingerprint },
        // 3 及其它一律视为交互式
        _ => new InteractiveMethod()
    };

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", table);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        // table 名为内部常量，无注入风险
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<IReadOnlyList<TreeNodeBase>> GetAllNodesAsync(CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);

        var list = new List<TreeNodeBase>();

        // 1. 读取基础节点（按节点类型：目录在前 0、会话在后 1；同类按 sort_order, name 排序）
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT t.id, t.parent_id, t.node_type, t.name, t.description, t.sort_order, t.created_at, t.updated_at,
                   t.protocol, t.is_expanded,
                   s.host, s.port, s.username, s.identity_id, s.terminal_type, s.startup_script, s.jump_host_id, s.env_vars_json,
                   s.terminal_profile_id
            FROM tree_nodes t
            LEFT JOIN session_details s ON t.id = s.node_id
            ORDER BY t.node_type ASC, t.sort_order ASC, t.name COLLATE NOCASE ASC;
        ";

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = Guid.Parse(reader.GetString(0));
            Guid? parentId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
            var nodeType = (NodeType)reader.GetInt32(2);
            var name = reader.GetString(3);
            var desc = reader.IsDBNull(4) ? null : reader.GetString(4);
            var sortOrder = reader.GetInt32(5);
            var createdAt = DateTime.Parse(reader.GetString(6));
            var updatedAt = DateTime.Parse(reader.GetString(7));
            var protocol = reader.IsDBNull(8) ? "ssh" : reader.GetString(8);
            var isExpanded = !reader.IsDBNull(9) && reader.GetInt32(9) != 0;

            if (nodeType == NodeType.Folder)
            {
                // 文件夹为纯分类容器，仅加载基础列与展开状态
                var folder = new FolderNode
                {
                    Id = id,
                    ParentId = parentId,
                    Name = name,
                    Description = desc,
                    SortOrder = sortOrder,
                    IsExpanded = isExpanded,
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt
                };
                list.Add(folder);
            }
            else
            {
                var envJson = reader.IsDBNull(17) ? null : reader.GetString(17);
                var envVars = string.IsNullOrEmpty(envJson)
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(envJson) ?? new();

                var session = new SessionNode
                {
                    Id = id,
                    ParentId = parentId,
                    Name = name,
                    Description = desc,
                    SortOrder = sortOrder,
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt,
                    Host = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                    Port = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    Username = reader.IsDBNull(12) ? null : reader.GetString(12),
                    IdentityId = reader.IsDBNull(13) ? null : Guid.Parse(reader.GetString(13)),
                    TerminalType = reader.IsDBNull(14) ? "xterm-256color" : reader.GetString(14),
                    StartupScript = reader.IsDBNull(15) ? null : reader.GetString(15),
                    JumpHostSessionId = reader.IsDBNull(16) ? null : Guid.Parse(reader.GetString(16)),
                    Protocol = protocol,
                    EnvironmentVariables = envVars,
                    TerminalProfileId = reader.IsDBNull(18) ? null : reader.GetString(18)
                };
                list.Add(session);
            }
        }

        return list;
    }

    public async Task<TreeNodeBase?> GetNodeByIdAsync(Guid id, CancellationToken ct = default)
    {
        var all = await GetAllNodesAsync(ct);
        return all.FirstOrDefault(n => n.Id == id);
    }

    public async Task SaveNodeAsync(TreeNodeBase node, CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);
        using var tx = conn.BeginTransaction();

        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO tree_nodes (id, parent_id, node_type, name, description, sort_order, protocol, is_expanded, created_at, updated_at)
            VALUES ($id, $parentId, $nodeType, $name, $description, $sortOrder, $protocol, $isExpanded, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                parent_id = $parentId,
                name = $name,
                description = $description,
                sort_order = $sortOrder,
                protocol = $protocol,
                is_expanded = $isExpanded,
                updated_at = $updatedAt;
        ";
        cmd.Parameters.AddWithValue("$id", node.Id.ToString());
        cmd.Parameters.AddWithValue("$parentId", (object?)node.ParentId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nodeType", (int)node.NodeType);
        cmd.Parameters.AddWithValue("$name", node.Name);
        cmd.Parameters.AddWithValue("$description", (object?)node.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sortOrder", node.SortOrder);
        cmd.Parameters.AddWithValue("$protocol", node is SessionNode p ? (string.IsNullOrWhiteSpace(p.Protocol) ? "ssh" : p.Protocol) : "ssh");
        cmd.Parameters.AddWithValue("$isExpanded", node is FolderNode f && f.IsExpanded ? 1 : 0);
        cmd.Parameters.AddWithValue("$createdAt", node.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);

        if (node is SessionNode session)
        {
            var sCmd = conn.CreateCommand();
            sCmd.Transaction = tx;
            sCmd.CommandText = @"
                INSERT INTO session_details (node_id, host, port, username, identity_id, terminal_type, startup_script, jump_host_id, env_vars_json, terminal_profile_id)
                VALUES ($nodeId, $host, $port, $username, $identityId, $terminalType, $startupScript, $jumpHostId, $envJson, $terminalProfileId)
                ON CONFLICT(node_id) DO UPDATE SET
                    host = $host,
                    port = $port,
                    username = $username,
                    identity_id = $identityId,
                    terminal_type = $terminalType,
                    startup_script = $startupScript,
                    jump_host_id = $jumpHostId,
                    env_vars_json = $envJson,
                    terminal_profile_id = $terminalProfileId;
            ";
            sCmd.Parameters.AddWithValue("$nodeId", session.Id.ToString());
            sCmd.Parameters.AddWithValue("$host", session.Host);
            sCmd.Parameters.AddWithValue("$port", (object?)session.Port ?? DBNull.Value);
            sCmd.Parameters.AddWithValue("$username", (object?)session.Username ?? DBNull.Value);
            sCmd.Parameters.AddWithValue("$identityId", (object?)session.IdentityId?.ToString() ?? DBNull.Value);
            sCmd.Parameters.AddWithValue("$terminalType", session.TerminalType);
            sCmd.Parameters.AddWithValue("$startupScript", (object?)session.StartupScript ?? DBNull.Value);
            sCmd.Parameters.AddWithValue("$jumpHostId", (object?)session.JumpHostSessionId?.ToString() ?? DBNull.Value);
            sCmd.Parameters.AddWithValue("$envJson", JsonSerializer.Serialize(session.EnvironmentVariables));
            sCmd.Parameters.AddWithValue("$terminalProfileId", (object?)session.TerminalProfileId ?? DBNull.Value);
            await sCmd.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
    }

    public async Task DeleteNodeAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tree_nodes WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MoveNodeAsync(Guid nodeId, Guid? newParentId, int sortOrder, CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE tree_nodes 
            SET parent_id = $parentId, sort_order = $sortOrder, updated_at = $updatedAt
            WHERE id = $id;
        ";
        cmd.Parameters.AddWithValue("$id", nodeId.ToString());
        cmd.Parameters.AddWithValue("$parentId", (object?)newParentId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sortOrder", sortOrder);
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateFolderExpandedAsync(Guid folderId, bool isExpanded, CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE tree_nodes
            SET is_expanded = $isExpanded, updated_at = $updatedAt
            WHERE id = $id AND node_type = $nodeType;
        ";
        cmd.Parameters.AddWithValue("$id", folderId.ToString());
        cmd.Parameters.AddWithValue("$isExpanded", isExpanded ? 1 : 0);
        cmd.Parameters.AddWithValue("$nodeType", (int)NodeType.Folder);
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed record LegacyCredentialRow(
        string Id,
        string Name,
        string? Description,
        long CredType,
        string? Username,
        string? KeyFilePath,
        string? KeyFingerprint,
        string CreatedAt,
        string UpdatedAt);
}
