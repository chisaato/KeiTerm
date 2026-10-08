using System.ComponentModel;
using System.Security.Cryptography;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Vault.Windows;

namespace Kei.Term.Tests;

public class WindowsDeviceQuickUnlockTests
{
    [WindowsQuickUnlockFact]
    public async Task StoredKey_SurvivesNewStore_AndRequiresSuccessfulConsent()
    {
        string keyId = "Kei.Term.Tests." + Guid.NewGuid().ToString("N");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        WindowsDeviceQuickUnlockStore store = CreateStore(WindowsHelloResult.Verified);
        try
        {
            await store.StoreKeyAsync(keyId, key);
            WindowsDeviceQuickUnlockStore cancelledStore = CreateStore(WindowsHelloResult.Cancelled);
            Assert.True(await cancelledStore.HasKeyAsync(keyId));
            DeviceUnlockResult cancelled = await cancelledStore.UnlockKeyAsync(keyId, "测试");
            Assert.Equal(DeviceUnlockOutcome.Cancelled, cancelled.Outcome);
            Assert.Null(cancelled.Key);

            DeviceUnlockResult unlocked = await CreateStore(WindowsHelloResult.Verified).UnlockKeyAsync(keyId, "测试");
            try
            {
                Assert.Equal(DeviceUnlockOutcome.Success, unlocked.Outcome);
                Assert.Equal(key, unlocked.Key);
            }
            finally { if (unlocked.Key is not null) CryptographicOperations.ZeroMemory(unlocked.Key); }

            await store.DeleteKeyAsync(keyId);
            Assert.False(await store.HasKeyAsync(keyId));
            DeviceUnlockResult missing = await store.UnlockKeyAsync(keyId, "测试");
            Assert.Equal(DeviceUnlockOutcome.NotEnrolled, missing.Outcome);
            Assert.Null(missing.Key);
        }
        finally
        {
            await store.DeleteKeyAsync(keyId);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [WindowsQuickUnlockFact]
    public void DpapiCiphertext_IsBoundToKeyId_AndRejectsTampering()
    {
        string keyId = Guid.NewGuid().ToString("N");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] sealedKey = WindowsProtectedCredential.Protect(keyId, key, decrypt: false);
        try
        {
            Assert.NotEqual(key, sealedKey);
            byte[] recovered = WindowsProtectedCredential.Protect(keyId, sealedKey, decrypt: true);
            try { Assert.Equal(key, recovered); }
            finally { CryptographicOperations.ZeroMemory(recovered); }

            Assert.Throws<Win32Exception>(() => WindowsProtectedCredential.Protect("different-" + keyId, sealedKey, decrypt: true));
            sealedKey[^1] ^= 0x40;
            Assert.Throws<Win32Exception>(() => WindowsProtectedCredential.Protect(keyId, sealedKey, decrypt: true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(sealedKey);
        }
    }

    [WindowsQuickUnlockFact]
    public async Task CancellationDuringConsent_DoesNotReturnStoredKey()
    {
        string keyId = "Kei.Term.Tests." + Guid.NewGuid().ToString("N");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        using CancellationTokenSource cancellation = new();
        WindowsDeviceQuickUnlockStore store = new(() => Task.FromResult<nint>(1), new TestConsent(
            () =>
            {
                cancellation.Cancel();
                return WindowsHelloResult.Verified;
            }));
        try
        {
            await store.StoreKeyAsync(keyId, key);
            DeviceUnlockResult result = await store.UnlockKeyAsync(keyId, "测试", cancellation.Token);
            Assert.Equal(DeviceUnlockOutcome.Cancelled, result.Outcome);
            Assert.Null(result.Key);
            Assert.True(await store.HasKeyAsync(keyId));
        }
        finally
        {
            await store.DeleteKeyAsync(keyId);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static WindowsDeviceQuickUnlockStore CreateStore(WindowsHelloResult result) =>
        new(() => Task.FromResult<nint>(1), new TestConsent(() => result));

    // 不启动交互式 Hello 窗口；其余路径使用真实 DPAPI 和 Windows 凭据管理器。
    private sealed class TestConsent(Func<WindowsHelloResult> verify) : IWindowsHelloVerifier
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<WindowsHelloResult> VerifyAsync(nint owner, string reason, CancellationToken ct) => Task.FromResult(verify());
    }
}

internal sealed class WindowsQuickUnlockFactAttribute : FactAttribute
{
    public WindowsQuickUnlockFactAttribute()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
            Skip = "需要 Windows 10 1709+，使用真实 DPAPI 和用户凭据管理器。";
    }
}
