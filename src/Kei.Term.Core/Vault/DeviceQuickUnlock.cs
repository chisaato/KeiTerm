namespace Kei.Term.Core.Vault;

// 本机快捷解锁只保存保险库密钥的受保护副本，不保存主密码或 SSH 业务材料。
public interface IDeviceQuickUnlockStore
{
    string DisplayName { get; }
    bool IsSupported { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<bool> HasKeyAsync(string keyId, CancellationToken ct = default);
    Task StoreKeyAsync(string keyId, ReadOnlyMemory<byte> key, CancellationToken ct = default);
    Task<DeviceUnlockResult> UnlockKeyAsync(string keyId, string reason, CancellationToken ct = default);
    Task DeleteKeyAsync(string keyId, CancellationToken ct = default);
}

public enum DeviceUnlockOutcome
{
    Success,
    Cancelled,
    Unavailable,
    NotEnrolled,
    Failed
}

// 成功返回的 Key 由调用方负责在使用后清零；失败不得携带密钥。
public sealed record DeviceUnlockResult(DeviceUnlockOutcome Outcome, byte[]? Key = null);

// 锁定版本用于拒绝系统验证期间发生锁定后的迟到结果。
public sealed record VaultQuickUnlockState(string KeyId, long LockVersion);

public sealed class VaultQuickUnlockMaterial(string keyId, byte[] key) : IDisposable
{
    public string KeyId { get; } = keyId;
    public byte[] Key { get; } = key;

    public void Dispose() => System.Security.Cryptography.CryptographicOperations.ZeroMemory(Key);
}

public interface IQuickUnlockVault
{
    // 撤销正在进行的快捷验证的提交资格，保留当前已解锁的会话。
    void InvalidateQuickUnlockAttempts();
    Task<VaultQuickUnlockState?> GetQuickUnlockStateAsync(CancellationToken ct = default);
    // 即使保险库已解锁，启用新设备也必须再次校验主密码。
    Task<VaultQuickUnlockMaterial> PrepareQuickUnlockAsync(string masterPassword, CancellationToken ct = default);
    Task<bool> UnlockWithDeviceKeyAsync(VaultQuickUnlockState state, ReadOnlyMemory<byte> key, CancellationToken ct = default);
}
