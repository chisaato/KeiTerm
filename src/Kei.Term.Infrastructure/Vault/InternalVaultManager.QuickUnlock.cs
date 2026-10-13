namespace Kei.Term.Infrastructure.Vault;

using System.Security.Cryptography;
using Kei.Term.Core.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

public partial class InternalVaultManager
{
    public void InvalidateQuickUnlockAttempts()
    {
        lock (_keyStateLock) { _lockVersion++; }
    }

    public async Task<VaultQuickUnlockState?> GetQuickUnlockStateAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        lock (_keyStateLock)
        {
            string? id = CurrentQuickUnlockKeyId();
            return id == null ? null : new VaultQuickUnlockState(id, _lockVersion);
        }
    }

    public async Task<VaultQuickUnlockMaterial> PrepareQuickUnlockAsync(string masterPassword, CancellationToken ct = default)
    {
        await _secretWriteLock.WaitAsync(ct);
        try
        {
            // 已解锁状态也不能替代启用时的主密码证明。
            await UnlockCoreAsync(masterPassword, ct);
            lock (_keyStateLock)
            {
                string? id = CurrentQuickUnlockKeyId();
                if (id == null || _vaultKey == null) throw new InvalidOperationException("请先为保管库设置主密码。");
                // 交出的 32 字节是 Vault Key 的副本，不是主密码或包装密钥
                return new VaultQuickUnlockMaterial(id, _vaultKey.ToArray());
            }
        }
        finally { _secretWriteLock.Release(); }
    }

    public async Task<bool> UnlockWithDeviceKeyAsync(VaultQuickUnlockState state, ReadOnlyMemory<byte> key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (key.Length != VaultCryptography.KeyLength) return false;

        byte[] candidate = key.ToArray();
        bool installed = false;
        try
        {
            await _secretWriteLock.WaitAsync(ct);
            try
            {
                await EnsureInitializedAsync(ct);
                lock (_keyStateLock)
                {
                    if (!MatchesQuickUnlockState(state)) return false;
                    // 有待轮换标记时必须先经口令解锁完成轮换，设备项不得抢先装回仍有效的旧字节
                    if (_rotationPending) return false;
                }

                string? verifier = await LoadValueAsync(KeyVerifier, ct);
                if (verifier == null) return false;
                byte[] verifierBlob = Convert.FromBase64String(verifier);

                byte[]? vaultId = _vaultId;
                // 新库 verifier 由 Vault Key 密封；尚未迁移的旧库仍用 MEK 的 AES-256-GCM
                bool valid = vaultId != null
                    ? VaultCryptography.VerifierMatches(candidate, vaultId, verifierBlob)
                    : VaultCryptography.TryLegacyVerifierMatches(candidate, verifierBlob);
                if (!valid) return false;

                using SqliteConnection conn = await CreateConnectionAsync(ct);
                await CompletePendingCleanupAsync(conn, ct);
                ct.ThrowIfCancellationRequested();
                lock (_keyStateLock)
                {
                    // 系统验证期间的手动锁定或主密码轮换具有优先权。
                    if (!MatchesQuickUnlockState(state)) return false;
                    // 设备解锁不保存包装密钥
                    ReplaceKeysLocked(candidate, null);
                    installed = true;
                }
                return true;
            }
            finally { _secretWriteLock.Release(); }
        }
        finally
        {
            if (!installed) CryptographicOperations.ZeroMemory(candidate);
        }
    }

    // 回滚一次设备登记。删除失败时不能只记日志：遗留副本可能仍是有效 Vault Key。
    public async Task DiscardDeviceKeyCopyAsync(string keyId, ReadOnlyMemory<byte> key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        if (_quickUnlockStore == null) return;
        if (await TryDeleteQuickUnlockAsync(keyId, ct)) return;

        _logger.LogWarning("回滚删除本机快速解锁项失败，检查遗留副本是否仍为当前数据密钥");
        await _secretWriteLock.WaitAsync(CancellationToken.None);
        try
        {
            await EnsureInitializedAsync(CancellationToken.None);
            bool stillCurrent = await IsCurrentDataKeyAsync(key, CancellationToken.None);
            if (!stillCurrent) return;

            // 先把标记落盘并提交，再尝试轮换：轮换失败或进程中断也留下下次口令解锁必须轮换的标记
            await MarkRotationPendingAsync(CancellationToken.None);
            if (HasWrappingKey())
            {
                // 有包装密钥就立刻轮换，成功会清标记
                await RotateVaultKeyLockedAsync(CancellationToken.None);
            }
            else
            {
                // 没有包装密钥可立即轮换：保持标记、递增 lock version 并锁定
                lock (_keyStateLock) { _lockVersion++; }
                Lock();
            }
        }
        finally { _secretWriteLock.Release(); }
    }

    // 判断交出的字节是否就是当前数据密钥：内存有 Vault Key 时直接固定时间比较；
    // 已锁定时用磁盘上的 verifier 与 vault_id（旧库用旧 verifier）核对
    private async Task<bool> IsCurrentDataKeyAsync(ReadOnlyMemory<byte> key, CancellationToken ct)
    {
        byte[]? vaultKey;
        byte[]? vaultId;
        lock (_keyStateLock)
        {
            vaultKey = _vaultKey;
            vaultId = _vaultId;
        }

        if (vaultKey != null)
        {
            return key.Length == vaultKey.Length && CryptographicOperations.FixedTimeEquals(key.Span, vaultKey);
        }

        string? verifierB64 = await LoadValueAsync(KeyVerifier, ct);
        if (verifierB64 == null) return false;
        byte[] verifier = Convert.FromBase64String(verifierB64);
        byte[] candidate = key.ToArray();
        try
        {
            if (vaultId != null) return VaultCryptography.VerifierMatches(candidate, vaultId, verifier);
            return VaultCryptography.TryLegacyVerifierMatches(candidate, verifier);
        }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }

    private bool HasWrappingKey()
    {
        lock (_keyStateLock) return _wrappingKey != null;
    }

    private async Task MarkRotationPendingAsync(CancellationToken ct)
    {
        using SqliteConnection conn = await CreateConnectionAsync(ct);
        using SqliteTransaction tx = conn.BeginTransaction();
        await UpsertMetadataAsync(conn, tx, KeyRotationPending, "1", ct);
        tx.Commit();
        lock (_keyStateLock) { _rotationPending = true; }
    }

    private bool MatchesQuickUnlockState(VaultQuickUnlockState state)
        => _hasMasterPassword && state.LockVersion == _lockVersion && state.KeyId == CurrentQuickUnlockKeyId();

    // key id = "keiterm-v1-" + SHA-256(盐) 十六进制；它只决定查找哪一项，不代表密钥字节失效。
    // 换密码换盐只换查找名，删除失败时必须轮换 Vault Key 才能让遗留副本真正失效。
    private string? CurrentQuickUnlockKeyId()
        => !_hasMasterPassword || _kdfSalt == null ? null : "keiterm-v1-" + Convert.ToHexString(SHA256.HashData(_kdfSalt));
}
