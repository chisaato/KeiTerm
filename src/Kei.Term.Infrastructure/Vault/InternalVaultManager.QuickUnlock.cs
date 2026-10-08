namespace Kei.Term.Infrastructure.Vault;

using System.Security.Cryptography;
using System.Text;
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
                if (id == null || _mek == null) throw new InvalidOperationException("请先为保管库设置主密码。");
                return new VaultQuickUnlockMaterial(id, _mek.ToArray());
            }
        }
        finally { _secretWriteLock.Release(); }
    }

    public async Task<bool> UnlockWithDeviceKeyAsync(VaultQuickUnlockState state, ReadOnlyMemory<byte> key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (key.Length != MekLength) return false;

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
                }

                string? verifier = await LoadValueAsync(KeyVerifier, ct);
                if (verifier == null) return false;
                byte[] plaintext;
                try { plaintext = DecryptWithMek(candidate, Convert.FromBase64String(verifier)); }
                catch (CryptographicException) { return false; }
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(plaintext, Encoding.UTF8.GetBytes(VerifierPlaintext))) return false;
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }

                using SqliteConnection conn = await CreateConnectionAsync(ct);
                await CompletePendingCleanupAsync(conn, ct);
                ct.ThrowIfCancellationRequested();
                lock (_keyStateLock)
                {
                    // 系统验证期间的手动锁定或主密码轮换具有优先权。
                    if (!MatchesQuickUnlockState(state)) return false;
                    if (_mek != null) CryptographicOperations.ZeroMemory(_mek);
                    _mek = candidate;
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

    private bool MatchesQuickUnlockState(VaultQuickUnlockState state)
        => _hasMasterPassword && state.LockVersion == _lockVersion && state.KeyId == CurrentQuickUnlockKeyId();

    // 每次主密码轮换生成新盐，因此旧设备项无法解锁当前保险库；无需修改数据库结构。
    private string? CurrentQuickUnlockKeyId()
        => !_hasMasterPassword || _kdfSalt == null ? null : "keiterm-v1-" + Convert.ToHexString(SHA256.HashData(_kdfSalt));

    private async Task RemovePreviousQuickUnlockAsync(string? keyId)
    {
        if (keyId == null || _quickUnlockStore == null) return;
        try { await _quickUnlockStore.DeleteKeyAsync(keyId, CancellationToken.None); }
        catch (Exception ex)
        {
            // 删除失败不能回滚已提交的主密码。新盐已撤销旧项对当前保险库的作用。
            _logger.LogWarning("无法删除旧的本机快速解锁项，错误类型={ErrorType}", ex.GetType().Name);
        }
    }
}
