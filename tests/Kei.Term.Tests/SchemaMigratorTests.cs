using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Kei.Term.Core.Models;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

// 版本化迁移器 + 连接工厂 + 持久化健壮性
public class SchemaMigratorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"keiterm_schema_{Guid.NewGuid():N}.db");

    private string ConnStr => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    [Fact]
    public async Task FreshDatabase_MigratesToLatestVersion_AndIsIdempotent()
    {
        var factory = new SqliteConnectionFactory(ConnStr);

        var first = await SchemaMigrator.MigrateAsync(factory);
        var second = await SchemaMigrator.MigrateAsync(factory);

        Assert.Equal(SchemaMigrator.LatestVersion, first);
        Assert.Equal(first, second);

        using var conn = await factory.OpenAsync();
        var tables = (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table';")).ToHashSet();
        Assert.Contains("tree_nodes", tables);
        Assert.Contains("session_details", tables);
        Assert.Contains("identities", tables);
        Assert.Contains("external_editors", tables);
        Assert.Contains("known_hosts", tables);
    }

    [Fact]
    public async Task ConnectionFactory_EnablesForeignKeysAndWal()
    {
        var factory = new SqliteConnectionFactory(ConnStr);
        await SchemaMigrator.MigrateAsync(factory);

        using var conn = await factory.OpenAsync();
        Assert.Equal(1L, await conn.ExecuteScalarAsync<long>("PRAGMA foreign_keys;"));
        Assert.Equal("wal", await conn.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
        Assert.True(await conn.ExecuteScalarAsync<long>("PRAGMA busy_timeout;") > 0);
    }

    [Fact]
    public async Task UnversionedExistingDatabase_IsAdoptedByBaselineWithoutDataLoss()
    {
        // 模拟 user_version=0 的现行库：表已存在且有数据，但缺少后加的列
        using (var conn = new SqliteConnection(ConnStr))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(@"
                CREATE TABLE tree_nodes (
                    id TEXT PRIMARY KEY NOT NULL, parent_id TEXT, node_type INTEGER NOT NULL, name TEXT NOT NULL,
                    description TEXT, sort_order INTEGER NOT NULL DEFAULT 0, protocol TEXT NOT NULL DEFAULT 'ssh',
                    is_expanded INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE identities (id TEXT PRIMARY KEY NOT NULL, name TEXT NOT NULL, description TEXT, username TEXT,
                    methods_json TEXT NOT NULL DEFAULT '[]', created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE session_details (
                    node_id TEXT PRIMARY KEY NOT NULL, host TEXT NOT NULL, port INTEGER, username TEXT, identity_id TEXT,
                    terminal_type TEXT NOT NULL DEFAULT 'xterm-256color', startup_script TEXT, jump_host_id TEXT, env_vars_json TEXT,
                    FOREIGN KEY(node_id) REFERENCES tree_nodes(id) ON DELETE CASCADE,
                    FOREIGN KEY(identity_id) REFERENCES identities(id) ON DELETE SET NULL);
                INSERT INTO tree_nodes (id, node_type, name, created_at, updated_at)
                    VALUES ('11111111-1111-1111-1111-111111111111', 1, 'legacy', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO session_details (node_id, host) VALUES ('11111111-1111-1111-1111-111111111111', 'legacy.example');
            ");
        }

        var repo = new SqliteTreeRepository(ConnStr);
        await repo.InitializeAsync();

        var node = Assert.IsType<SessionNode>(await repo.GetNodeByIdAsync(Guid.Parse("11111111-1111-1111-1111-111111111111")));
        Assert.Equal("legacy.example", node.Host);
        Assert.Equal(FileTransferProtocol.Sftp, node.FileTransferProtocol);
        Assert.Null(node.TerminalProfileId);
    }

    [Fact]
    public async Task TimestampsRoundTripAsUtc()
    {
        var repo = new SqliteTreeRepository(ConnStr);
        await repo.InitializeAsync();

        var created = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var folder = new FolderNode { Name = "f", CreatedAt = created };
        await repo.SaveNodeAsync(folder);

        var loaded = await repo.GetNodeByIdAsync(folder.Id);
        Assert.NotNull(loaded);
        Assert.Equal(DateTimeKind.Utc, loaded!.CreatedAt.Kind);
        Assert.Equal(created, loaded.CreatedAt);
    }

    [Fact]
    public void MigrationList_V1ToV6_NamesAndOrderAreFrozen()
    {
        (int Version, string Name)[] expected =
        [
            (1, "baseline"),
            (2, "known_hosts"),
            (3, "session_options"),
            (4, "proxies"),
            (5, "port_forwards"),
            (6, "proxy_secrets"),
        ];

        (int Version, string Name)[] actual = SchemaMigrations.All
            .Where(m => m.Version <= 6)
            .Select(m => (m.Version, m.Name))
            .ToArray();

        Assert.Equal(expected, actual);

        // Apply 委托也要锁死：把某条迁移的委托指向别的方法必须失败
        string[] expectedApplyNames =
        [
            "ApplyBaselineAsync",
            "ApplyKnownHostsAsync",
            "ApplySessionOptionsAsync",
            "ApplyProxiesAsync",
            "ApplyPortForwardsAsync",
            "ApplyProxySecretsAsync",
        ];
        string[] actualApplyNames = SchemaMigrations.All
            .Where(m => m.Version <= 6)
            .Select(m => m.Apply.Method.Name)
            .ToArray();
        Assert.Equal(expectedApplyNames, actualApplyNames);

        // v7 是本次追加的唯一一项，且号在末尾
        Assert.Equal(7, SchemaMigrations.All[^1].Version);
        Assert.Equal("sync_columns", SchemaMigrations.All[^1].Name);
    }

    [Fact]
    public async Task UpgradeFromV6_AddsSyncColumnsWithDefaults_AndLeavesExcludedTablesAlone()
    {
        string[] syncableTables =
        [
            "tree_nodes",
            "identities",
            "proxies",
            "port_forwards",
            "external_editors",
            "file_associations",
        ];
        string[] excludedTables =
        [
            "identity_secrets",
            "proxy_secrets",
            "vault_metadata",
            "known_hosts",
            "external_editor_paths",
        ];

        // 只迁到 v6，模拟尚未带来同步列的旧库
        await MigrateToVersionAsync(ConnStr, 6);

        using (var pre = new SqliteConnection(ConnStr))
        {
            await pre.OpenAsync();
            Assert.Equal(6, await SchemaMigrator.GetUserVersionAsync(pre, null, CancellationToken.None));

            // 旧迁移若擅自引入这两列，这里会先失败
            foreach (string table in syncableTables)
            {
                long count = await pre.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name IN ('revision', 'deleted_at');",
                    new { table });
                Assert.Equal(0L, count);
            }
        }

        using (var seed = new SqliteConnection(ConnStr))
        {
            await seed.OpenAsync();
            await seed.ExecuteAsync(@"
                INSERT INTO tree_nodes (id, node_type, name, created_at, updated_at)
                    VALUES ('aaaaaaaa-0000-0000-0000-000000000001', 1, 'node', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO identities (id, name, created_at, updated_at)
                    VALUES ('bbbbbbbb-0000-0000-0000-000000000002', 'id1', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO proxies (id, name, config_json, created_at, updated_at)
                    VALUES ('cccccccc-0000-0000-0000-000000000003', 'p1', '{}', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO port_forwards (id, session_id, mode, bind_address, listen_port)
                    VALUES ('dddddddd-0000-0000-0000-000000000004', 'aaaaaaaa-0000-0000-0000-000000000001', 'local', '127.0.0.1', 8080);
                INSERT INTO external_editors (id, name, created_at, updated_at)
                    VALUES ('eeeeeeee-0000-0000-0000-000000000005', 'ed', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO file_associations (id, pattern, editor_id, created_at, updated_at)
                    VALUES ('ffffffff-0000-0000-0000-000000000006', '*.txt', 'eeeeeeee-0000-0000-0000-000000000005', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
            ");
        }

        var factory = new SqliteConnectionFactory(ConnStr);
        int version = await SchemaMigrator.MigrateAsync(factory);
        Assert.Equal(7, version);

        using var conn = await factory.OpenAsync();
        foreach (string table in syncableTables)
        {
            // 已存在的行经 ALTER TABLE ADD COLUMN 后取列默认值
            long revision = await conn.ExecuteScalarAsync<long>($"SELECT revision FROM {table};");
            Assert.Equal(0L, revision);

            object? deletedAt = await conn.ExecuteScalarAsync<object?>($"SELECT deleted_at FROM {table};");
            Assert.True(deletedAt is null || deletedAt is DBNull);

            // 新插入的行同样有默认值
            long columnCount = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name IN ('revision', 'deleted_at');",
                new { table });
            Assert.Equal(2L, columnCount);
        }

        foreach (string table in excludedTables)
        {
            long count = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name IN ('revision', 'deleted_at');",
                new { table });
            Assert.Equal(0L, count);
        }
    }

    // v6 已发布 schema 的冻结快照：表名 + 每张表的列（name/type/notnull/dflt_value/pk）。
    // 期望值由本机把空库迁到 v6 后从 sqlite_master / pragma_table_info 抄写，user_version 确认停在 6。
    private static readonly (string Table, (string Name, string Type, long NotNull, string? Dflt, long Pk)[] Columns)[] V6SchemaSnapshot =
    [
        ("external_editor_paths",
        [
            ("id", "TEXT", 1, null, 1),
            ("editor_id", "TEXT", 1, null, 0),
            ("os", "TEXT", 1, null, 0),
            ("path", "TEXT", 1, null, 0),
        ]),
        ("external_editors",
        [
            ("id", "TEXT", 1, null, 1),
            ("name", "TEXT", 1, null, 0),
            ("arguments_template", "TEXT", 1, "'\"{path}\"'", 0),
            ("is_default", "INTEGER", 1, "0", 0),
            ("created_at", "TEXT", 1, null, 0),
            ("updated_at", "TEXT", 1, null, 0),
        ]),
        ("file_associations",
        [
            ("id", "TEXT", 1, null, 1),
            ("pattern", "TEXT", 1, null, 0),
            ("editor_id", "TEXT", 1, null, 0),
            ("priority", "INTEGER", 1, "0", 0),
            ("created_at", "TEXT", 1, null, 0),
            ("updated_at", "TEXT", 1, null, 0),
        ]),
        ("identities",
        [
            ("id", "TEXT", 1, null, 1),
            ("name", "TEXT", 1, null, 0),
            ("description", "TEXT", 0, null, 0),
            ("username", "TEXT", 0, null, 0),
            ("methods_json", "TEXT", 1, "'[]'", 0),
            ("created_at", "TEXT", 1, null, 0),
            ("updated_at", "TEXT", 1, null, 0),
        ]),
        ("identity_secrets",
        [
            ("identity_id", "TEXT", 1, null, 1),
            ("secrets_blob", "BLOB", 1, null, 0),
            ("encryption_algorithm", "TEXT", 1, "'AES-256-GCM'", 0),
            ("nonce", "BLOB", 0, null, 0),
            ("tag", "BLOB", 0, null, 0),
        ]),
        ("known_hosts",
        [
            ("id", "TEXT", 1, null, 1),
            ("host", "TEXT", 1, null, 0),
            ("port", "INTEGER", 1, null, 0),
            ("key_type", "TEXT", 1, null, 0),
            ("public_key", "TEXT", 1, null, 0),
            ("fingerprint_sha256", "TEXT", 1, null, 0),
            ("status", "INTEGER", 1, "0", 0),
            ("source", "INTEGER", 1, "0", 0),
            ("comment", "TEXT", 0, null, 0),
            ("created_at", "TEXT", 1, null, 0),
            ("last_seen_at", "TEXT", 0, null, 0),
        ]),
        ("port_forwards",
        [
            ("id", "TEXT", 1, null, 1),
            ("session_id", "TEXT", 1, null, 0),
            ("name", "TEXT", 0, null, 0),
            ("mode", "TEXT", 1, null, 0),
            ("bind_address", "TEXT", 1, null, 0),
            ("listen_port", "INTEGER", 1, null, 0),
            ("destination_host", "TEXT", 0, null, 0),
            ("destination_port", "INTEGER", 0, null, 0),
        ]),
        ("proxies",
        [
            ("id", "TEXT", 1, null, 1),
            ("name", "TEXT", 1, null, 0),
            ("sort_order", "INTEGER", 1, "0", 0),
            ("config_json", "TEXT", 1, null, 0),
            ("created_at", "TEXT", 1, null, 0),
            ("updated_at", "TEXT", 1, null, 0),
        ]),
        ("proxy_secrets",
        [
            ("proxy_id", "TEXT", 1, null, 1),
            ("secrets_blob", "BLOB", 1, null, 0),
            ("encryption_algorithm", "TEXT", 1, null, 0),
            ("nonce", "BLOB", 0, null, 0),
            ("tag", "BLOB", 0, null, 0),
        ]),
        ("session_details",
        [
            ("node_id", "TEXT", 1, null, 1),
            ("host", "TEXT", 1, null, 0),
            ("port", "INTEGER", 0, null, 0),
            ("username", "TEXT", 0, null, 0),
            ("identity_id", "TEXT", 0, null, 0),
            ("terminal_type", "TEXT", 1, "'xterm-256color'", 0),
            ("startup_script", "TEXT", 0, null, 0),
            ("jump_host_id", "TEXT", 0, null, 0),
            ("env_vars_json", "TEXT", 0, null, 0),
            ("terminal_profile_id", "TEXT", 0, null, 0),
            ("file_transfer_protocol", "INTEGER", 1, "0", 0),
            ("sftp_mode", "INTEGER", 1, "0", 0),
            ("options_json", "TEXT", 0, null, 0),
            ("proxy_json", "TEXT", 0, null, 0),
        ]),
        ("tree_nodes",
        [
            ("id", "TEXT", 1, null, 1),
            ("parent_id", "TEXT", 0, null, 0),
            ("node_type", "INTEGER", 1, null, 0),
            ("name", "TEXT", 1, null, 0),
            ("description", "TEXT", 0, null, 0),
            ("sort_order", "INTEGER", 1, "0", 0),
            ("protocol", "TEXT", 1, "'ssh'", 0),
            ("is_expanded", "INTEGER", 1, "0", 0),
            ("created_at", "TEXT", 1, null, 0),
            ("updated_at", "TEXT", 1, null, 0),
        ]),
        ("vault_metadata",
        [
            ("key", "TEXT", 1, null, 1),
            ("value", "TEXT", 1, null, 0),
        ]),
    ];

    [Fact]
    public async Task V6Schema_IsFrozen_FullSnapshotOfPublishedColumns()
    {
        await MigrateToVersionAsync(ConnStr, 6);

        using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        Assert.Equal(6, await SchemaMigrator.GetUserVersionAsync(conn, null, CancellationToken.None));

        var actualTables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;")).ToList();
        Assert.Equal(V6SchemaSnapshot.Select(t => t.Table).ToArray(), actualTables);

        foreach ((string table, (string Name, string Type, long NotNull, string? Dflt, long Pk)[] expected) in V6SchemaSnapshot)
        {
            var columns = (await conn.QueryAsync<ColumnInfo>(new CommandDefinition(
                "SELECT name AS Name, type AS Type, \"notnull\" AS \"NotNull\", dflt_value AS DfltValue, pk AS Pk "
                + "FROM pragma_table_info(@table) ORDER BY cid;",
                new { table }))).ToList();

            Assert.Equal(expected.Length, columns.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].Name, columns[i].Name);
                Assert.Equal(expected[i].Type, columns[i].Type);
                Assert.Equal(expected[i].NotNull, columns[i].NotNull);
                Assert.Equal(expected[i].Dflt, columns[i].DfltValue);
                Assert.Equal(expected[i].Pk, columns[i].Pk);
            }

            // v7 的同步列不得回灌进 v6 快照
            Assert.DoesNotContain(columns, c => c.Name is "revision" or "deleted_at");
        }
    }

    private sealed class ColumnInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public long NotNull { get; set; }
        public string? DfltValue { get; set; }
        public long Pk { get; set; }
    }

    // 只用 SchemaMigrations.All 的公开 Apply 逐条推进，复刻 SchemaMigrator 的事务/版本语义
    private static async Task MigrateToVersionAsync(string connStr, int targetVersion)
    {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        await conn.ExecuteAsync("PRAGMA journal_mode = WAL;");
        await conn.ExecuteAsync("PRAGMA foreign_keys = ON;");

        foreach (SchemaMigration migration in SchemaMigrations.All.Where(m => m.Version <= targetVersion))
        {
            using SqliteTransaction tx = conn.BeginTransaction(deferred: false);
            await migration.Apply(conn, tx, CancellationToken.None);
            await conn.ExecuteAsync(new CommandDefinition(
                $"PRAGMA user_version = {migration.Version};", transaction: tx));
            tx.Commit();
        }
    }

    [Fact]
    public async Task SettingsService_BacksUpCorruptFileAndSavesAtomically()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"keiterm_settings_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            await File.WriteAllTextAsync(path, "{ \"FontSize\": 1"); // 截断的 JSON

            var service = new JsonSettingsService(path);
            var loaded = await service.LoadSettingsAsync();

            Assert.Equal(14.0, loaded.FontSize);
            Assert.Single(Directory.GetFiles(dir, "settings.json.corrupt-*"));

            loaded.FontSize = 18;
            await service.SaveSettingsAsync(loaded);

            Assert.False(File.Exists(path + ".tmp"));
            var reloaded = await new JsonSettingsService(path).LoadSettingsAsync();
            Assert.Equal(18.0, reloaded.FontSize);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
