using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Kei.Term.Core.Models;
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

    // 测试统一用 8192 KiB / t=1 / p=1，避免每个用例跑 64 MiB 的生产 Argon2id
    internal static NSec.Cryptography.Argon2Parameters TestArgon2Parameters { get; } = new()
    {
        MemorySize = 8192,
        NumberOfPasses = 1,
        DegreeOfParallelism = 1,
    };

    public static InternalVaultManager CreateVault(string connectionString, IDeviceQuickUnlockStore? quickUnlockStore = null)
        => CreateVault(new SqliteConnectionFactory(connectionString), quickUnlockStore);

    public static InternalVaultManager CreateVault(SqliteConnectionFactory factory, IDeviceQuickUnlockStore? quickUnlockStore = null)
        => new(factory, null, quickUnlockStore, TestArgon2Parameters);

    public static async Task CreateAsync(string connStr, Guid identityId)
    {
        var tree = new SqliteTreeRepository(connStr);
        await tree.InitializeAsync();

        var identities = new SqliteIdentityRepository(connStr);
        await identities.SaveAsync(new Identity { Id = identityId, Name = "Test" });
    }

    // 材料库条目密文行（vault_secrets 载荷列为单数 secret_blob）
    public static async Task<(string algorithm, byte[] blob, bool hasNonce, bool hasTag)> ReadVaultSecretRawAsync(string connStr, Guid secretId)
    {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT secret_blob, encryption_algorithm, nonce, tag FROM vault_secrets WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", secretId.ToString());
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(1),
            (byte[])reader["secret_blob"],
            !reader.IsDBNull(2),
            !reader.IsDBNull(3));
    }

    public static async Task<(string algorithm, byte[] blob, byte[]? nonce, byte[]? tag)> ReadVaultSecretRowAsync(string connStr, Guid secretId)
    {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT secret_blob, encryption_algorithm, nonce, tag FROM vault_secrets WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", secretId.ToString());
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(1),
            (byte[])reader["secret_blob"],
            reader.IsDBNull(2) ? null : (byte[])reader["nonce"],
            reader.IsDBNull(3) ? null : (byte[])reader["tag"]);
    }

    // 改写已有 vault_secrets 密文行（不改变算法），用于模拟密文被对调/篡改
    public static async Task OverwriteVaultSecretAsync(string connStr, Guid secretId, byte[] blob, byte[]? nonce, byte[]? tag)
    {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE vault_secrets SET secret_blob = $blob, nonce = $nonce, tag = $tag WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", secretId.ToString());
        cmd.Parameters.Add("$blob", SqliteType.Blob).Value = blob;
        cmd.Parameters.AddWithValue("$nonce", (object?)nonce ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tag", (object?)tag ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    // 改写已有密文行（不改变算法），用于模拟密文被对调/篡改
    public static async Task OverwriteSecretAsync(string connStr, Guid identityId, byte[] blob, byte[]? nonce, byte[]? tag)
    {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE identity_secrets SET secrets_blob = $blob, nonce = $nonce, tag = $tag WHERE identity_id = $id;";
        cmd.Parameters.AddWithValue("$id", identityId.ToString());
        cmd.Parameters.Add("$blob", SqliteType.Blob).Value = blob;
        cmd.Parameters.AddWithValue("$nonce", (object?)nonce ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tag", (object?)tag ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpsertMetaAsync(string connStr, string key, string value)
    {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO vault_metadata (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = $value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<string?> ReadMetaAsync(string connStr, string key)
    {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM vault_metadata WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return await cmd.ExecuteScalarAsync() as string;
    }

    // 构造旧 PBKDF2 库（无 vault_id/wrapped_vault_key），用 AES-256-GCM 保护 verifier 与条目
    public static async Task<byte[]> SeedLegacyVaultAsync(string connStr, Guid identityId, string password, Dictionary<string, SecretPayload> secrets)
    {
        await CreateAsync(connStr, identityId);

        byte[] salt = RandomNumberGenerator.GetBytes(VaultCryptography.SaltLength);
        byte[] mek = Rfc2898DeriveBytes.Pbkdf2(password, salt, VaultCryptography.Pbkdf2Iterations, HashAlgorithmName.SHA512, VaultCryptography.KeyLength);
        byte[] verifier = VaultCryptography.LegacySeal(mek, Encoding.UTF8.GetBytes(VaultCryptography.VerifierPlaintext));

        await UpsertMetaAsync(connStr, "plain_mode", "0");
        await UpsertMetaAsync(connStr, "kdf", VaultCryptography.Pbkdf2Sha512Id);
        await UpsertMetaAsync(connStr, "kdf_salt", Convert.ToBase64String(salt));
        await UpsertMetaAsync(connStr, "verifier", Convert.ToBase64String(verifier));

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(secrets);
        byte[] blob = VaultCryptography.LegacySeal(mek, json);
        using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO identity_secrets (identity_id, secrets_blob, encryption_algorithm, nonce, tag)
                VALUES ($id, $blob, $alg, $nonce, $tag);
            ";
            cmd.Parameters.AddWithValue("$id", identityId.ToString());
            cmd.Parameters.Add("$blob", SqliteType.Blob).Value = blob;
            cmd.Parameters.AddWithValue("$alg", VaultCryptography.LegacyAlgorithm);
            cmd.Parameters.AddWithValue("$nonce", blob.AsSpan(0, VaultCryptography.LegacyNonceLength).ToArray());
            cmd.Parameters.AddWithValue("$tag", blob.AsSpan(VaultCryptography.LegacyNonceLength, VaultCryptography.LegacyTagLength).ToArray());
            await cmd.ExecuteNonQueryAsync();
        }
        return mek;
    }
}

public class InternalVaultManagerTests
{
    [Fact]
    public async Task PlainMode_IsUnlocked_AndPassthroughBytes()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        var identityId = Guid.NewGuid();
        try
        {
            await VaultTestDb.CreateAsync(connStr, identityId);
            var vault = VaultTestDb.CreateVault(connStr);
            var passwordId = Guid.NewGuid();
            var keyId = Guid.NewGuid();

            await vault.SaveAsync(
                new VaultSecret { Id = passwordId, Kind = SecretKind.Password },
                new SecretPayload { Password = "topsecret" });
            await vault.SaveAsync(
                new VaultSecret { Id = keyId, Kind = SecretKind.SshPrivateKey },
                new SecretPayload { PrivateKeyContent = "KEY", Passphrase = "pp" });

            Assert.True(vault.IsPlainMode);
            Assert.True(vault.IsUnlocked);
            Assert.True(await vault.TryAutoUnlockAsync());

            Assert.Equal("topsecret", (await vault.GetPayloadAsync(passwordId))!.Password);
            Assert.Equal("pp", (await vault.GetPayloadAsync(keyId))!.Passphrase);

            // 明文模式直通：algorithm=PLAIN，blob 即 JSON 明文
            var raw = await VaultTestDb.ReadVaultSecretRawAsync(connStr, passwordId);
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
            var vault = VaultTestDb.CreateVault(connStr);

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
            var vault = VaultTestDb.CreateVault(connStr);
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
        try
        {
            await VaultTestDb.CreateAsync(connStr, Guid.NewGuid());
            var vault = VaultTestDb.CreateVault(connStr);
            var entryId = Guid.NewGuid();

            await vault.SetMasterPasswordAsync("pw");
            await vault.SaveAsync(
                new VaultSecret { Id = entryId, Kind = SecretKind.SshPrivateKey },
                new SecretPayload { Password = "topsecret", PrivateKeyContent = "KEY", Passphrase = "pp" });

            vault.Lock();
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.GetPayloadAsync(entryId));

            await vault.UnlockAsync("pw", false);
            SecretPayload payload = (await vault.GetPayloadAsync(entryId))!;
            Assert.Equal("topsecret", payload.Password);
            Assert.Equal("KEY", payload.PrivateKeyContent);

            // 密文落库：algorithm=XCHACHA20-POLY1305，且 blob 不含明文
            var raw = await VaultTestDb.ReadVaultSecretRawAsync(connStr, entryId);
            Assert.Equal("XCHACHA20-POLY1305", raw.algorithm);
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
        try
        {
            await VaultTestDb.CreateAsync(connStr, Guid.NewGuid());
            var vault = VaultTestDb.CreateVault(connStr);
            var entryId = Guid.NewGuid();

            // 先在明文模式写入
            await vault.SaveAsync(
                new VaultSecret { Id = entryId, Kind = SecretKind.Password },
                new SecretPayload { Password = "topsecret" });

            // 再设置主密码：应无损升级为密文
            await vault.SetMasterPasswordAsync("pw");

            vault.Lock();
            await vault.UnlockAsync("pw", false);

            Assert.Equal("topsecret", (await vault.GetPayloadAsync(entryId))!.Password);

            var raw = await VaultTestDb.ReadVaultSecretRawAsync(connStr, entryId);
            Assert.Equal("XCHACHA20-POLY1305", raw.algorithm);
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
            var vault = VaultTestDb.CreateVault(connStr);

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

// Vault Key 格式、Argon2id 参数、旧库迁移与 AAD 绑定
public class VaultKeyFormatTests
{
    private static Dictionary<string, SecretPayload> Sample() => new()
    {
        ["m1"] = new SecretPayload { Password = "topsecret" },
        ["m2"] = new SecretPayload { PrivateKeyContent = "KEY", Passphrase = "pp" }
    };

    [Fact]
    public void ProductionArgon2Parameters_AreTheDocumentedConstants()
    {
        // 只断言生产常量，不真的跑 64 MiB 生产 KDF
        Assert.Equal(65_536, VaultCryptography.Argon2MemorySizeKiB);
        Assert.Equal(3, VaultCryptography.Argon2Passes);
        Assert.Equal(1, VaultCryptography.Argon2Parallelism);
        Assert.Equal("argon2id:m=65536,t=3,p=1", VaultCryptography.ProductionKdfId);

        NSec.Cryptography.Argon2Parameters parameters = VaultCryptography.ProductionArgon2Parameters;
        Assert.Equal(65_536, parameters.MemorySize);
        Assert.Equal(3, parameters.NumberOfPasses);
        Assert.Equal(1, parameters.DegreeOfParallelism);
    }

    [Fact]
    public async Task LegacyPbkdf2Vault_UnlocksAndMigratesToVaultKey()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        var identityId = Guid.NewGuid();
        var method = new VaultPasswordMethod();
        byte[] mek = [];
        try
        {
            // 旧库整包键控在方法 Id 上：解锁后材料库条目 Id = 方法 Id
            mek = await VaultTestDb.SeedLegacyVaultAsync(connStr, identityId, "legacy-password", new Dictionary<string, SecretPayload>
            {
                [method.Id.ToString()] = new SecretPayload { Password = "topsecret" }
            });
            await new SqliteIdentityRepository(connStr).SaveAsync(new Identity { Id = identityId, Name = "legacy", Methods = [method] });

            var vault = VaultTestDb.CreateVault(connStr);
            await vault.UnlockAsync("legacy-password", false);

            // 迁移后数据仍在
            Assert.Equal("topsecret", (await vault.GetPayloadAsync(method.Id))!.Password);

            // 条目升级为新算法
            var raw = await VaultTestDb.ReadVaultSecretRawAsync(connStr, method.Id);
            Assert.Equal("XCHACHA20-POLY1305", raw.algorithm);

            // 元数据换成 Argon2id + Vault Key 包装
            Assert.StartsWith("argon2id:", await VaultTestDb.ReadMetaAsync(connStr, "kdf"));
            Assert.NotNull(await VaultTestDb.ReadMetaAsync(connStr, "vault_id"));
            Assert.NotNull(await VaultTestDb.ReadMetaAsync(connStr, "wrapped_vault_key"));

            // 重启后按新格式解锁，旧口令仍可用，错误口令被拒绝
            var reopened = VaultTestDb.CreateVault(connStr);
            await reopened.UnlockAsync("legacy-password", false);
            Assert.Equal("topsecret", (await reopened.GetPayloadAsync(method.Id))!.Password);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reopened.UnlockAsync("wrong", false));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mek);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task LegacyMigrationFailure_LeavesOldMetadataIntact()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        var identityId = Guid.NewGuid();
        byte[] mek = [];
        try
        {
            mek = await VaultTestDb.SeedLegacyVaultAsync(connStr, identityId, "legacy-password", Sample());

            // 破坏旧条目密文，使迁移重加密阶段失败
            await VaultTestDb.OverwriteSecretAsync(connStr, identityId, new byte[40], null, null);

            var vault = VaultTestDb.CreateVault(connStr);
            await Assert.ThrowsAnyAsync<CryptographicException>(() => vault.UnlockAsync("legacy-password", false));
            Assert.False(vault.IsUnlocked);

            // 事务回滚：不能留下半新的元数据
            Assert.Equal(VaultCryptography.Pbkdf2Sha512Id, await VaultTestDb.ReadMetaAsync(connStr, "kdf"));
            Assert.Null(await VaultTestDb.ReadMetaAsync(connStr, "vault_id"));
            Assert.Null(await VaultTestDb.ReadMetaAsync(connStr, "wrapped_vault_key"));

            // 再次解锁仍走旧库路径并再次失败，说明候选密钥已清掉、未安装
            var retry = VaultTestDb.CreateVault(connStr);
            await Assert.ThrowsAnyAsync<CryptographicException>(() => retry.UnlockAsync("legacy-password", false));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mek);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task NewFormat_WrongItemId_FailsToDecrypt()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        try
        {
            await VaultTestDb.CreateAsync(connStr, Guid.NewGuid());

            var vault = VaultTestDb.CreateVault(connStr);
            await vault.SetMasterPasswordAsync("pw");
            await vault.SaveAsync(
                new VaultSecret { Id = firstId, Kind = SecretKind.Password },
                new SecretPayload { Password = "topsecret" });
            await vault.SaveAsync(
                new VaultSecret { Id = secondId, Kind = SecretKind.Password },
                new SecretPayload { Password = "other" });

            // 把 first 的密文对调到 second：AAD 绑定条目 Id，解密必须失败
            var rowFirst = await VaultTestDb.ReadVaultSecretRowAsync(connStr, firstId);
            await VaultTestDb.OverwriteVaultSecretAsync(connStr, secondId, rowFirst.blob, rowFirst.nonce, rowFirst.tag);

            await Assert.ThrowsAsync<CryptographicException>(() => vault.GetPayloadAsync(secondId));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task UnknownKdf_ThrowsNotSupported_NotWrongPassword()
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        try
        {
            await VaultTestDb.CreateAsync(connStr, Guid.NewGuid());
            await VaultTestDb.UpsertMetaAsync(connStr, "plain_mode", "0");
            await VaultTestDb.UpsertMetaAsync(connStr, "kdf", "scrypt:1");
            await VaultTestDb.UpsertMetaAsync(connStr, "kdf_salt", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
            await VaultTestDb.UpsertMetaAsync(connStr, "verifier", Convert.ToBase64String(new byte[44]));

            var vault = VaultTestDb.CreateVault(connStr);
            await Assert.ThrowsAsync<NotSupportedException>(() => vault.UnlockAsync("whatever", false));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("argon2id:m=8192,t=1,p=2")]
    [InlineData("argon2id:m=0,t=1,p=1")]
    [InlineData("argon2id:garbage")]
    public async Task MalformedArgon2Id_ThrowsNotSupported(string kdfId)
    {
        var path = VaultTestDb.NewPath();
        var connStr = $"Data Source={path}";
        try
        {
            await VaultTestDb.CreateAsync(connStr, Guid.NewGuid());
            await VaultTestDb.UpsertMetaAsync(connStr, "plain_mode", "0");
            await VaultTestDb.UpsertMetaAsync(connStr, "kdf", kdfId);
            await VaultTestDb.UpsertMetaAsync(connStr, "kdf_salt", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
            await VaultTestDb.UpsertMetaAsync(connStr, "verifier", Convert.ToBase64String(new byte[44]));

            var vault = VaultTestDb.CreateVault(connStr);
            await Assert.ThrowsAsync<NotSupportedException>(() => vault.UnlockAsync("whatever", false));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
