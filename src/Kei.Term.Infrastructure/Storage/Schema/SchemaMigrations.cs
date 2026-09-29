namespace Kei.Term.Infrastructure.Storage.Schema;

using System.Text.Json;
using Dapper;
using Kei.Term.Core.Vault;
using Microsoft.Data.Sqlite;

// 全库 Schema 的唯一定义处。新增表/列 = 追加一条版本号 +1 的迁移，已发布的迁移永不修改。
public static class SchemaMigrations
{
    public static IReadOnlyList<SchemaMigration> All { get; } =
    [
        new(1, "baseline", ApplyBaselineAsync),
        new(2, "known_hosts", ApplyKnownHostsAsync),
    ];

    // v1 基线：必须幂等，兼容三类库——全新库、user_version=0 的现行库、更早的 credentials 旧库
    private static async Task ApplyBaselineAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        await ExecAsync(conn, tx, @"
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

            CREATE TABLE IF NOT EXISTS external_editors (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                arguments_template TEXT NOT NULL DEFAULT '""{path}""',
                is_default INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS external_editor_paths (
                id TEXT PRIMARY KEY NOT NULL,
                editor_id TEXT NOT NULL REFERENCES external_editors(id) ON DELETE CASCADE,
                os TEXT NOT NULL,
                path TEXT NOT NULL,
                UNIQUE(editor_id, os)
            );

            CREATE TABLE IF NOT EXISTS file_associations (
                id TEXT PRIMARY KEY NOT NULL,
                pattern TEXT NOT NULL,
                editor_id TEXT NOT NULL REFERENCES external_editors(id) ON DELETE CASCADE,
                priority INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_file_associations_priority ON file_associations(priority DESC);
        ", ct);

        // 更早版本的 tree_nodes 没有这两列
        await AddColumnIfMissingAsync(conn, tx, "tree_nodes", "protocol", "TEXT NOT NULL DEFAULT 'ssh'", ct);
        await AddColumnIfMissingAsync(conn, tx, "tree_nodes", "is_expanded", "INTEGER NOT NULL DEFAULT 0", ct);

        // 旧 credentials -> identities（旧表从未实现密钥材料，无秘密可迁）
        if (await TableExistsAsync(conn, tx, "credentials", ct))
        {
            await MigrateLegacyCredentialsAsync(conn, tx, ct);
        }

        // session_details：新库直接建；旧库 credential_id -> identity_id 需重建表以挂 FK
        if (!await TableExistsAsync(conn, tx, "session_details", ct))
        {
            await CreateSessionDetailsAsync(conn, tx, "session_details", ct);
        }
        else if (await ColumnExistsAsync(conn, tx, "session_details", "credential_id", ct))
        {
            await RebuildLegacySessionDetailsAsync(conn, tx, ct);
        }

        await AddColumnIfMissingAsync(conn, tx, "session_details", "terminal_profile_id", "TEXT", ct);
        await AddColumnIfMissingAsync(conn, tx, "session_details", "file_transfer_protocol", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(conn, tx, "session_details", "sftp_mode", "INTEGER NOT NULL DEFAULT 0", ct);

        // 目录组配置继承已废弃
        await ExecAsync(conn, tx, "DROP TABLE IF EXISTS folder_configs;", ct);
    }

    // v2 主机密钥信任库：一台主机可有多把密钥（不同算法 / 轮换期新旧并存 / 被吊销）
    private static Task ApplyKnownHostsAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
        => ExecAsync(conn, tx, @"
            CREATE TABLE IF NOT EXISTS known_hosts (
                id TEXT PRIMARY KEY NOT NULL,
                host TEXT NOT NULL,
                port INTEGER NOT NULL,
                key_type TEXT NOT NULL,
                public_key TEXT NOT NULL,
                fingerprint_sha256 TEXT NOT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                source INTEGER NOT NULL DEFAULT 0,
                comment TEXT,
                created_at TEXT NOT NULL,
                last_seen_at TEXT,
                UNIQUE(host, port, public_key)
            );

            CREATE INDEX IF NOT EXISTS idx_known_hosts_endpoint ON known_hosts(host, port);
        ", ct);

    private static async Task CreateSessionDetailsAsync(SqliteConnection conn, SqliteTransaction tx, string tableName, CancellationToken ct)
    {
        // tableName 仅为内部常量
        await ExecAsync(conn, tx, $@"
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
                file_transfer_protocol INTEGER NOT NULL DEFAULT 0,
                sftp_mode INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(node_id) REFERENCES tree_nodes(id) ON DELETE CASCADE,
                FOREIGN KEY(identity_id) REFERENCES identities(id) ON DELETE SET NULL
            );
        ", ct);
    }

    private static async Task RebuildLegacySessionDetailsAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        await ExecAsync(conn, tx, "DROP TABLE IF EXISTS session_details_migrated;", ct);
        await CreateSessionDetailsAsync(conn, tx, "session_details_migrated", ct);
        await ExecAsync(conn, tx, @"
            INSERT INTO session_details_migrated
                (node_id, host, port, username, identity_id, terminal_type, startup_script, jump_host_id, env_vars_json)
            SELECT node_id, host, port, username, credential_id, terminal_type, startup_script, jump_host_id, env_vars_json
            FROM session_details;

            DROP TABLE session_details;
            ALTER TABLE session_details_migrated RENAME TO session_details;
        ", ct);
    }

    private static async Task MigrateLegacyCredentialsAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        IEnumerable<LegacyCredentialRow> rows = await conn.QueryAsync<LegacyCredentialRow>(new CommandDefinition(@"
            SELECT id, name, description, cred_type, username, key_file_path, key_fingerprint, created_at, updated_at
            FROM credentials;
        ", transaction: tx, cancellationToken: ct));

        string now = SqliteValue.FormatUtc(DateTime.UtcNow);
        foreach (LegacyCredentialRow row in rows)
        {
            AuthMethodEntry method = MapLegacyMethod(row.CredType, row.KeyFilePath, row.KeyFingerprint);
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO identities (id, name, description, username, methods_json, created_at, updated_at)
                VALUES (@Id, @Name, @Description, @Username, @Methods, @CreatedAt, @UpdatedAt)
                ON CONFLICT(id) DO NOTHING;
            ", new
            {
                row.Id,
                row.Name,
                row.Description,
                row.Username,
                Methods = JsonSerializer.Serialize(new List<AuthMethodEntry> { method }),
                CreatedAt = row.CreatedAt ?? now,
                UpdatedAt = row.UpdatedAt ?? now
            }, transaction: tx, cancellationToken: ct));
        }

        await ExecAsync(conn, tx, "DROP TABLE credentials;", ct);
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

    private static Task ExecAsync(SqliteConnection conn, SqliteTransaction tx, string sql, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(sql, transaction: tx, cancellationToken: ct));

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, SqliteTransaction tx, string table, CancellationToken ct)
        => await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @table;",
            new { table }, transaction: tx, cancellationToken: ct)) != null;

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, SqliteTransaction tx, string table, string column, CancellationToken ct)
    {
        // pragma_table_info 表值函数可参数化，无需拼接表名
        long count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @column COLLATE NOCASE;",
            new { table, column }, transaction: tx, cancellationToken: ct));
        return count > 0;
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, tx, table, ct) || await ColumnExistsAsync(conn, tx, table, column, ct))
        {
            return;
        }

        // 表名/列名/定义均为内部常量
        await ExecAsync(conn, tx, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", ct);
    }

    private sealed class LegacyCredentialRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public long CredType { get; set; }
        public string? Username { get; set; }
        public string? KeyFilePath { get; set; }
        public string? KeyFingerprint { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }
}
