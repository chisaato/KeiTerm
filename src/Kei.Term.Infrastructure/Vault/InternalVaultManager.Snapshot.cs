using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Kei.Term.Infrastructure.Vault;

public partial class InternalVaultManager
{
    // 锁先于 SQLite 事务获取，和密钥轮换使用相同顺序，避免轮换后落入旧密钥密文。
    internal async Task<T> WithSnapshotSecretsAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _secretWriteLock.WaitAsync(ct);
        try { return await action(); }
        finally { _secretWriteLock.Release(); }
    }

    internal async Task<byte[]?> ExportSnapshotSecretAsync(SqliteConnection conn, SqliteTransaction tx, bool identity, string id, CancellationToken ct)
    {
        string table = identity ? "identity_secrets" : "proxy_secrets";
        string key = identity ? "identity_id" : "proxy_id";
        var row = await conn.QuerySingleOrDefaultAsync<LocalSecretRow>(new CommandDefinition(
            $"SELECT secrets_blob, encryption_algorithm FROM {table} WHERE {key} = @id AND deleted_at IS NULL;", new { id }, tx, cancellationToken: ct));
        if (row == null) return null;
        lock (_keyStateLock)
        {
            EnsureUnlocked();
            return DecryptItem(Guid.Parse(id), row.SecretsBlob, row.EncryptionAlgorithm);
        }
    }

    internal async Task ImportSnapshotSecretAsync(SqliteConnection conn, SqliteTransaction tx, bool identity, string id, byte[]? plain, string updated, string? deleted, CancellationToken ct)
    {
        string table = identity ? "identity_secrets" : "proxy_secrets";
        string key = identity ? "identity_id" : "proxy_id";
        if (plain == null || deleted != null)
        {
            // 清除秘密也随所属实体版本传播，不保留旧设备的有效秘密。
            await conn.ExecuteAsync(new CommandDefinition($"UPDATE {table} SET deleted_at = @deleted WHERE {key} = @id;", new { id, deleted = deleted ?? updated }, tx, cancellationToken: ct));
            return;
        }
        try
        {
            (byte[] Blob, string Algorithm, byte[]? Nonce, byte[]? Tag) encrypted;
            lock (_keyStateLock)
            {
                EnsureUnlocked();
                encrypted = EncryptItem(Guid.Parse(id), plain);
            }
            await conn.ExecuteAsync(new CommandDefinition($"""
                INSERT INTO {table} ({key}, secrets_blob, encryption_algorithm, nonce, tag, deleted_at)
                VALUES (@id, @blob, @algorithm, @nonce, @tag, NULL)
                ON CONFLICT({key}) DO UPDATE SET secrets_blob = excluded.secrets_blob,
                    encryption_algorithm = excluded.encryption_algorithm, nonce = excluded.nonce, tag = excluded.tag, deleted_at = NULL;
                """, new { id, blob = encrypted.Blob, algorithm = encrypted.Algorithm, nonce = encrypted.Nonce, tag = encrypted.Tag }, tx, cancellationToken: ct));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
