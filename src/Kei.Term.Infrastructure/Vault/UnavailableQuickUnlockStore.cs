namespace Kei.Term.Infrastructure.Vault;

using Kei.Term.Core.Vault;

// 本期仅接入 Windows/macOS；其余平台保持主密码解锁。
public sealed class UnavailableQuickUnlockStore : IDeviceQuickUnlockStore
{
    public string DisplayName => "System authentication";
    public bool IsSupported => false;
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> HasKeyAsync(string keyId, CancellationToken ct = default) => Task.FromResult(false);
    public Task StoreKeyAsync(string keyId, ReadOnlyMemory<byte> key, CancellationToken ct = default)
        => Task.FromException(new PlatformNotSupportedException());
    public Task<DeviceUnlockResult> UnlockKeyAsync(string keyId, string reason, CancellationToken ct = default)
        => Task.FromResult(new DeviceUnlockResult(DeviceUnlockOutcome.Unavailable));
    public Task DeleteKeyAsync(string keyId, CancellationToken ct = default) => Task.CompletedTask;
}
