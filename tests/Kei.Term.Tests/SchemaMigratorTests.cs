using System;
using System.IO;
using System.Linq;
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
    public async Task GetNodeById_ReturnsNullForUnknownId()
    {
        var repo = new SqliteTreeRepository(ConnStr);
        await repo.InitializeAsync();

        Assert.Null(await repo.GetNodeByIdAsync(Guid.NewGuid()));
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
