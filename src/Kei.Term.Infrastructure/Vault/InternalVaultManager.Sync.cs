using System.Security.Cryptography;
using System.Text;
using Dapper;
using Kei.Term.Infrastructure.Storage;

namespace Kei.Term.Infrastructure.Vault;

public partial class InternalVaultManager
{
    // 专用本机 Vault 条目，不创建虚假的身份/代理，不参与同步导出。
    private static readonly Guid SyncPassphraseId = Guid.Parse("bc184718-2c16-4fb0-bff9-9491fd709713");

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
            await EnsureInitializedAsync(ct);
            EnsureUnlocked();
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

    private sealed class LocalSecretRow
    {
        public byte[] SecretsBlob { get; set; } = [];
        public string EncryptionAlgorithm { get; set; } = "";
    }
}
