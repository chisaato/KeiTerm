namespace Kei.Term.Infrastructure.Storage;

using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage.Schema;

// vault_secrets 表访问：元数据明文列出、载荷以密文封套整行存取。
// 加解密由上层 VaultManager 负责（AAD 绑定条目 Id），本仓储只做持久化。
public sealed class SqliteVaultSecretRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteVaultSecretRepository(string connectionString)
        : this(new SqliteConnectionFactory(connectionString))
    {
    }

    public SqliteVaultSecretRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync(CancellationToken ct = default) => SchemaMigrator.MigrateAsync(_factory, ct);

    // 只读元数据列：未解锁也能列出与选择材料
    public async Task<IReadOnlyList<VaultSecret>> ListAsync(CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        IEnumerable<VaultSecretRow> rows = await conn.QueryAsync<VaultSecretRow>(new CommandDefinition(@"
            SELECT id, name, kind, meta_json, created_at, updated_at
            FROM vault_secrets
            ORDER BY name COLLATE NOCASE ASC, id ASC;
        ", cancellationToken: ct));

        var list = new List<VaultSecret>();
        foreach (VaultSecretRow row in rows)
        {
            list.Add(ToSecret(row));
        }

        return list;
    }

    // 读全行：调用方须已解锁，否则密文封套无法解密
    public async Task<VaultSecretEncrypted?> GetAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        VaultSecretRow? row = await conn.QuerySingleOrDefaultAsync<VaultSecretRow>(new CommandDefinition(@"
            SELECT id, name, kind, meta_json, secret_blob, encryption_algorithm, nonce, tag, created_at, updated_at
            FROM vault_secrets
            WHERE id = @id;
        ", new { id = id.ToString() }, cancellationToken: ct));

        if (row == null)
        {
            return null;
        }

        var envelope = new VaultSecretEnvelope(
            row.SecretBlob,
            row.EncryptionAlgorithm,
            row.Nonce,
            row.Tag);
        return new VaultSecretEncrypted(ToSecret(row), envelope);
    }

    // 新建或更新整条材料（元数据 + 已加密载荷）。created_at 保留，updated_at 一律取当前 UTC。
    public async Task UpsertAsync(VaultSecret secret, VaultSecretEnvelope envelope, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        DateTime now = DateTime.UtcNow;
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO vault_secrets
                (id, name, kind, meta_json, secret_blob, encryption_algorithm, nonce, tag, created_at, updated_at)
            VALUES
                (@Id, @Name, @Kind, @MetaJson, @SecretBlob, @Algorithm, @Nonce, @Tag, @CreatedAt, @UpdatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                kind = excluded.kind,
                meta_json = excluded.meta_json,
                secret_blob = excluded.secret_blob,
                encryption_algorithm = excluded.encryption_algorithm,
                nonce = excluded.nonce,
                tag = excluded.tag,
                updated_at = excluded.updated_at;
        ", new
        {
            Id = secret.Id.ToString(),
            secret.Name,
            secret.Kind,
            MetaJson = WriteMeta(secret),
            SecretBlob = envelope.Blob,
            Algorithm = envelope.Algorithm,
            Nonce = envelope.Nonce,
            Tag = envelope.Tag,
            CreatedAt = SqliteValue.FormatUtc(secret.CreatedAt == default ? now : secret.CreatedAt),
            UpdatedAt = SqliteValue.FormatUtc(now)
        }, cancellationToken: ct));
    }

    // 物理删除；引用保护由上层服务（SecretReferences）先校验
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM vault_secrets WHERE id = @id;",
            new { id = id.ToString() },
            cancellationToken: ct));
    }

    private static VaultSecret ToSecret(VaultSecretRow row)
    {
        VaultSecretMeta? meta = ReadMeta(row.MetaJson);
        return new VaultSecret
        {
            Id = Guid.Parse(row.Id),
            Name = row.Name,
            Kind = row.Kind,
            Fingerprint = meta?.Fingerprint,
            KeyType = meta?.KeyType,
            CreatedAt = SqliteValue.ParseUtc(row.CreatedAt),
            UpdatedAt = SqliteValue.ParseUtc(row.UpdatedAt)
        };
    }

    // 损坏的 meta_json 只降级为空元数据，绝不使整表列出失败（公开元数据非机密，容错读取）
    private static VaultSecretMeta? ReadMeta(string? metaJson)
    {
        if (string.IsNullOrEmpty(metaJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VaultSecretMeta>(metaJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? WriteMeta(VaultSecret secret)
    {
        if (secret.Fingerprint is null && secret.KeyType is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(new VaultSecretMeta
        {
            Fingerprint = secret.Fingerprint,
            KeyType = secret.KeyType
        });
    }

    // meta_json 的明文公开元数据（camelCase 键，与设计文档一致）
    private sealed class VaultSecretMeta
    {
        [JsonPropertyName("fingerprint")]
        public string? Fingerprint { get; set; }

        [JsonPropertyName("keyType")]
        public string? KeyType { get; set; }
    }

    private sealed class VaultSecretRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string? MetaJson { get; set; }
        public byte[] SecretBlob { get; set; } = [];
        public string EncryptionAlgorithm { get; set; } = string.Empty;
        public byte[]? Nonce { get; set; }
        public byte[]? Tag { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }
}
