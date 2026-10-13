using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

// InternalVaultManager 作为 IVaultSecretLibrary：材料库加解密、锁定语义、AAD 绑定条目 Id、
// 明文/加密模式与主密码设置/移除的维护集成
public class VaultSecretLibraryTests : IDisposable
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

    private async Task<(string Algorithm, byte[] Blob, byte[]? Nonce, byte[]? Tag)> ReadSecretRowAsync(Guid id)
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT secret_blob, encryption_algorithm, nonce, tag FROM vault_secrets WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(1),
            (byte[])reader["secret_blob"],
            reader.IsDBNull(2) ? null : (byte[])reader["nonce"],
            reader.IsDBNull(3) ? null : (byte[])reader["tag"]);
    }

    private async Task OverwriteSecretAsync(Guid id, byte[] blob, byte[]? nonce, byte[]? tag, string algorithm)
    {
        using var conn = new SqliteConnection(Connection);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE vault_secrets SET secret_blob = $blob, nonce = $nonce, tag = $tag, encryption_algorithm = $alg WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.Add("$blob", SqliteType.Blob).Value = blob;
        cmd.Parameters.AddWithValue("$nonce", (object?)nonce ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tag", (object?)tag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$alg", algorithm);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Save_ThenGet_RoundTrips_AndCipherIsNotPlaintext()
    {
        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");

        var id = Guid.NewGuid();
        await vault.SaveAsync(
            new VaultSecret { Id = id, Name = "我的密码", Kind = SecretKind.Password },
            new SecretPayload { Password = "topsecret" });

        var payload = await vault.GetPayloadAsync(id);
        Assert.Equal("topsecret", payload!.Password);

        var raw = await ReadSecretRowAsync(id);
        Assert.Equal("XCHACHA20-POLY1305", raw.Algorithm);
        Assert.True(raw.Nonce is { Length: > 0 } && raw.Tag is { Length: > 0 });
        Assert.DoesNotContain("topsecret", Encoding.UTF8.GetString(raw.Blob));
    }

    [Fact]
    public async Task GetPayload_WhenLocked_Throws()
    {
        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        var id = Guid.NewGuid();
        await vault.SaveAsync(
            new VaultSecret { Id = id, Name = "n", Kind = SecretKind.Password },
            new SecretPayload { Password = "secret" });

        vault.Lock();
        Assert.False(vault.IsUnlocked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.GetPayloadAsync(id));

        // ListAsync 只读元数据，锁定也能列出与选择
        var listed = Assert.Single(await vault.ListAsync());
        Assert.Equal(id, listed.Id);
    }

    [Fact]
    public async Task WrongEntryId_FailsToDecrypt()
    {
        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        await vault.SaveAsync(new VaultSecret { Id = aId, Name = "A", Kind = SecretKind.Password }, new SecretPayload { Password = "secret-a" });
        await vault.SaveAsync(new VaultSecret { Id = bId, Name = "B", Kind = SecretKind.Password }, new SecretPayload { Password = "secret-b" });

        // 把 A 的密文封套对调到 B 行：AAD 绑定条目 Id，解密必须失败
        var rowA = await ReadSecretRowAsync(aId);
        await OverwriteSecretAsync(bId, rowA.Blob, rowA.Nonce, rowA.Tag, rowA.Algorithm);

        await Assert.ThrowsAsync<CryptographicException>(() => vault.GetPayloadAsync(bId));
    }

    [Fact]
    public async Task PlainMode_WritesPlainAndRoundTrips()
    {
        var vault = VaultTestDb.CreateVault(Connection);
        var id = Guid.NewGuid();
        await vault.SaveAsync(
            new VaultSecret { Id = id, Name = "n", Kind = SecretKind.Password },
            new SecretPayload { Password = "plain-secret" });

        var raw = await ReadSecretRowAsync(id);
        Assert.Equal("PLAIN", raw.Algorithm);
        Assert.Contains("plain-secret", Encoding.UTF8.GetString(raw.Blob));

        var payload = await vault.GetPayloadAsync(id);
        Assert.Equal("plain-secret", payload!.Password);
    }

    [Fact]
    public async Task Save_NullPayload_UpdatesMetadataOnly_KeepsPayload()
    {
        var vault = VaultTestDb.CreateVault(Connection);
        await vault.SetMasterPasswordAsync("pw");
        var id = Guid.NewGuid();
        await vault.SaveAsync(
            new VaultSecret { Id = id, Name = "原名", Kind = SecretKind.Password, Fingerprint = "fp-1" },
            new SecretPayload { Password = "keep-me" });
        var before = await ReadSecretRowAsync(id);

        await vault.SaveAsync(
            new VaultSecret { Id = id, Name = "改名", Kind = SecretKind.Password, Fingerprint = "fp-2" },
            payload: null);

        // 载荷未被改写：密文字节保持一致，读回仍是原值
        var after = await ReadSecretRowAsync(id);
        Assert.Equal(before.Blob, after.Blob);
        Assert.Equal("keep-me", (await vault.GetPayloadAsync(id))!.Password);

        var listed = Assert.Single(await vault.ListAsync());
        Assert.Equal("改名", listed.Name);
        Assert.Equal("fp-2", listed.Fingerprint);
    }

    [Fact]
    public async Task Save_NullPayload_WhenEntryMissing_Throws()
    {
        var vault = VaultTestDb.CreateVault(Connection);
        await Assert.ThrowsAsync<ArgumentException>(() => vault.SaveAsync(
            new VaultSecret { Id = Guid.NewGuid(), Name = "x", Kind = SecretKind.Password },
            payload: null));
    }

    [Fact]
    public async Task RotateAndMasterPassword_KeepMaterialReadable()
    {
        var vault = VaultTestDb.CreateVault(Connection);
        var id = Guid.NewGuid();
        await vault.SaveAsync(
            new VaultSecret { Id = id, Name = "n", Kind = SecretKind.Password },
            new SecretPayload { Password = "material" });

        // 设主密码 → 去主密码 → 再设：vault_secrets 一并重加密/明文落库，材料始终可读
        await vault.SetMasterPasswordAsync("pw1");
        await vault.RemoveMasterPasswordAsync();
        await vault.SetMasterPasswordAsync("pw2");

        vault.Lock();
        await vault.UnlockAsync("pw2", false);
        Assert.Equal("material", (await vault.GetPayloadAsync(id))!.Password);
    }

    [Fact]
    public async Task Delete_RemovesEntry()
    {
        var vault = VaultTestDb.CreateVault(Connection);
        var id = Guid.NewGuid();
        await vault.SaveAsync(
            new VaultSecret { Id = id, Name = "n", Kind = SecretKind.Password },
            new SecretPayload { Password = "x" });

        await vault.DeleteAsync(id);

        Assert.Empty(await vault.ListAsync());
        Assert.Null(await vault.GetPayloadAsync(id));
    }
}
