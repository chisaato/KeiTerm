using System.Text.Json;
using Dapper;
using Kei.Term.Core.Models;
using Kei.Term.Core.Transfers;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;
using Microsoft.Data.Sqlite;

namespace Kei.Term.Tests;

public class SessionMetadataRepositoryTests
{
    [Fact]
    public async Task MigrationV8_AddsMetadataColumn_WithoutTouchingExistingRows()
    {
        string path = NewDbPath();
        string connectionString = "Data Source=" + path;
        try
        {
            await MigrateToVersionAsync(connectionString, 7);
            Guid sessionId = Guid.NewGuid();
            using (SqliteConnection seed = new(connectionString))
            {
                await seed.OpenAsync();
                Assert.False(await ColumnExistsAsync(seed, "metadata_json"));
                await seed.ExecuteAsync(@"
                    INSERT INTO tree_nodes (id, node_type, name, created_at, updated_at)
                    VALUES (@id, 1, 'legacy', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                    INSERT INTO session_details (node_id, host, username)
                    VALUES (@id, '10.0.0.8', 'ops');
                ", new { id = sessionId.ToString() });
            }

            SqliteConnectionFactory factory = new(connectionString);
            int version = await SchemaMigrator.MigrateAsync(factory);
            Assert.Equal(8, version);
            Assert.Equal(8, await SchemaMigrator.MigrateAsync(factory));

            using SqliteConnection conn = await factory.OpenAsync();
            Assert.True(await ColumnExistsAsync(conn, "metadata_json"));
            string? host = await conn.ExecuteScalarAsync<string>(
                "SELECT host FROM session_details WHERE node_id = @id;",
                new { id = sessionId.ToString() });
            Assert.Equal("10.0.0.8", host);
            object? metadata = await conn.ExecuteScalarAsync<object>(
                "SELECT metadata_json FROM session_details WHERE node_id = @id;",
                new { id = sessionId.ToString() });
            Assert.True(metadata is null or DBNull);
        }
        finally
        {
            DeleteDb(path);
        }
    }

    [Fact]
    public async Task Probe_RoundTrips_SurvivesSessionSave_AndCascadesOnDelete()
    {
        string path = NewDbPath();
        string connectionString = "Data Source=" + path;
        try
        {
            SqliteConnectionFactory factory = new(connectionString);
            await SchemaMigrator.MigrateAsync(factory);
            SqliteTreeRepository tree = new(factory);
            SqliteSessionMetadataRepository metadata = new(factory);
            SessionNode session = new()
            {
                Name = "web",
                Host = "web.example",
                Overrides = new SessionOverrides { FollowRemoteTitle = true },
            };
            await tree.SaveNodeAsync(session);

            using (SqliteConnection primed = await factory.OpenAsync())
            {
                await primed.ExecuteAsync(
                    "UPDATE session_details SET metadata_json = '{\"keep\":1}' WHERE node_id = @id;",
                    new { id = session.Id.ToString() });
            }

            DateTime probedAt = new(2026, 10, 18, 3, 4, 5, 6, DateTimeKind.Utc);
            TransferProbeResult probe = new(
                RemotePlatform.Linux,
                [TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Md5],
                [TransferArchiveFormat.Gzip, TransferArchiveFormat.Tar],
                probedAt);
            Assert.True(await metadata.SetTransferProbeAsync(session.Id, probe));
            Assert.False(await metadata.SetTransferProbeAsync(Guid.NewGuid(), probe));

            TransferProbeResult? loaded = await metadata.GetTransferProbeAsync(session.Id);
            Assert.NotNull(loaded);
            Assert.Equal(RemotePlatform.Linux, loaded.Platform);
            Assert.Equal(probe.Hashes, loaded.Hashes);
            Assert.Equal(probe.Archives, loaded.Archives);
            Assert.Equal(probedAt, loaded.ProbedAtUtc);

            using (SqliteConnection conn = await factory.OpenAsync())
            {
                string? json = await conn.ExecuteScalarAsync<string>(
                    "SELECT metadata_json FROM session_details WHERE node_id = @id;",
                    new { id = session.Id.ToString() });
                Assert.False(string.IsNullOrEmpty(json));
                using JsonDocument document = JsonDocument.Parse(json);
                Assert.Equal(1, document.RootElement.GetProperty("keep").GetInt32());
                string? probeType = await conn.ExecuteScalarAsync<string>(
                    "SELECT json_type(metadata_json, '$.transferProbe') FROM session_details WHERE node_id = @id;",
                    new { id = session.Id.ToString() });
                Assert.Equal("object", probeType);
                long revision = await conn.ExecuteScalarAsync<long>(
                    "SELECT revision FROM tree_nodes WHERE id = @id;",
                    new { id = session.Id.ToString() });
                Assert.Equal(0L, revision);

                await conn.ExecuteAsync(
                    "UPDATE session_details SET options_json = '{\"FollowRemoteTitle\":false}' WHERE node_id = @id;",
                    new { id = session.Id.ToString() });
            }

            SessionNode oldJson = Assert.IsType<SessionNode>(await tree.GetNodeByIdAsync(session.Id));
            Assert.False(oldJson.Overrides.FollowRemoteTitle);
            Assert.Null(oldJson.Overrides.VerifyHashEnabled);
            Assert.Null(oldJson.Overrides.ArchiveDirectoriesEnabled);

            string beforeSave;
            using (SqliteConnection before = await factory.OpenAsync())
            {
                beforeSave = await before.ExecuteScalarAsync<string>(
                    "SELECT metadata_json FROM session_details WHERE node_id = @id;",
                    new { id = session.Id.ToString() }) ?? string.Empty;
            }

            session.Overrides = new SessionOverrides { VerifyHashEnabled = false, ArchiveDirectoriesEnabled = true, FollowRemoteTitle = true };
            session.Host = "web2.example";
            await tree.SaveNodeAsync(session);

            string afterSave;
            using (SqliteConnection after = await factory.OpenAsync())
            {
                afterSave = await after.ExecuteScalarAsync<string>(
                    "SELECT metadata_json FROM session_details WHERE node_id = @id;",
                    new { id = session.Id.ToString() }) ?? string.Empty;
                long revision = await after.ExecuteScalarAsync<long>(
                    "SELECT revision FROM tree_nodes WHERE id = @id;",
                    new { id = session.Id.ToString() });
                Assert.Equal(0L, revision);
            }

            Assert.Equal(beforeSave, afterSave);
            SessionNode saved = Assert.IsType<SessionNode>(await tree.GetNodeByIdAsync(session.Id));
            Assert.Equal("web2.example", saved.Host);
            Assert.False(saved.Overrides.VerifyHashEnabled);
            Assert.True(saved.Overrides.ArchiveDirectoriesEnabled);
            Assert.True(saved.Overrides.FollowRemoteTitle);
            TransferProbeResult? stillThere = await metadata.GetTransferProbeAsync(session.Id);
            Assert.NotNull(stillThere);
            Assert.Equal(TransferHashAlgorithm.Sha256, stillThere.SelectHash([TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Md5]));

            using (SqliteConnection corrupt = await factory.OpenAsync())
            {
                await corrupt.ExecuteAsync(
                    "UPDATE session_details SET metadata_json = '{broken' WHERE node_id = @id;",
                    new { id = session.Id.ToString() });
            }

            Assert.Null(await metadata.GetTransferProbeAsync(session.Id));
            Assert.True(await metadata.SetTransferProbeAsync(session.Id, probe));
            Assert.NotNull(await metadata.GetTransferProbeAsync(session.Id));

            using (SqliteConnection unknown = await factory.OpenAsync())
            {
                await unknown.ExecuteAsync("""
                    UPDATE session_details
                    SET metadata_json = '{"keep":true,"transferProbe":{"platform":"Linux","hashes":["Sha256","Nope","Sha256","Md5"],"archives":["Nope","Gzip"],"probedAtUtc":"2026-10-18T03:04:05.0060000Z"}}'
                    WHERE node_id = @id;
                    """, new { id = session.Id.ToString() });
            }

            TransferProbeResult? cleaned = await metadata.GetTransferProbeAsync(session.Id);
            Assert.NotNull(cleaned);
            Assert.Equal([TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Md5], cleaned.Hashes);
            Assert.Equal([TransferArchiveFormat.Gzip], cleaned.Archives);

            await metadata.ClearTransferProbeAsync(session.Id);
            Assert.Null(await metadata.GetTransferProbeAsync(session.Id));
            using (SqliteConnection cleared = await factory.OpenAsync())
            {
                string? json = await cleared.ExecuteScalarAsync<string>(
                    "SELECT metadata_json FROM session_details WHERE node_id = @id;",
                    new { id = session.Id.ToString() });
                Assert.False(string.IsNullOrEmpty(json));
                using JsonDocument document = JsonDocument.Parse(json);
                Assert.True(document.RootElement.GetProperty("keep").GetBoolean());
                Assert.False(document.RootElement.TryGetProperty("transferProbe", out _));
            }

            await tree.DeleteNodeAsync(session.Id);
            Assert.Null(await metadata.GetTransferProbeAsync(session.Id));
            using SqliteConnection gone = await factory.OpenAsync();
            long rows = await gone.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM session_details WHERE node_id = @id;",
                new { id = session.Id.ToString() });
            Assert.Equal(0L, rows);
        }
        finally
        {
            DeleteDb(path);
        }
    }

    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), "keiterm-metadata-" + Guid.NewGuid().ToString("N") + ".db");

    private static void DeleteDb(string path)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string candidate = path + suffix;
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string column)
    {
        long count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pragma_table_info('session_details') WHERE name = @column;",
            new { column });
        return count > 0;
    }

    private static async Task MigrateToVersionAsync(string connectionString, int targetVersion)
    {
        using SqliteConnection conn = new(connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("PRAGMA journal_mode = WAL;");
        await conn.ExecuteAsync("PRAGMA foreign_keys = ON;");
        foreach (SchemaMigration migration in SchemaMigrations.All.Where(item => item.Version <= targetVersion))
        {
            using SqliteTransaction tx = conn.BeginTransaction(deferred: false);
            await migration.Apply(conn, tx, CancellationToken.None);
            await conn.ExecuteAsync(new CommandDefinition(
                $"PRAGMA user_version = {migration.Version};",
                transaction: tx));
            tx.Commit();
        }
    }
}
