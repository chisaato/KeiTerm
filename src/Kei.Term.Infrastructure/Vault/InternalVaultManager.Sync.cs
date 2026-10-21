using System.Security.Cryptography;
using System.Text;
using Dapper;
using Kei.Term.Infrastructure.Storage;

namespace Kei.Term.Infrastructure.Vault;

public partial class InternalVaultManager
{
    // 专用本机 Vault 条目，不创建虚假的身份/代理，不参与同步导出。
    private static readonly Guid SyncPassphraseId = Guid.Parse("bc184718-2c16-4fb0-bff9-9491fd709713");
    private static readonly Guid WebDavPasswordId = Guid.Parse("7f3c1a2e-9b64-4d15-8e70-2c4a6f8b1d33");
    private static readonly Guid S3SecretKeyId = Guid.Parse("c1a84e20-5d77-4b09-9f31-6e2b0a8c4d55");

    public Task DeletePassphraseAsync(CancellationToken ct = default)
        => WithSecretWriteLockAsync(async () =>
        {
            await EnsureInitializedAsync(ct);
            EnsureUnlocked();
            using var conn = await _factory.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM local_vault_secrets WHERE id=@id;", new { id = SyncPassphraseId.ToString() }, cancellationToken: ct));
        }, ct);

    public async Task<char[]?> GetPassphraseAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        EnsureUnlocked();
        using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<LocalSecretRow>(new CommandDefinition(
            "SELECT secrets_blob, encryption_algorithm FROM local_vault_secrets WHERE id = @id;",
            new { id = SyncPassphraseId.ToString() }, cancellationToken: ct));
        if (row == null) return null;
        byte[] plain = DecryptItem(SyncPassphraseId, row.SecretsBlob, row.EncryptionAlgorithm);
        try { return Encoding.UTF8.GetChars(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public Task SavePassphraseAsync(ReadOnlyMemory<char> passphrase, CancellationToken ct = default)
        => WithSecretWriteLockAsync(async () =>
        {
            await EnsureSyncMasterPasswordAsync(ct);
            if (passphrase.IsEmpty) throw new ArgumentException("同步口令不能为空");
            byte[] plain = new byte[Encoding.UTF8.GetByteCount(passphrase.Span)];
            Encoding.UTF8.GetBytes(passphrase.Span, plain);
            try
            {
                var encrypted = EncryptItem(SyncPassphraseId, plain);
                using var conn = await _factory.OpenAsync(ct);
                await conn.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO local_vault_secrets (id, secrets_blob, encryption_algorithm, nonce, tag)
                    VALUES (@id, @blob, @algorithm, @nonce, @tag)
                    ON CONFLICT(id) DO UPDATE SET secrets_blob = excluded.secrets_blob,
                        encryption_algorithm = excluded.encryption_algorithm, nonce = excluded.nonce, tag = excluded.tag;",
                    new { id = SyncPassphraseId.ToString(), blob = encrypted.Blob, algorithm = encrypted.Algorithm,
                        nonce = encrypted.Nonce, tag = encrypted.Tag }, cancellationToken: ct));
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }, ct);

    public Task<string?> GetWebDavPasswordAsync(CancellationToken ct = default) => ReadLocalTextAsync(WebDavPasswordId, ct);

    public Task SaveWebDavPasswordAsync(string password, CancellationToken ct = default)
        => WriteLocalTextAsync(WebDavPasswordId, password, "WebDAV 密码不能为空", ct);

    public Task<string?> GetS3SecretKeyAsync(CancellationToken ct = default) => ReadLocalTextAsync(S3SecretKeyId, ct);

    public Task SaveS3SecretKeyAsync(string secretKey, CancellationToken ct = default)
        => WriteLocalTextAsync(S3SecretKeyId, secretKey, "S3 Secret Key 不能为空", ct);

    private async Task<string?> ReadLocalTextAsync(Guid id, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        EnsureUnlocked();
        using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<LocalSecretRow>(new CommandDefinition(
            "SELECT secrets_blob, encryption_algorithm FROM local_vault_secrets WHERE id = @id;",
            new { id = id.ToString() }, cancellationToken: ct));
        if (row == null) return null;
        byte[] plain = DecryptItem(id, row.SecretsBlob, row.EncryptionAlgorithm);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private Task WriteLocalTextAsync(Guid id, string secret, string emptyMessage, CancellationToken ct)
        => WithSecretWriteLockAsync(async () =>
        {
            await EnsureInitializedAsync(ct);
            EnsureUnlocked();
            if (string.IsNullOrEmpty(secret)) throw new ArgumentException(emptyMessage);
            byte[] plain = Encoding.UTF8.GetBytes(secret);
            try
            {
                var encrypted = EncryptItem(id, plain);
                using var conn = await _factory.OpenAsync(ct);
                await conn.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO local_vault_secrets (id, secrets_blob, encryption_algorithm, nonce, tag)
                    VALUES (@id, @blob, @algorithm, @nonce, @tag)
                    ON CONFLICT(id) DO UPDATE SET secrets_blob = excluded.secrets_blob,
                        encryption_algorithm = excluded.encryption_algorithm, nonce = excluded.nonce, tag = excluded.tag;",
                    new { id = id.ToString(), blob = encrypted.Blob, algorithm = encrypted.Algorithm,
                        nonce = encrypted.Nonce, tag = encrypted.Tag }, cancellationToken: ct));
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }, ct);

    private sealed class LocalSecretRow
    {
        public byte[] SecretsBlob { get; set; } = [];
        public string EncryptionAlgorithm { get; set; } = "";
    }
}
