using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.Core.Models;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

// 旧库迁移：credentials -> identities；session_details.credential_id -> identity_id；
// tree_nodes 补 protocol 列
public class SqliteMigrationTests
{
    private sealed record LegacySeed(Guid SessionId, Guid FileKeyCredentialId, Guid PasswordCredentialId);

    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), $"keiterm_migrate_{Guid.NewGuid():N}.db");

    // 构造旧结构数据库并写入一条文件私钥凭据、一条密码凭据、一个绑定凭据的会话
    private static async Task<LegacySeed> SeedLegacyAsync(string connStr)
    {
        var sessionId = Guid.NewGuid();
        var fileKeyCredentialId = Guid.NewGuid();
        var passwordCredentialId = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("O");

        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE tree_nodes (
                    id TEXT PRIMARY KEY NOT NULL,
                    parent_id TEXT,
                    node_type INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    description TEXT,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(parent_id) REFERENCES tree_nodes(id) ON DELETE CASCADE
                );
                CREATE TABLE session_details (
                    node_id TEXT PRIMARY KEY NOT NULL,
                    host TEXT NOT NULL,
                    port INTEGER,
                    username TEXT,
                    credential_id TEXT,
                    terminal_type TEXT NOT NULL DEFAULT 'xterm-256color',
                    startup_script TEXT,
                    jump_host_id TEXT,
                    env_vars_json TEXT,
                    FOREIGN KEY(node_id) REFERENCES tree_nodes(id) ON DELETE CASCADE
                );
                CREATE TABLE credentials (
                    id TEXT PRIMARY KEY NOT NULL,
                    name TEXT NOT NULL,
                    description TEXT,
                    cred_type INTEGER NOT NULL,
                    key_source INTEGER NOT NULL,
                    username TEXT,
                    key_fingerprint TEXT,
                    public_key_openssh TEXT,
                    key_file_path TEXT,
                    backend_id TEXT NOT NULL DEFAULT 'internal-vault',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
            ";
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                INSERT INTO tree_nodes (id, parent_id, node_type, name, description, sort_order, created_at, updated_at)
                VALUES ($sid, NULL, 1, 'Legacy Session', NULL, 0, $now, $now);
                INSERT INTO session_details (node_id, host, port, username, credential_id, terminal_type, startup_script, jump_host_id, env_vars_json)
                VALUES ($sid, '10.0.0.9', 22, 'ops', $fid, 'xterm-256color', NULL, NULL, NULL);
                INSERT INTO credentials (id, name, description, cred_type, key_source, username, key_fingerprint, public_key_openssh, key_file_path, backend_id, created_at, updated_at)
                VALUES ($fid, 'Legacy Key', '旧文件私钥', 1, 1, 'ops', 'SHA256:legacy', NULL, '/home/ops/.ssh/id_ed25519', 'internal-vault', $now, $now);
                INSERT INTO credentials (id, name, description, cred_type, key_source, username, key_fingerprint, public_key_openssh, key_file_path, backend_id, created_at, updated_at)
                VALUES ($pid, 'Legacy Password', NULL, 0, 0, 'admin', NULL, NULL, NULL, 'internal-vault', $now, $now);
            ";
            cmd.Parameters.AddWithValue("$sid", sessionId.ToString());
            cmd.Parameters.AddWithValue("$fid", fileKeyCredentialId.ToString());
            cmd.Parameters.AddWithValue("$pid", passwordCredentialId.ToString());
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        return new LegacySeed(sessionId, fileKeyCredentialId, passwordCredentialId);
    }

    private static async Task<bool> TableExistsRawAsync(string connStr, string table)
    {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
        cmd.Parameters.AddWithValue("$name", table);
        return await cmd.ExecuteScalarAsync() != null;
    }

    private static async Task<bool> ColumnExistsRawAsync(string connStr, string table, string column)
    {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task Initialize_MigratesLegacySchema_AndIsIdempotent()
    {
        var dbPath = NewDbPath();
        var connStr = $"Data Source={dbPath}";
        try
        {
            var seed = await SeedLegacyAsync(connStr);
            var repo = new SqliteTreeRepository(connStr);
            await repo.InitializeAsync();

            // credentials 已删除
            Assert.False(await TableExistsRawAsync(connStr, "credentials"));

            // identities 两条，元数据与单方法映射正确
            var identities = new SqliteIdentityRepository(connStr);
            var all = await identities.GetAllAsync();
            Assert.Equal(2, all.Count);

            var fileIdentity = all.Single(i => i.Id == seed.FileKeyCredentialId);
            Assert.Equal("Legacy Key", fileIdentity.Name);
            Assert.Equal("旧文件私钥", fileIdentity.Description);
            Assert.Equal("ops", fileIdentity.Username);
            var fileMethod = Assert.IsType<FilePrivateKeyMethod>(Assert.Single(fileIdentity.Methods));
            Assert.Equal("/home/ops/.ssh/id_ed25519", fileMethod.KeyFilePath);
            Assert.Equal(PassphrasePersistence.AlwaysAsk, fileMethod.PassphraseMode);

            var pwIdentity = all.Single(i => i.Id == seed.PasswordCredentialId);
            Assert.Equal("admin", pwIdentity.Username);
            Assert.IsType<VaultPasswordMethod>(Assert.Single(pwIdentity.Methods));

            // tree_nodes.protocol 列已补且旧行取默认 ssh
            Assert.True(await ColumnExistsRawAsync(connStr, "tree_nodes", "protocol"));

            // session_details.credential_id 改 identity_id（数据搬迁），且 protocol 读取正确
            Assert.False(await ColumnExistsRawAsync(connStr, "session_details", "credential_id"));
            Assert.True(await ColumnExistsRawAsync(connStr, "session_details", "identity_id"));

            var nodes = await repo.GetAllNodesAsync();
            var session = Assert.IsType<SessionNode>(nodes.Single(n => n.Id == seed.SessionId));
            Assert.Equal(seed.FileKeyCredentialId, session.IdentityId);
            Assert.Equal("ssh", session.Protocol);
            Assert.Null(session.TerminalProfileId);
            Assert.True(await ColumnExistsRawAsync(connStr, "session_details", "terminal_profile_id"));

            // 幂等：再次初始化不重复迁移、不丢失数据
            await repo.InitializeAsync();
            Assert.Equal(2, (await identities.GetAllAsync()).Count);
            var after = await repo.GetAllNodesAsync();
            Assert.Equal(seed.FileKeyCredentialId, Assert.IsType<SessionNode>(after.Single()).IdentityId);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Initialize_FreshDb_UsesNewSchema()
    {
        var dbPath = NewDbPath();
        var connStr = $"Data Source={dbPath}";
        try
        {
            var repo = new SqliteTreeRepository(connStr);
            await repo.InitializeAsync();

            // 新库直接具备 identity_id / protocol / terminal_profile_id，且无 credential_id / credentials
            Assert.True(await ColumnExistsRawAsync(connStr, "session_details", "identity_id"));
            Assert.False(await ColumnExistsRawAsync(connStr, "session_details", "credential_id"));
            Assert.True(await ColumnExistsRawAsync(connStr, "session_details", "terminal_profile_id"));
            Assert.True(await ColumnExistsRawAsync(connStr, "tree_nodes", "protocol"));
            Assert.False(await TableExistsRawAsync(connStr, "credentials"));
            Assert.True(await TableExistsRawAsync(connStr, "identities"));
            Assert.True(await TableExistsRawAsync(connStr, "identity_secrets"));
            Assert.True(await TableExistsRawAsync(connStr, "vault_metadata"));

            // 会话读写带 IdentityId 与 Protocol 往返
            var identity = new Identity { Name = "I" };
            await new SqliteIdentityRepository(connStr).SaveAsync(identity);

            var session = new SessionNode
            {
                Name = "S",
                Host = "h",
                IdentityId = identity.Id,
                Protocol = "ssh"
            };
            await repo.SaveNodeAsync(session);

            var loaded = Assert.IsType<SessionNode>(await repo.GetNodeByIdAsync(session.Id));
            Assert.Equal(identity.Id, loaded.IdentityId);
            Assert.Equal("ssh", loaded.Protocol);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
