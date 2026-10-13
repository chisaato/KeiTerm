using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kei.Term.Tests;

// 旧 identity_secrets 整包 → vault_secrets 的运行时迁移：
// 各解锁入口挂载、拆分/合并、幂等、失败整体回滚并回退旧表
public class VaultSecretMigrationTests : IDisposable
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

    private async Task<Identity> SeedIdentityAsync(Guid identityId, string name, params AuthMethodEntry[] methods)
    {
        await VaultTestDb.CreateAsync(Connection, identityId);
        var identity = new Identity { Id = identityId, Name = name, Methods = [.. methods] };
        await new SqliteIdentityRepository(Connection).SaveAsync(identity);
        return identity;
    }

    private async Task<int> CountSecretsAsync()
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vault_secrets;");
    }

    private async Task<Guid> SingleSecretIdAsync()
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        return Guid.Parse((await conn.ExecuteScalarAsync<string>("SELECT id FROM vault_secrets;"))!);
    }

    private async Task<string> SingleSecretKindAsync()
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<string>("SELECT kind FROM vault_secrets;"))!;
    }

    private async Task<string?> MarkerAsync()
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT value FROM vault_metadata WHERE key = 'secret_library_migrated';");
    }

    private async Task<string?> ReadMethodsJsonAsync(Guid id)
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT methods_json FROM identities WHERE id = @id;",
            new { id = id.ToString() });
    }

    private async Task<string?> ReadIdentityUpdatedAtAsync(Guid id)
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT updated_at FROM identities WHERE id = @id;",
            new { id = id.ToString() });
    }

    private async Task<int> CountLegacyBundlesAsync()
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM identity_secrets;");
    }

    private async Task<HashSet<Guid>> MigratedIdentitySetAsync()
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        string? json = await conn.ExecuteScalarAsync<string?>(
            "SELECT value FROM vault_metadata WHERE key = 'secret_library_migrated_identities';");
        var set = new HashSet<Guid>();
        if (string.IsNullOrEmpty(json))
        {
            return set;
        }

        foreach (string value in JsonSerializer.Deserialize<List<string>>(json) ?? [])
        {
            if (Guid.TryParse(value, out Guid identityId))
            {
                set.Add(identityId);
            }
        }

        return set;
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql, new { id = id.ToString() });
    }

    private async Task ExecuteAsync(string sql, Guid id, string value)
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql, new { id = id.ToString(), value });
    }

    private async Task ExecuteRawAsync(string sql)
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql);
    }

    [Fact]
    public async Task UnlockWithMasterPassword_MigratesAndSplits_AndIsIdempotent()
    {
        var identityId = Guid.NewGuid();
        var key = new VaultPrivateKeyMethod();
        var password = new VaultPasswordMethod();
        await SeedIdentityAsync(identityId, "Prod", key, password);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
        {
            [key.Id.ToString()] = new() { PrivateKeyContent = "KEY", Passphrase = "pp" },
            [password.Id.ToString()] = new() { Password = "pw" }
        });
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);

        // 拆成两个条目、写入标记
        Assert.Equal(2, await CountSecretsAsync());
        Assert.Equal("1", await MarkerAsync());

        // methods_json 回填 SecretId = 方法 Id
        var backfilled = await new SqliteIdentityRepository(Connection).GetByIdAsync(identityId);
        Assert.Equal(key.Id, Assert.IsType<VaultPrivateKeyMethod>(backfilled!.Methods.Single(m => m.Id == key.Id)).SecretId);
        Assert.Equal(password.Id, Assert.IsType<VaultPasswordMethod>(backfilled.Methods.Single(m => m.Id == password.Id)).SecretId);

        // 材料可读
        Assert.Equal("KEY", (await reopened.GetPayloadAsync(key.Id))!.PrivateKeyContent);

        // 幂等：再次解锁不重复建条目
        reopened.Lock();
        await reopened.UnlockAsync("pw", false);
        Assert.Equal(2, await CountSecretsAsync());
    }

    [Fact]
    public async Task UnlockWithMasterPassword_MergesIdenticalPrivateKeys_AcrossIdentities()
    {
        var firstId = Guid.NewGuid();
        var firstKey = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(firstId, "First", firstKey);
        var secondId = Guid.NewGuid();
        var secondKey = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(secondId, "Second", secondKey);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        // 两个身份各一把内容与口令完全相同的私钥
        await vault.SaveSecretsAsync(firstId, new Dictionary<string, SecretPayload>
        {
            [firstKey.Id.ToString()] = new() { PrivateKeyContent = "SAME-KEY", Passphrase = "pp" }
        });
        await vault.SaveSecretsAsync(secondId, new Dictionary<string, SecretPayload>
        {
            [secondKey.Id.ToString()] = new() { PrivateKeyContent = "SAME-KEY", Passphrase = "pp" }
        });
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);

        // 只剩 1 个 ssh-private-key 条目
        Assert.Equal(1, await CountSecretsAsync());
        Assert.Equal(SecretKind.SshPrivateKey, await SingleSecretKindAsync());
        Guid retained = await SingleSecretIdAsync();
        Assert.Contains(retained, new[] { firstKey.Id, secondKey.Id });

        // 两个身份的 SecretId 都指向保留条目（第二个身份被重新指向）
        var repo = new SqliteIdentityRepository(Connection);
        Assert.Equal(retained, Assert.IsType<VaultPrivateKeyMethod>((await repo.GetByIdAsync(firstId))!.Methods.Single()).SecretId);
        Assert.Equal(retained, Assert.IsType<VaultPrivateKeyMethod>((await repo.GetByIdAsync(secondId))!.Methods.Single()).SecretId);
    }

    [Fact]
    public async Task UnlockWithMasterPassword_KeepsDifferentPrivateKeys_Unmerged()
    {
        var firstId = Guid.NewGuid();
        var firstKey = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(firstId, "First", firstKey);
        var secondId = Guid.NewGuid();
        var secondKey = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(secondId, "Second", secondKey);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        // 内容相同但口令不同：不得合并
        await vault.SaveSecretsAsync(firstId, new Dictionary<string, SecretPayload>
        {
            [firstKey.Id.ToString()] = new() { PrivateKeyContent = "SAME-KEY", Passphrase = "p1" }
        });
        await vault.SaveSecretsAsync(secondId, new Dictionary<string, SecretPayload>
        {
            [secondKey.Id.ToString()] = new() { PrivateKeyContent = "SAME-KEY", Passphrase = "p2" }
        });
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);

        Assert.Equal(2, await CountSecretsAsync());
        var repo = new SqliteIdentityRepository(Connection);
        Assert.Equal(firstKey.Id, Assert.IsType<VaultPrivateKeyMethod>((await repo.GetByIdAsync(firstId))!.Methods.Single()).SecretId);
        Assert.Equal(secondKey.Id, Assert.IsType<VaultPrivateKeyMethod>((await repo.GetByIdAsync(secondId))!.Methods.Single()).SecretId);
    }

    [Fact]
    public async Task StartupPlainMode_Migrates()
    {
        var identityId = Guid.NewGuid();
        var key = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(identityId, "Prod", key);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
        {
            [key.Id.ToString()] = new() { PrivateKeyContent = "KEY" }
        });

        var restarted = VaultTestDb.CreateVault(Connection);
        Assert.True(await restarted.TryAutoUnlockAsync());

        Assert.Equal(1, await CountSecretsAsync());
        Assert.Equal("1", await MarkerAsync());
    }

    [Fact]
    public async Task QuickUnlock_AlsoMigrates()
    {
        var identityId = Guid.NewGuid();
        var key = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(identityId, "Prod", key);

        var vault = VaultTestDb.CreateVault(Connection);
        // 明文先写入旧整包，再设主密码时一并升级为密文
        await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
        {
            [key.Id.ToString()] = new() { PrivateKeyContent = "KEY" }
        });
        await vault.SetMasterPasswordAsync("pw");
        using var material = await vault.PrepareQuickUnlockAsync("pw");

        // 清掉登记材料库时已完成的迁移成果，使下一次迁移只能由设备解锁触发
        await ExecuteAsync("DELETE FROM vault_secrets;", identityId);
        await ExecuteAsync("DELETE FROM vault_metadata WHERE key = 'secret_library_migrated';", identityId);
        await ExecuteAsync("DELETE FROM vault_metadata WHERE key = 'secret_library_migrated_identities';", identityId);

        vault.Lock();
        var state = await vault.GetQuickUnlockStateAsync();
        Assert.True(await vault.UnlockWithDeviceKeyAsync(state!, material.Key));

        Assert.Equal(1, await CountSecretsAsync());
        Assert.Equal("1", await MarkerAsync());
    }

    [Fact]
    public async Task MetadataList_WhileLocked_DoesNotMigrate()
    {
        var identityId = Guid.NewGuid();
        var key = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(identityId, "Prod", key);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
        {
            [key.Id.ToString()] = new() { PrivateKeyContent = "KEY" }
        });

        // 未解锁的新实例：ListAsync 只读元数据，不得产生迁移副作用
        var locked = VaultTestDb.CreateVault(Connection);
        await locked.ListAsync();
        Assert.False(locked.IsUnlocked);
        Assert.Equal(0, await CountSecretsAsync());
        Assert.Null(await MarkerAsync());
    }

    [Fact]
    public async Task MigrationFailure_AfterVaultSecretWritten_RollsBackAll()
    {
        var firstId = Guid.NewGuid();
        var firstKey = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(firstId, "First", firstKey);
        var secondId = Guid.NewGuid();
        var secondPassword = new VaultPasswordMethod();
        await SeedIdentityAsync(secondId, "Second", secondPassword);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        await vault.SaveSecretsAsync(firstId, new Dictionary<string, SecretPayload>
        {
            [firstKey.Id.ToString()] = new() { PrivateKeyContent = "KEY" }
        });
        await vault.SaveSecretsAsync(secondId, new Dictionary<string, SecretPayload>
        {
            [secondPassword.Id.ToString()] = new() { Password = "second-pw" }
        });
        var methodsBefore = await ReadMethodsJsonAsync(secondId);
        vault.Lock();

        // 让「第一条 vault_secrets 写入成功后」的后续 INSERT 触发 ABORT：
        // 若迁移不在单事务里整体回滚，先前写入的条目会残留，本测试即失败。
        await ExecuteRawAsync(
            "CREATE TRIGGER fail_after_first_insert BEFORE INSERT ON vault_secrets " +
            "WHEN (SELECT COUNT(*) FROM vault_secrets) >= 1 " +
            "BEGIN SELECT RAISE(ABORT, 'injected migration failure'); END;");

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);   // 迁移失败不得阻断解锁

        Assert.True(reopened.IsUnlocked);
        Assert.Equal(0, await CountSecretsAsync());                 // 事务整体回滚
        Assert.Null(await MarkerAsync());
        Assert.Empty(await MigratedIdentitySetAsync());             // 已迁移集合与写表同一事务，一并回滚
        Assert.Equal(methodsBefore, await ReadMethodsJsonAsync(secondId));   // 回填未发生
        Assert.Equal("KEY", (await reopened.GetPayloadAsync(firstKey.Id))!.PrivateKeyContent);   // 回退旧整包
    }

    // RF3 补充：迁移失败后标记未置位 → 下一次解锁可重试并成功完成迁移
    [Fact]
    public async Task MigrationFailure_ThenRetryOnNextUnlock_CompletesMigration()
    {
        var identityId = Guid.NewGuid();
        var key = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(identityId, "Prod", key);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
        {
            [key.Id.ToString()] = new() { PrivateKeyContent = "KEY" }
        });
        vault.Lock();

        // 注入故障：本次迁移的所有 vault_secrets 写入都失败
        await ExecuteRawAsync(
            "CREATE TRIGGER fail_migration BEFORE INSERT ON vault_secrets " +
            "BEGIN SELECT RAISE(ABORT, 'injected migration failure'); END;");

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);   // 首次解锁：迁移失败但不阻断，标记未置位

        Assert.True(reopened.IsUnlocked);
        Assert.Equal(0, await CountSecretsAsync());
        Assert.Null(await MarkerAsync());

        // 故障排除后再次解锁：迁移重试并完成
        await ExecuteRawAsync("DROP TRIGGER fail_migration;");
        reopened.Lock();
        await reopened.UnlockAsync("pw", false);

        Assert.Equal(1, await CountSecretsAsync());
        Assert.Equal("1", await MarkerAsync());
        var backfilled = await new SqliteIdentityRepository(Connection).GetByIdAsync(identityId);
        Assert.Equal(key.Id, Assert.IsType<VaultPrivateKeyMethod>(Assert.Single(backfilled!.Methods)).SecretId);
        Assert.Equal("KEY", (await reopened.GetPayloadAsync(key.Id))!.PrivateKeyContent);
    }

    // I2：单个整包损坏按身份容错——正常身份仍迁移且可用，损坏项不阻断解锁（不写完成标记）
    [Fact]
    public async Task BrokenBundle_MigratesHealthyIdentity_LogsCorruptId_UnlockSucceeds()
    {
        var firstId = Guid.NewGuid();
        var firstKey = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(firstId, "First", firstKey);
        var secondId = Guid.NewGuid();
        var secondKey = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(secondId, "Second", secondKey);

        var logger = new RecordingLogger<InternalVaultManager>();
        var factory = new SqliteConnectionFactory(Connection);
        var vault = new InternalVaultManager(factory, logger, null, VaultTestDb.TestArgon2Parameters);
        await vault.SetMasterPasswordAsync("pw");
        await vault.SaveSecretsAsync(firstId, new Dictionary<string, SecretPayload>
        {
            [firstKey.Id.ToString()] = new() { PrivateKeyContent = "FIRST-KEY" }
        });
        await vault.SaveSecretsAsync(secondId, new Dictionary<string, SecretPayload>
        {
            [secondKey.Id.ToString()] = new() { PrivateKeyContent = "SECOND-KEY" }
        });

        // 损坏第二个身份的整包密文：整个身份被跳过，但不得阻断第一个身份的迁移
        await ExecuteAsync(
            "UPDATE identity_secrets SET secrets_blob = zeroblob(48), nonce = NULL, tag = NULL WHERE identity_id = $id;",
            secondId);
        vault.Lock();

        var reopened = new InternalVaultManager(factory, logger, null, VaultTestDb.TestArgon2Parameters);
        await reopened.UnlockAsync("pw", false);

        Assert.True(reopened.IsUnlocked);
        Assert.Equal(1, await CountSecretsAsync());          // 正常身份已迁移
        Assert.Null(await MarkerAsync());                    // 存在跳过项：不写完成标记
        Assert.Contains(firstId, await MigratedIdentitySetAsync());   // 已迁移集合记录正常身份

        // 正常身份：SecretId 已回填且材料可读（物化可用）
        var backfilled = await new SqliteIdentityRepository(Connection).GetByIdAsync(firstId);
        Assert.Equal(firstKey.Id, Assert.IsType<VaultPrivateKeyMethod>(Assert.Single(backfilled!.Methods)).SecretId);
        Assert.Equal("FIRST-KEY", (await reopened.GetPayloadAsync(firstKey.Id))!.PrivateKeyContent);
        // 损坏身份未被回填（旧数据保留，供未迁移回退路径读取）
        Assert.Null(Assert.IsType<VaultPrivateKeyMethod>(
            Assert.Single((await new SqliteIdentityRepository(Connection).GetByIdAsync(secondId))!.Methods)).SecretId);

        // 失败日志带出出错的身份 Id，且绝不泄露任何材料内容
        Assert.Contains(logger.Warnings, w => w.Contains(secondId.ToString()));
        Assert.DoesNotContain(logger.Warnings, w => w.Contains("SECOND-KEY") || w.Contains("FIRST-KEY"));
    }

    // I2：部分迁移后修复损坏整包，下次解锁重试补迁，已迁移项不重复
    [Fact]
    public async Task PartialMigration_AfterRepair_CompletesOnNextUnlock()
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
        await ExecuteAsync(
            "UPDATE identity_secrets SET secrets_blob = zeroblob(48), nonce = NULL, tag = NULL WHERE identity_id = $id;",
            secondId);
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);
        Assert.Equal(1, await CountSecretsAsync());
        Assert.Null(await MarkerAsync());

        // 修复第二个整包（用当前密钥重新加密写回旧表）
        await reopened.SaveSecretsAsync(secondId, new Dictionary<string, SecretPayload>
        {
            [secondKey.Id.ToString()] = new() { PrivateKeyContent = "SECOND-KEY" }
        });

        reopened.Lock();
        await reopened.UnlockAsync("pw", false);   // 无跳过项：补迁第二个身份并写完成标记

        Assert.Equal("1", await MarkerAsync());
        Assert.Equal(2, await CountSecretsAsync());   // 首个身份未重复建条目
        var second = await new SqliteIdentityRepository(Connection).GetByIdAsync(secondId);
        Assert.Equal(secondKey.Id, Assert.IsType<VaultPrivateKeyMethod>(Assert.Single(second!.Methods)).SecretId);
        Assert.Equal("SECOND-KEY", (await reopened.GetPayloadAsync(secondKey.Id))!.PrivateKeyContent);
    }

    // I1：迁移完成后旧表保留但不再回退——删除某条材料后旧私钥不得复活
    [Fact]
    public async Task MigratedRead_AfterEntryDeleted_DoesNotResurrectLegacyPayload()
    {
        var identityId = Guid.NewGuid();
        var key = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(identityId, "Prod", key);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
        {
            [key.Id.ToString()] = new() { PrivateKeyContent = "LEGACY-KEY", Passphrase = "pp" }
        });
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);   // 迁移完成并写入标记
        Assert.Equal("1", await MarkerAsync());
        Assert.Equal("LEGACY-KEY", (await reopened.GetPayloadAsync(key.Id))!.PrivateKeyContent);

        // 删除已迁移的条目：旧整包仍在，但完成标记使回退关闭，旧私钥不得复活
        await reopened.DeleteAsync(key.Id);
        Assert.Null(await reopened.GetPayloadAsync(key.Id));
        // 旧整包未被清除，证明「不复活」来自回退被禁用而非旧数据消失
        Assert.Equal(1, await CountLegacyBundlesAsync());
    }

    // I1：文件私钥 PassphraseMode 切走（删条目）再切回 Persistent 后，被删口令不得复活
    [Fact]
    public async Task MigratedRead_AfterPassphraseEntryDeleted_DoesNotResurrectLegacy()
    {
        var identityId = Guid.NewGuid();
        var fileMethod = new FilePrivateKeyMethod { PassphraseMode = PassphrasePersistence.Persistent };
        await SeedIdentityAsync(identityId, "Prod", fileMethod);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
        {
            [fileMethod.Id.ToString()] = new() { Passphrase = "legacy-pp" }
        });
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);
        Assert.Equal("1", await MarkerAsync());
        Assert.Equal("legacy-pp", (await reopened.GetPayloadAsync(fileMethod.Id))!.Passphrase);

        // 切到 SessionOnly（口令不再持久化）→ 删除遗留口令条目 → 切回 Persistent
        var repo = new SqliteIdentityRepository(Connection);
        await repo.SaveAsync(new Identity
        {
            Id = identityId,
            Name = "Prod",
            Methods = [new FilePrivateKeyMethod { Id = fileMethod.Id, PassphraseMode = PassphrasePersistence.SessionOnly }],
        });
        await reopened.DeleteAsync(fileMethod.Id);
        await repo.SaveAsync(new Identity
        {
            Id = identityId,
            Name = "Prod",
            Methods = [new FilePrivateKeyMethod { Id = fileMethod.Id, PassphraseMode = PassphrasePersistence.Persistent }],
        });

        // 迁移已完成：回退关闭，旧整包中的口令不得经方法 Id 复活
        Assert.Null(await reopened.GetPayloadAsync(fileMethod.Id));
        Assert.Equal(1, await CountLegacyBundlesAsync());
    }

    // M1：回填 methods_json 的同一条 UPDATE 一并刷新 identities.updated_at
    [Fact]
    public async Task MigrationBackfill_RefreshesIdentityUpdatedAt()
    {
        const string sentinel = "2000-01-01T00:00:00.0000000Z";
        var identityId = Guid.NewGuid();
        var key = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(identityId, "Prod", key);
        // 迁移前把 updated_at 置为哨兵旧值，确保能观察到回填时的刷新
        await ExecuteAsync("UPDATE identities SET updated_at = @value WHERE id = @id;", identityId, sentinel);

        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
        {
            [key.Id.ToString()] = new() { PrivateKeyContent = "KEY" }
        });
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);

        string? updatedAt = await ReadIdentityUpdatedAtAsync(identityId);
        Assert.NotNull(updatedAt);
        // ISO-8601「O」格式按字典序可比：刷新后应晚于哨兵值
        Assert.True(string.CompareOrdinal(updatedAt, sentinel) > 0, $"updated_at 未刷新: {updatedAt}");
    }

    [Fact]
    public async Task UnmigratedRead_FallsBackToLegacyBundle()
    {
        var identityId = Guid.NewGuid();
        var key = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(identityId, "Prod", key);

        // 明文模式：不调 TryAutoUnlockAsync（那会迁移），直接按条目 Id 读
        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
        {
            [key.Id.ToString()] = new() { PrivateKeyContent = "KEY", Passphrase = "pp" }
        });

        var payload = await vault.GetPayloadAsync(key.Id);
        Assert.Equal("KEY", payload!.PrivateKeyContent);
        Assert.Equal("pp", payload.Passphrase);
        Assert.Equal(0, await CountSecretsAsync());
        Assert.Null(await MarkerAsync());
    }

    // 缺陷 A① / A②：Partial 期间删除已迁移身份的材料——不回退复活，也不在下次解锁被重新插入
    [Fact]
    public async Task PartialMigration_DeletedMigratedEntry_NotResurrectedNorReinserted()
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
        await ExecuteAsync(
            "UPDATE identity_secrets SET secrets_blob = zeroblob(48), nonce = NULL, tag = NULL WHERE identity_id = $id;",
            secondId);
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);
        Assert.Equal(1, await CountSecretsAsync());
        Assert.Null(await MarkerAsync());                          // Partial：未写完成标记
        Assert.Contains(firstId, await MigratedIdentitySetAsync()); // 已迁移集合记录 first

        // 删除已迁移身份的材料：Partial 期间仍会尝试回退旧表，但该身份已迁移 → 不得复活
        await reopened.DeleteAsync(firstKey.Id);
        Assert.Null(await reopened.GetPayloadAsync(firstKey.Id));

        // 再次解锁（second 仍损坏）：已迁移身份不重复处理，删除的条目不得被重新插入
        reopened.Lock();
        await reopened.UnlockAsync("pw", false);
        Assert.Equal(0, await CountSecretsAsync());
        Assert.Null(await reopened.GetPayloadAsync(firstKey.Id));
    }

    // 缺陷 B：重试不得覆盖用户在新库中改绑的 SecretId（已迁移身份整体跳过）
    [Fact]
    public async Task RetryBackfill_DoesNotOverwriteUserReboundSecretId()
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
        await ExecuteAsync(
            "UPDATE identity_secrets SET secrets_blob = zeroblob(48), nonce = NULL, tag = NULL WHERE identity_id = $id;",
            secondId);
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);
        Assert.Equal(1, await CountSecretsAsync());

        // 用户把 first 的方法改绑到另一个条目 Id
        Guid rebound = Guid.NewGuid();
        var repo = new SqliteIdentityRepository(Connection);
        Identity first = (await repo.GetByIdAsync(firstId))!;
        Assert.IsType<VaultPrivateKeyMethod>(Assert.Single(first.Methods)).SecretId = rebound;
        await repo.SaveAsync(first);

        // 修复 second，重试完成迁移
        await reopened.SaveSecretsAsync(secondId, new Dictionary<string, SecretPayload>
        {
            [secondKey.Id.ToString()] = new() { PrivateKeyContent = "SECOND-KEY" }
        });
        reopened.Lock();
        await reopened.UnlockAsync("pw", false);
        Assert.Equal("1", await MarkerAsync());

        var after = await repo.GetByIdAsync(firstId);
        Assert.Equal(rebound, Assert.IsType<VaultPrivateKeyMethod>(Assert.Single(after!.Methods)).SecretId);
    }

    // 缺陷 B：永久损坏时，好身份的 updated_at 在第二次解锁不再变化（已迁移身份不再 UPDATE）
    [Fact]
    public async Task PermanentCorruption_GoodIdentityUpdatedAt_StableAcrossUnlocks()
    {
        const string sentinel = "2000-01-01T00:00:00.0000000Z";
        var firstId = Guid.NewGuid();
        var firstKey = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(firstId, "First", firstKey);
        var secondId = Guid.NewGuid();
        var secondKey = new VaultPrivateKeyMethod();
        await SeedIdentityAsync(secondId, "Second", secondKey);
        await ExecuteAsync("UPDATE identities SET updated_at = @value WHERE id = @id;", firstId, sentinel);

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
        await ExecuteAsync(
            "UPDATE identity_secrets SET secrets_blob = zeroblob(48), nonce = NULL, tag = NULL WHERE identity_id = $id;",
            secondId);
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);
        string? afterFirstUnlock = await ReadIdentityUpdatedAtAsync(firstId);
        Assert.NotNull(afterFirstUnlock);
        Assert.True(string.CompareOrdinal(afterFirstUnlock, sentinel) > 0, $"首次迁移应刷新 updated_at: {afterFirstUnlock}");

        // second 仍损坏 → 第二次解锁仍是 Partial；first 已在集合内，不得再刷新 updated_at
        reopened.Lock();
        await reopened.UnlockAsync("pw", false);
        Assert.Equal(afterFirstUnlock, await ReadIdentityUpdatedAtAsync(firstId));
    }

    // 缺陷 A：Partial 期间未命中读取——回退遍历到损坏整包必须跳过并返回 null，而不是抛
    [Fact]
    public async Task PartialMigration_MissRead_ReturnsNull_NotThrow()
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
        await ExecuteAsync(
            "UPDATE identity_secrets SET secrets_blob = zeroblob(48), nonce = NULL, tag = NULL WHERE identity_id = $id;",
            secondId);
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);
        Assert.Null(await MarkerAsync());

        // 未命中：回退会遍历到损坏的 second 整包，须跳过并返回 null
        Assert.Null(await reopened.GetPayloadAsync(Guid.NewGuid()));
    }

    // 缺陷 B：重试时相同的私钥合并进既有条目，保留条目 Id 在重试间保持稳定
    [Fact]
    public async Task RetryMerge_ReusesExistingEntry_ForIdenticalKey()
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
            [firstKey.Id.ToString()] = new() { PrivateKeyContent = "SAME-KEY", Passphrase = "pp" }
        });
        await vault.SaveSecretsAsync(secondId, new Dictionary<string, SecretPayload>
        {
            [secondKey.Id.ToString()] = new() { PrivateKeyContent = "SAME-KEY", Passphrase = "pp" }
        });
        await ExecuteAsync(
            "UPDATE identity_secrets SET secrets_blob = zeroblob(48), nonce = NULL, tag = NULL WHERE identity_id = $id;",
            secondId);
        vault.Lock();

        var reopened = VaultTestDb.CreateVault(Connection);
        await reopened.UnlockAsync("pw", false);
        Assert.Equal(1, await CountSecretsAsync());
        Assert.Equal(firstKey.Id, await SingleSecretIdAsync());   // 首次迁移保留 first 的条目 Id

        // 修复 second；重试时相同私钥必须合并进既有条目，不得新建第二条目
        await reopened.SaveSecretsAsync(secondId, new Dictionary<string, SecretPayload>
        {
            [secondKey.Id.ToString()] = new() { PrivateKeyContent = "SAME-KEY", Passphrase = "pp" }
        });
        reopened.Lock();
        await reopened.UnlockAsync("pw", false);

        Assert.Equal("1", await MarkerAsync());
        Assert.Equal(1, await CountSecretsAsync());
        Assert.Equal(firstKey.Id, await SingleSecretIdAsync());
        var second = await new SqliteIdentityRepository(Connection).GetByIdAsync(secondId);
        Assert.Equal(firstKey.Id, Assert.IsType<VaultPrivateKeyMethod>(Assert.Single(second!.Methods)).SecretId);
    }

    // 只记录 Warning 文本，用于断言失败日志的字段与「不泄露材料」
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
