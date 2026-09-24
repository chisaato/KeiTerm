using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

// 临时库夹具：建全表（含 identities/identity_secrets/vault_metadata）并提供一个身份
internal static class VaultTestDb
{
    public static string NewPath() => Path.Combine(Path.GetTempPath(), $"keiterm_vault_{Guid.NewGuid():N}.db");

    public static async Task CreateAsync(string connStr, Guid identityId)
    {
        var tree = new SqliteTreeRepository(connStr);
        await tree.InitializeAsync();

        var identities = new SqliteIdentityRepository(connStr);
        await identities.SaveAsync(new Identity { Id = identityId, Name = "Test" });
    }

    public static async Task<(string algorithm, byte[] blob, bool hasNonce, bool hasTag)> ReadRawAsync(string connStr, Guid identityId)
    {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT secrets_blob, encryption_algorithm, nonce, tag FROM identity_secrets WHERE identity_id = $id;";
        cmd.Parameters.AddWithValue("$id", identityId.ToString());
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(1),
            (byte[])reader["secrets_blob"],
            !reader.IsDBNull(2),
            !reader.IsDBNull(3));
    }
}

public class InternalVaultManagerTests
{
    private static Dictionary<string, SecretPayload> Sample() => new()
    {
        ["m1"] = new SecretPayload { Password = "topsecret" },
        ["m2"] = new SecretPayload { PrivateKeyContent = "KEY", Passphrase = "pp" }
    };

    [Fact]
    public async Task PlainMode_IsUnlocked_AndPassthroughBytes()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        var identityId = Guid.NewGuid();
        try
        {
            await VaultTestDb.CreateAsync(connStr, identityId);
            var vault = new InternalVaultManager(connStr);

            await vault.SaveSecretsAsync(identityId, Sample());

            Assert.True(vault.IsPlainMode);
            Assert.True(vault.IsUnlocked);
            Assert.True(await vault.TryAutoUnlockAsync());

            var secrets = await vault.GetSecretsAsync(identityId);
            Assert.Equal("topsecret", secrets["m1"].Password);
            Assert.Equal("pp", secrets["m2"].Passphrase);

            // 明文模式直通：algorithm=PLAIN，blob 即 JSON 明文
            var raw = await VaultTestDb.ReadRawAsync(connStr, identityId);
            Assert.Equal("PLAIN", raw.algorithm);
            Assert.Contains("topsecret", Encoding.UTF8.GetString(raw.blob));
            Assert.False(raw.hasNonce);
            Assert.False(raw.hasTag);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SetMasterPassword_UnlockCorrectWrong_AndLock()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        try
        {
            await VaultTestDb.CreateAsync(connStr, Guid.NewGuid());
            var vault = new InternalVaultManager(connStr);

            await vault.SetMasterPasswordAsync("hunter2");

            Assert.False(vault.IsPlainMode);
            Assert.True(vault.IsUnlocked);

            vault.Lock();
            Assert.False(vault.IsUnlocked);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.UnlockAsync("wrong", false));
            Assert.False(vault.IsUnlocked);

            await vault.UnlockAsync("hunter2", false);
            Assert.True(vault.IsUnlocked);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SetMasterPassword_Empty_Throws()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        try
        {
            await VaultTestDb.CreateAsync(connStr, Guid.NewGuid());
            var vault = new InternalVaultManager(connStr);
            await Assert.ThrowsAsync<ArgumentException>(() => vault.SetMasterPasswordAsync(""));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Encrypted_SecretsRoundTrip_AndLockedThrows()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        var identityId = Guid.NewGuid();
        try
        {
            await VaultTestDb.CreateAsync(connStr, identityId);
            var vault = new InternalVaultManager(connStr);

            await vault.SetMasterPasswordAsync("pw");
            await vault.SaveSecretsAsync(identityId, Sample());

            vault.Lock();
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.GetSecretsAsync(identityId));

            await vault.UnlockAsync("pw", false);
            var secrets = await vault.GetSecretsAsync(identityId);
            Assert.Equal("topsecret", secrets["m1"].Password);
            Assert.Equal("KEY", secrets["m2"].PrivateKeyContent);

            // 密文落库：algorithm=AES-256-GCM，且 blob 不含明文
            var raw = await VaultTestDb.ReadRawAsync(connStr, identityId);
            Assert.Equal("AES-256-GCM", raw.algorithm);
            Assert.True(raw.hasNonce);
            Assert.True(raw.hasTag);
            Assert.DoesNotContain("topsecret", Encoding.UTF8.GetString(raw.blob));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SetMasterPassword_ReEncryptsExistingPlainSecrets()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        var identityId = Guid.NewGuid();
        try
        {
            await VaultTestDb.CreateAsync(connStr, identityId);
            var vault = new InternalVaultManager(connStr);

            // 先在明文模式写入
            await vault.SaveSecretsAsync(identityId, Sample());

            // 再设置主密码：应无损升级为密文
            await vault.SetMasterPasswordAsync("pw");

            vault.Lock();
            await vault.UnlockAsync("pw", false);

            var secrets = await vault.GetSecretsAsync(identityId);
            Assert.Equal("topsecret", secrets["m1"].Password);

            var raw = await VaultTestDb.ReadRawAsync(connStr, identityId);
            Assert.Equal("AES-256-GCM", raw.algorithm);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Lock_InPlainMode_StaysUnlocked()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        try
        {
            await VaultTestDb.CreateAsync(connStr, Guid.NewGuid());
            var vault = new InternalVaultManager(connStr);

            vault.Lock();

            Assert.True(vault.IsUnlocked);
            Assert.True(vault.IsPlainMode);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

// identity_secrets 整包存取删 + 身份删除级联清材料
public class VaultSecretStoreTests
{
    [Fact]
    public async Task SaveGetDelete_RoundTrip()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        var identityId = Guid.NewGuid();
        try
        {
            await VaultTestDb.CreateAsync(connStr, identityId);
            var store = new InternalVaultManager(connStr);

            var secrets = new Dictionary<string, SecretPayload>
            {
                ["method-a"] = new SecretPayload { Password = "a" },
                ["method-b"] = new SecretPayload { Passphrase = "b" }
            };
            await store.SaveSecretsAsync(identityId, secrets);

            var loaded = await store.GetSecretsAsync(identityId);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("a", loaded["method-a"].Password);
            Assert.Equal("b", loaded["method-b"].Passphrase);

            await store.DeleteSecretsAsync(identityId);
            Assert.Empty(await store.GetSecretsAsync(identityId));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task DeleteIdentity_CascadesSecrets()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        var identityId = Guid.NewGuid();
        try
        {
            await VaultTestDb.CreateAsync(connStr, identityId);
            var store = new InternalVaultManager(connStr);
            await store.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
            {
                ["m1"] = new SecretPayload { Password = "x" }
            });

            // 删除身份应通过 ON DELETE CASCADE 一并清除材料
            var repo = new SqliteIdentityRepository(connStr);
            await repo.DeleteAsync(identityId);

            Assert.Empty(await store.GetSecretsAsync(identityId));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
