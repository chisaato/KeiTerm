using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Kei.Term.Core.Sync;

namespace Kei.Term.Infrastructure.Vault;

// 固定本机条目参与既有 Vault Key 轮换，不新增 Schema，也不进入同步包。
public partial class InternalVaultManager : ISyncSafetyStore
{
    private static readonly Guid SyncBackupsId = Guid.Parse("4163e54c-5b76-414d-9cae-873a3fe47c65");
    private static readonly Guid SyncPendingPassphraseId = Guid.Parse("b51da352-4564-4a96-a59b-3060756b0cd8");
    private const string SyncJoinedKey = "sync.joined";
    private sealed record Backup(SyncLocalBackup Info, List<SyncEntity> Entities);

    public async Task EnsureSyncMasterPasswordAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        lock (_keyStateLock)
        {
            if (!_hasMasterPassword) throw new InvalidOperationException(SyncCoordinator.MasterPasswordRequiredMessage);
            EnsureUnlocked();
        }
    }

    public async Task<bool> HasJoinedAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT value FROM vault_metadata WHERE key=@key;", new { key = SyncJoinedKey }, cancellationToken: ct)) == "1";
    }

    public async Task SetJoinedAsync(bool joined, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("INSERT INTO vault_metadata(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;", new { key = SyncJoinedKey, value = joined ? "1" : "0" }, cancellationToken: ct));
    }

    public Task SaveBackupAsync(IReadOnlyList<SyncEntity> entities, CancellationToken ct = default)
        => WithSecretWriteLockAsync(async () =>
        {
            var backups = await ReadBackupsCoreAsync(ct);
            backups.Insert(0, new(new(Guid.NewGuid().ToString("N"), DateTime.UtcNow), entities.ToList()));
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(backups.Take(10));
            try { await WriteSafetyBlobAsync(SyncBackupsId, plain, ct); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }, ct);

    public async Task<IReadOnlyList<SyncLocalBackup>> ListBackupsAsync(CancellationToken ct = default)
        => await WithSnapshotSecretsAsync<IReadOnlyList<SyncLocalBackup>>(async () => (await ReadBackupsCoreAsync(ct)).Select(b => b.Info).ToList(), ct);

    public async Task<List<SyncEntity>> ReadBackupAsync(string id, CancellationToken ct = default)
        => await WithSnapshotSecretsAsync(async () => (await ReadBackupsCoreAsync(ct)).SingleOrDefault(b => b.Info.Id == id)?.Entities ?? throw new IOException("本机快照不存在"), ct);

    private async Task<List<Backup>> ReadBackupsCoreAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        EnsureUnlocked();
        using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<LocalSecretRow>(new CommandDefinition("SELECT secrets_blob,encryption_algorithm FROM local_vault_secrets WHERE id=@id;", new { id = SyncBackupsId.ToString() }, cancellationToken: ct));
        if (row == null) return [];
        byte[] plain;
        lock (_keyStateLock) plain = DecryptItem(SyncBackupsId, row.SecretsBlob, row.EncryptionAlgorithm);
        try { return JsonSerializer.Deserialize<List<Backup>>(plain) ?? throw new JsonException("本机快照损坏"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task WriteSafetyBlobAsync(Guid id, byte[] plain, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        EnsureUnlocked();
        // 明文 Vault 没有可用的 Vault Key；绝不降级为明文备份。
        (byte[] Blob, string Algorithm, byte[]? Nonce, byte[]? Tag) encrypted;
        lock (_keyStateLock)
        {
            if (!_hasMasterPassword) throw new InvalidOperationException(SyncCoordinator.MasterPasswordRequiredMessage);
            encrypted = EncryptItem(id, plain);
        }
        using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("INSERT INTO local_vault_secrets(id,secrets_blob,encryption_algorithm,nonce,tag) VALUES(@id,@blob,@algorithm,@nonce,@tag) ON CONFLICT(id) DO UPDATE SET secrets_blob=excluded.secrets_blob,encryption_algorithm=excluded.encryption_algorithm,nonce=excluded.nonce,tag=excluded.tag;",
            new { id = id.ToString(), blob = encrypted.Blob, algorithm = encrypted.Algorithm, nonce = encrypted.Nonce, tag = encrypted.Tag }, cancellationToken: ct));
    }

    public Task SavePendingPassphraseAsync(ReadOnlyMemory<char> passphrase, CancellationToken ct = default)
        => WithSecretWriteLockAsync(async () =>
        {
            // 连同旧口令保留，远端提交后清历史失败也不会丢失历史恢复凭据。
            char[]? old = await GetPassphraseAsync(ct);
            char[] next = passphrase.ToArray();
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new { Old = old, New = next });
            try { await WriteSafetyBlobAsync(SyncPendingPassphraseId, plain, ct); }
            finally { CryptographicOperations.ZeroMemory(plain); Array.Clear(next); if (old != null) Array.Clear(old); }
        }, ct);

    public async Task<char[]?> GetPendingPassphraseAsync(CancellationToken ct = default)
        => await WithSnapshotSecretsAsync(() => GetPendingPassphraseCoreAsync("New", ct), ct);

    public async Task<char[]?> GetPreviousPassphraseAsync(CancellationToken ct = default)
        => await WithSnapshotSecretsAsync(() => GetPendingPassphraseCoreAsync("Old", ct), ct);

    private async Task<char[]?> GetPendingPassphraseCoreAsync(string property, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        EnsureUnlocked();
        using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<LocalSecretRow>(new CommandDefinition("SELECT secrets_blob,encryption_algorithm FROM local_vault_secrets WHERE id=@id;", new { id = SyncPendingPassphraseId.ToString() }, cancellationToken: ct));
        if (row == null) return null;
        byte[] plain;
        lock (_keyStateLock) plain = DecryptItem(SyncPendingPassphraseId, row.SecretsBlob, row.EncryptionAlgorithm);
        try { using var doc = JsonDocument.Parse(plain); return doc.RootElement.GetProperty(property).Deserialize<char[]>(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public Task ClearPendingPassphraseAsync(CancellationToken ct = default) => DeleteSafetyItemAsync(SyncPendingPassphraseId, ct);
    public Task DeleteSyncBackupsAsync(CancellationToken ct = default) => DeleteSafetyItemAsync(SyncBackupsId, ct);

    private Task DeleteSafetyItemAsync(Guid id, CancellationToken ct)
        => WithSecretWriteLockAsync(async () =>
        {
            await EnsureInitializedAsync(ct);
            EnsureUnlocked();
            using var conn = await _factory.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM local_vault_secrets WHERE id=@id;", new { id = id.ToString() }, cancellationToken: ct));
        }, ct);
}
