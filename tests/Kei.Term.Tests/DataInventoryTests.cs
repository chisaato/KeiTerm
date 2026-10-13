using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kei.Term.Tests;

// 数据清单：固定七项条数，以及旧身份密文清理的范围判定（未开始 / 部分 / 完成）。
public class DataInventoryTests : IDisposable
{
    private readonly string _path = VaultTestDb.NewPath();
    private string Connection => $"Data Source={_path}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch { }
        }
    }

    private async Task SeedIdentityAsync(Guid identityId, string name, params AuthMethodEntry[] methods)
    {
        await VaultTestDb.CreateAsync(Connection, identityId);
        await new SqliteIdentityRepository(Connection).SaveAsync(new Identity { Id = identityId, Name = name, Methods = [.. methods] });
    }

    private async Task ExecAsync(string sql, object? param = null)
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql, param);
    }

    private async Task<int> CountAsync(string table)
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table};");
    }

    private async Task InsertVaultSecretAsync(Guid id)
    {
        string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await ExecAsync(
            "INSERT INTO vault_secrets (id, name, kind, meta_json, secret_blob, encryption_algorithm, nonce, tag, created_at, updated_at) " +
            "VALUES (@id, 'entry', 'password', NULL, @blob, 'PLAIN', NULL, NULL, @now, @now);",
            new { id = id.ToString(), blob = new byte[] { 1 }, now });
    }

    // 插入无法解析的 identity_id 旧行：临时关掉外键（Microsoft.Data.Sqlite 默认开启）
    private async Task InsertUnparseableLegacyRowAsync()
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        await conn.ExecuteAsync("PRAGMA foreign_keys = OFF;");
        await conn.ExecuteAsync(
            "INSERT INTO identity_secrets (identity_id, secrets_blob, encryption_algorithm, nonce, tag) VALUES ('not-a-guid', @blob, 'PLAIN', NULL, NULL);",
            new { blob = new byte[] { 1 } });
    }

    [Fact]
    public async Task Load_CountsSessionsNotFolders_AndOtherItems()
    {
        var identityId = Guid.NewGuid();
        await VaultTestDb.CreateAsync(Connection, identityId);

        string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        // 1 个文件夹 + 2 个会话：文件夹不得计入会话
        await ExecAsync(
            "INSERT INTO tree_nodes (id, parent_id, node_type, name, description, sort_order, protocol, is_expanded, created_at, updated_at) " +
            "VALUES (@id, NULL, 0, 'Folder', NULL, 0, 'ssh', 0, @now, @now);",
            new { id = Guid.NewGuid().ToString(), now });
        for (int i = 0; i < 2; i++)
        {
            await ExecAsync(
                "INSERT INTO tree_nodes (id, parent_id, node_type, name, description, sort_order, protocol, is_expanded, created_at, updated_at) " +
                "VALUES (@id, NULL, 1, @name, NULL, 0, 'ssh', 0, @now, @now);",
                new { id = Guid.NewGuid().ToString(), name = $"Session {i}", now });
        }

        await InsertVaultSecretAsync(Guid.NewGuid());

        var proxyId = Guid.NewGuid();
        await ExecAsync(
            "INSERT INTO proxies (id, name, sort_order, config_json, created_at, updated_at) VALUES (@id, 'P', 0, '{}', @now, @now);",
            new { id = proxyId.ToString(), now });
        await ExecAsync(
            "INSERT INTO proxy_secrets (proxy_id, secrets_blob, encryption_algorithm, nonce, tag) VALUES (@id, @blob, 'PLAIN', NULL, NULL);",
            new { id = proxyId.ToString(), blob = new byte[] { 1 } });

        await ExecAsync(
            "INSERT INTO known_hosts (id, host, port, key_type, public_key, fingerprint_sha256, status, source, comment, created_at, last_seen_at) " +
            "VALUES (@id, 'host', 22, 'ssh-ed25519', 'AAA', 'fp', 0, 0, NULL, @now, NULL);",
            new { id = Guid.NewGuid().ToString(), now });

        var editorId = Guid.NewGuid();
        await ExecAsync(
            "INSERT INTO external_editors (id, name, arguments_template, is_default, created_at, updated_at) VALUES (@id, 'E', '\"{path}\"', 0, @now, @now);",
            new { id = editorId.ToString(), now });
        await ExecAsync(
            "INSERT INTO external_editor_paths (id, editor_id, os, path) VALUES (@id, @editor, 'linux', '/usr/bin/e');",
            new { id = Guid.NewGuid().ToString(), editor = editorId.ToString() });
        await ExecAsync(
            "INSERT INTO file_associations (id, pattern, editor_id, priority, created_at, updated_at) VALUES (@id, '*.txt', @editor, 0, @now, @now);",
            new { id = Guid.NewGuid().ToString(), editor = editorId.ToString(), now });

        var vault = VaultTestDb.CreateVault(Connection);
        IReadOnlyList<DataInventoryItem> items = await vault.LoadAsync();

        Assert.Equal(
            new[] { "sessions", "identities", "secrets", "legacy-secrets", "proxies", "known-hosts", "editors" },
            items.Select(i => i.Id).ToArray());

        Assert.Equal(2, items.Single(i => i.Id == "sessions").Count);      // 文件夹不计
        Assert.Equal(1, items.Single(i => i.Id == "identities").Count);
        Assert.Equal(1, items.Single(i => i.Id == "secrets").Count);

        DataInventoryItem proxies = items.Single(i => i.Id == "proxies");
        Assert.Equal(1, proxies.Count);
        Assert.Equal(new[] { 1 }, proxies.ExtraCounts);                    // proxy_secrets

        DataInventoryItem editors = items.Single(i => i.Id == "editors");
        Assert.Equal(1, editors.Count);
        Assert.Equal(new[] { 1, 1 }, editors.ExtraCounts);                 // paths、associations

        Assert.Equal(1, items.Single(i => i.Id == "known-hosts").Count);

        foreach (string id in new[] { "sessions", "identities", "secrets", "known-hosts" })
        {
            Assert.Empty(items.Single(i => i.Id == id).ExtraCounts);
        }

        // 只有 legacy-secrets 带迁移状态；其余 Cleanup 均为 None
        DataInventoryItem legacy = items.Single(i => i.Id == "legacy-secrets");
        Assert.Equal(LegacySecretState.NotStarted, legacy.LegacyState);
        Assert.Equal(LegacySecretCleanup.None, legacy.Cleanup);
        Assert.All(items.Where(i => i.Id != "legacy-secrets"), i => Assert.Null(i.LegacyState));
        Assert.All(items.Where(i => i.Id != "legacy-secrets"), i => Assert.Equal(LegacySecretCleanup.None, i.Cleanup));
        Assert.Equal(LegacySecretCleanup.None, items.Single(i => i.Id == "secrets").Cleanup);
    }

    [Fact]
    public async Task NotStarted_PurgeDeletesNothing()
    {
        var identityId = Guid.NewGuid();
        await VaultTestDb.CreateAsync(Connection, identityId);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
        {
            [Guid.NewGuid().ToString()] = new() { Password = "p" }
        });

        DataInventoryItem legacy = (await vault.LoadAsync()).Single(i => i.Id == "legacy-secrets");
        Assert.Equal(LegacySecretState.NotStarted, legacy.LegacyState);
        Assert.Equal(LegacySecretCleanup.None, legacy.Cleanup);
        Assert.Equal(0, legacy.DeletableCount);

        Assert.Equal(0, await vault.PurgeLegacyIdentitySecretsAsync());
        Assert.Equal(1, await CountAsync("identity_secrets"));
    }

    [Fact]
    public async Task Partial_DeletesOnlyMigratedRows_AndFallbackStillReads()
    {
        var firstId = Guid.NewGuid();
        var firstKey = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(firstId, "First", firstKey);
        var secondId = Guid.NewGuid();
        var secondKey = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(secondId, "Second", secondKey);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        await vault.SaveSecretsAsync(firstId, new Dictionary<string, SecretPayload>
        {
            [firstKey.Id.ToString()] = new() { PrivateKeyContent = "FIRST-KEY" }
        });
        await vault.SaveSecretsAsync(secondId, new Dictionary<string, SecretPayload>
        {
            [secondKey.Id.ToString()] = new() { PrivateKeyContent = "SECOND-KEY" }
        });

        // 损坏第二个整包：迁移跳过它，只迁第一个 → Partial，不写完成标记
        await ExecAsync(
            "UPDATE identity_secrets SET secrets_blob = zeroblob(48), nonce = NULL, tag = NULL WHERE identity_id = @id;",
            new { id = secondId.ToString() });
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);
        Assert.Null(await VaultTestDb.ReadMetaAsync(Connection, "secret_library_migrated"));
        Assert.Equal(2, await CountAsync("identity_secrets"));
        Assert.Equal(1, await CountAsync("vault_secrets"));

        DataInventoryItem legacy = (await reopened.LoadAsync()).Single(i => i.Id == "legacy-secrets");
        Assert.Equal(LegacySecretState.Partial, legacy.LegacyState);
        Assert.Equal(LegacySecretCleanup.MigratedRowsOnly, legacy.Cleanup);
        Assert.Equal(2, legacy.Count);
        Assert.Equal(1, legacy.DeletableCount);   // 只算已迁移的第一个

        // 只删第一个身份的行；第二个整包（还没迁走）必须留下，否则下次解锁无法重试
        Assert.Equal(1, await reopened.PurgeLegacyIdentitySecretsAsync());
        Assert.Equal(1, await CountAsync("identity_secrets"));
        Assert.Equal(1, await ExecAsyncScalarAsync(
            "SELECT COUNT(*) FROM identity_secrets WHERE identity_id = @id;", new { id = secondId.ToString() }));
        Assert.Equal(1, await CountAsync("vault_secrets"));   // 材料库不受影响

        // 修复第二个整包后仍能经回退读到（完成标记仍未置位）
        await reopened.SaveSecretsAsync(secondId, new Dictionary<string, SecretPayload>
        {
            [secondKey.Id.ToString()] = new() { PrivateKeyContent = "SECOND-KEY" }
        });
        Assert.Equal("SECOND-KEY", (await reopened.GetPayloadAsync(secondKey.Id))!.PrivateKeyContent);

        // 再次解锁补迁第二个：材料仍然可读
        reopened.Lock();
        await reopened.UnlockAsync("pw", false);
        Assert.Equal("SECOND-KEY", (await reopened.GetPayloadAsync(secondKey.Id))!.PrivateKeyContent);
    }

    [Fact]
    public async Task CorruptMigratedSet_DeletesNothing()
    {
        var identityId = Guid.NewGuid();
        await VaultTestDb.CreateAsync(Connection, identityId);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
        {
            [Guid.NewGuid().ToString()] = new() { Password = "p" }
        });

        // 集合值损坏、完成标记不是 "1"：按空集处理，不得因此删行
        await VaultTestDb.UpsertMetaAsync(Connection, "secret_library_migrated_identities", "not-json");
        Assert.Null(await VaultTestDb.ReadMetaAsync(Connection, "secret_library_migrated"));

        DataInventoryItem legacy = (await vault.LoadAsync()).Single(i => i.Id == "legacy-secrets");
        Assert.Equal(LegacySecretState.NotStarted, legacy.LegacyState);

        Assert.Equal(0, await vault.PurgeLegacyIdentitySecretsAsync());
        Assert.Equal(1, await CountAsync("identity_secrets"));
    }

    [Fact]
    public async Task Complete_DeletesAllLegacyRows_IncludingUnparseableIds()
    {
        var identityId = Guid.NewGuid();
        await VaultTestDb.CreateAsync(Connection, identityId);

        var logger = new RecordingLogger<InternalVaultManager>();
        var factory = new SqliteConnectionFactory(Connection);
        var vault = new InternalVaultManager(factory, logger, null, VaultTestDb.TestArgon2Parameters);
        await vault.SetMasterPasswordAsync("pw");
        await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
        {
            [Guid.NewGuid().ToString()] = new() { PrivateKeyContent = "TOP-SECRET-PRIVATE-KEY" }
        });
        await vault.SaveAsync(new VaultSecret { Id = Guid.NewGuid(), Kind = SecretKind.Password }, new SecretPayload { Password = "vault-pw" });

        // 无法解析的 identity_id 旧行（临时关外键）
        await InsertUnparseableLegacyRowAsync();

        await VaultTestDb.UpsertMetaAsync(Connection, "secret_library_migrated", "1");

        vault.Lock();
        Assert.False(vault.IsUnlocked);

        // 锁定时也能清理：不解密、不解锁
        Assert.Equal(2, await vault.PurgeLegacyIdentitySecretsAsync());
        Assert.Equal(0, await CountAsync("identity_secrets"));
        Assert.Equal(1, await CountAsync("vault_secrets"));
        Assert.Equal("1", await VaultTestDb.ReadMetaAsync(Connection, "secret_library_migrated"));

        // 日志只记模式与行数，不含私钥正文
        Assert.Contains(logger.Messages, m => m.Contains("清理旧身份密文"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("TOP-SECRET-PRIVATE-KEY"));

        DataInventoryItem legacy = (await vault.LoadAsync()).Single(i => i.Id == "legacy-secrets");
        Assert.Equal(LegacySecretState.Complete, legacy.LegacyState);
        Assert.Equal(LegacySecretCleanup.AllRows, legacy.Cleanup);
        Assert.Equal(0, legacy.DeletableCount);
        Assert.Equal(0, legacy.Count);
    }

    private async Task<long> ExecAsyncScalarAsync(string sql, object? param)
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>(sql, param);
    }

    // 记录全部日志文本，用于断言清理日志不泄露材料
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
