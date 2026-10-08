using System.Runtime.InteropServices;
using Kei.Term.Infrastructure.Vault.MacOS;

namespace Kei.Term.Tests;

// 直接运行无交互的原生 ABI，覆盖 P/Invoke、取消和密钥泄漏边界，不写入用户 Keychain。
public sealed class MacOsQuickUnlockNativeTests
{
    [Fact]
    public void CancelledNativeRequest_CannotStartAuthenticationOrReturnKey()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        IntPtr request = Native.CreateRequest("KeiTerm.CancelledNativeRequest", "解锁 KeiTerm 保险库");
        Assert.NotEqual(IntPtr.Zero, request);
        try
        {
            Native.CancelRequest(request);
            Assert.Equal(1, Native.WaitRequest(request));
            Assert.Equal(1, Native.GetKey(request, out IntPtr key, out int length));
            Assert.Equal(IntPtr.Zero, key);
            Assert.Equal(0, length);
        }
        finally
        {
            Native.ReleaseRequest(request);
        }
    }

    [Fact]
    public void NativeRequest_BeforeVerificationCannotReturnKey()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        IntPtr request = Native.CreateRequest("KeiTerm.UnverifiedNativeRequest", "解锁 KeiTerm 保险库");
        Assert.NotEqual(IntPtr.Zero, request);
        try
        {
            Assert.Equal(4, Native.GetKey(request, out IntPtr key, out int length));
            Assert.Equal(IntPtr.Zero, key);
            Assert.Equal(0, length);
        }
        finally
        {
            Native.ReleaseRequest(request);
        }
    }

    [Fact]
    public async Task PreCancelledUnlock_DoesNotStartSystemVerification()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        MacOsDeviceQuickUnlockStore store = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.UnlockKeyAsync("KeiTerm.PreCancelledUnlock", "解锁 KeiTerm 保险库", cancellation.Token));
    }

    [Fact]
    public async Task StoreKey_RejectsMalformedMaterialBeforeCallingKeychain()
    {
        MacOsDeviceQuickUnlockStore store = new();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.StoreKeyAsync("KeiTerm.InvalidMaterial", new byte[31]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.StoreKeyAsync("KeiTerm\0DifferentAccount", new byte[32]));
    }

    private static class Native
    {
        private const string Library = "kei_quick_unlock";

        [DllImport(Library, EntryPoint = "kei_quick_unlock_request_create")]
        internal static extern IntPtr CreateRequest([MarshalAs(UnmanagedType.LPUTF8Str)] string keyId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string reason);

        [DllImport(Library, EntryPoint = "kei_quick_unlock_request_wait")]
        internal static extern int WaitRequest(IntPtr request);

        [DllImport(Library, EntryPoint = "kei_quick_unlock_request_cancel")]
        internal static extern void CancelRequest(IntPtr request);

        [DllImport(Library, EntryPoint = "kei_quick_unlock_request_get_key")]
        internal static extern int GetKey(IntPtr request, out IntPtr key, out int length);

        [DllImport(Library, EntryPoint = "kei_quick_unlock_request_release")]
        internal static extern void ReleaseRequest(IntPtr request);
    }
}
