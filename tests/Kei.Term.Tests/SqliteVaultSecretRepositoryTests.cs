using System;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Xunit;

namespace Kei.Term.Tests;

// v8 vault_secrets 仓储：元数据明文列出、密文封套整行存取、created_at 保留、物理删除
public class SqliteVaultSecretRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"keiterm_vault_secrets_{Guid.NewGuid():N}.db");

    private string ConnStr => $"Data Source={_dbPath}";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    [Fact]
    public async Task UpsertGetListDelete_RoundTripsMetadataAndEnvelope()
    {
        var factory = new SqliteConnectionFactory(ConnStr);
        var repo = new SqliteVaultSecretRepository(factory);
        await repo.InitializeAsync();

        var id = Guid.NewGuid();
        var created = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var secret = new VaultSecret
        {
            Id = id,
            Name = "我的私钥",
            Kind = SecretKind.SshPrivateKey,
            Fingerprint = "SHA256:abc",
            KeyType = "ssh-ed25519",
            CreatedAt = created,
            UpdatedAt = created
        };
        await repo.UpsertAsync(secret, new VaultSecretEnvelope([1, 2, 3, 4], "AES-256-GCM", [9, 8], [7, 6]));

        // meta_json 以 camelCase 公开元数据键存放（设计文档约定的形状）
        using (var conn = await factory.OpenAsync())
        {
            string? metaJson = await conn.ExecuteScalarAsync<string>(
                "SELECT meta_json FROM vault_secrets WHERE id = @id;", new { id = id.ToString() });
            Assert.Contains("\"fingerprint\"", metaJson);
            Assert.Contains("\"keyType\"", metaJson);
        }

        // ListAsync 只读元数据列，未解锁也能列出
        var listed = Assert.Single(await repo.ListAsync());
        Assert.Equal(id, listed.Id);
        Assert.Equal("我的私钥", listed.Name);
        Assert.Equal(SecretKind.SshPrivateKey, listed.Kind);
        Assert.Equal("SHA256:abc", listed.Fingerprint);
        Assert.Equal("ssh-ed25519", listed.KeyType);
        Assert.Equal(created, listed.CreatedAt);

        // GetAsync 返回完整密文封套
        var encrypted = Assert.IsType<VaultSecretEncrypted>(await repo.GetAsync(id));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, encrypted.Envelope.Blob);
        Assert.Equal("AES-256-GCM", encrypted.Envelope.Algorithm);
        Assert.Equal(new byte[] { 9, 8 }, encrypted.Envelope.Nonce);
        Assert.Equal(new byte[] { 7, 6 }, encrypted.Envelope.Tag);

        // 记录首次写入后的更新时间，供二次写入比较
        DateTime firstUpdatedAt = encrypted.Secret.UpdatedAt;

        // 再次 Upsert：即便传入 default CreatedAt，created_at 仍保留首次值；updated_at 只增不减
        await repo.UpsertAsync(
            secret with { Name = "改名", Fingerprint = null, KeyType = null, CreatedAt = default },
            new VaultSecretEnvelope([5, 5], "AES-256-GCM", null, null));

        var again = Assert.IsType<VaultSecretEncrypted>(await repo.GetAsync(id));
        Assert.Equal("改名", again.Secret.Name);
        Assert.Null(again.Secret.Fingerprint);
        Assert.Null(again.Secret.KeyType);
        Assert.Equal(created, again.Secret.CreatedAt);
        Assert.True(again.Secret.UpdatedAt >= firstUpdatedAt);
        Assert.Equal(new byte[] { 5, 5 }, again.Envelope.Blob);

        // DeleteAsync 物理删除
        await repo.DeleteAsync(id);
        Assert.Null(await repo.GetAsync(id));
        Assert.Empty(await repo.ListAsync());
    }

    [Fact]
    public async Task Upsert_UnknownKind_IsPreservedAsIs()
    {
        var repo = new SqliteVaultSecretRepository(ConnStr);
        await repo.InitializeAsync();

        var id = Guid.NewGuid();
        await repo.UpsertAsync(
            new VaultSecret { Id = id, Name = "未来类型", Kind = "future-kind" },
            new VaultSecretEnvelope([1, 2], "PLAIN", null, null));

        var listed = Assert.Single(await repo.ListAsync());
        Assert.Equal("future-kind", listed.Kind);
        var encrypted = Assert.IsType<VaultSecretEncrypted>(await repo.GetAsync(id));
        Assert.Equal("future-kind", encrypted.Secret.Kind);
    }

    [Fact]
    public async Task ListAsync_CorruptMetaJson_DoesNotThrow_AndDegradesToEmptyMetadata()
    {
        var factory = new SqliteConnectionFactory(ConnStr);
        var repo = new SqliteVaultSecretRepository(factory);
        await repo.InitializeAsync();

        var id = Guid.NewGuid();
        // 直接写入损坏的 meta_json（非 JSON 文本），模拟历史/同步损坏数据
        using (var conn = await factory.OpenAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO vault_secrets
                    (id, name, kind, meta_json, secret_blob, encryption_algorithm, nonce, tag, created_at, updated_at)
                VALUES
                    (@id, '坏元数据', 'password', '{not json', @blob, 'AES-256-GCM', NULL, NULL, @now, @now);",
                new
                {
                    id = id.ToString(),
                    blob = new byte[] { 1, 2 },
                    now = "2026-01-01T00:00:00.0000000Z"
                });
        }

        // 损坏元数据只降级为空，不得使整表列出失败
        var listed = Assert.Single(await repo.ListAsync());
        Assert.Equal(id, listed.Id);
        Assert.Equal("坏元数据", listed.Name);
        Assert.Null(listed.Fingerprint);
        Assert.Null(listed.KeyType);
    }
}
