using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Kei.Term.Core.Vault;
using Microsoft.Win32.SafeHandles;

namespace Kei.Term.Infrastructure.Vault.MacOS;

// 生物验证与密钥保管分开：系统 Touch ID 成功后才能读取当前用户钥匙串中的保险库密钥。
public sealed class MacOsDeviceQuickUnlockStore : IDeviceQuickUnlockStore
{
    private const int KeyLength = 32;

    public string DisplayName => "Touch ID";
    public bool IsSupported => OperatingSystem.IsMacOS();

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsSupported)
            return Task.FromResult(false);

        try
        {
            return Task.FromResult(Native.IsAvailable() == NativeOutcome.Success);
        }
        catch (DllNotFoundException)
        {
            return Task.FromResult(false);
        }
        catch (EntryPointNotFoundException)
        {
            return Task.FromResult(false);
        }
    }

    public async Task<bool> HasKeyAsync(string keyId, CancellationToken ct = default)
    {
        ValidateKeyId(keyId);
        ct.ThrowIfCancellationRequested();
        if (!IsSupported)
            return false;

        bool found = await Task.Run(() =>
        {
            EnsureSuccess(Native.HasKey(keyId, out int hasKey));
            return hasKey != 0;
        }, CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return found;
    }

    public async Task StoreKeyAsync(string keyId, ReadOnlyMemory<byte> key, CancellationToken ct = default)
    {
        ValidateKeyId(keyId);
        if (key.Length != KeyLength)
            throw new ArgumentException("保险库密钥必须为 32 字节。", nameof(key));
        EnsureSupported();
        ct.ThrowIfCancellationRequested();

        // P/Invoke 只在调用期间借用副本，避免修改调用方的 MEK，并在结束时擦除副本。
        byte[] copy = key.ToArray();
        try
        {
            await Task.Run(() => EnsureSuccess(Native.StoreKey(keyId, copy, copy.Length)), CancellationToken.None)
                .ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                // 系统钥匙串保存没有取消 API；操作结束后撤销这次已取消的启用。
                EnsureSuccess(Native.DeleteKey(keyId));
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    public async Task<DeviceUnlockResult> UnlockKeyAsync(string keyId, string reason, CancellationToken ct = default)
    {
        ValidateKeyId(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ct.ThrowIfCancellationRequested();
        if (!IsSupported)
            return new DeviceUnlockResult(DeviceUnlockOutcome.Unavailable);

        try
        {
            using UnlockRequest request = Native.CreateRequest(keyId, reason);
            if (request.IsInvalid)
                return new DeviceUnlockResult(DeviceUnlockOutcome.Failed);

            // 注册在 await 之前；原生取消会 invalidate LAContext，并保留迟到回调所需的对象。
            using CancellationTokenRegistration registration = ct.Register(() => Native.CancelRequest(request));
            NativeOutcome outcome = await Task.Run(() => Native.WaitRequest(request), CancellationToken.None)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (outcome != NativeOutcome.Success)
                return new DeviceUnlockResult(ToOutcome(outcome));

            outcome = Native.GetKey(request, out IntPtr pointer, out int length);
            if (outcome != NativeOutcome.Success || pointer == IntPtr.Zero || length != KeyLength)
                return new DeviceUnlockResult(outcome == NativeOutcome.Success
                    ? DeviceUnlockOutcome.Failed
                    : ToOutcome(outcome));

            byte[] key = new byte[length];
            try
            {
                Marshal.Copy(pointer, key, 0, length);
                ct.ThrowIfCancellationRequested();
                return new DeviceUnlockResult(DeviceUnlockOutcome.Success, key);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }
        catch (DllNotFoundException)
        {
            return new DeviceUnlockResult(DeviceUnlockOutcome.Unavailable);
        }
        catch (EntryPointNotFoundException)
        {
            return new DeviceUnlockResult(DeviceUnlockOutcome.Unavailable);
        }
    }

    public async Task DeleteKeyAsync(string keyId, CancellationToken ct = default)
    {
        ValidateKeyId(keyId);
        EnsureSupported();
        ct.ThrowIfCancellationRequested();
        await Task.Run(() => EnsureSuccess(Native.DeleteKey(keyId)), CancellationToken.None).ConfigureAwait(false);
    }

    private void EnsureSupported()
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("Touch ID 本机快速解锁仅支持 macOS。");
    }

    private static void ValidateKeyId(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (keyId.Length > 256 || keyId.Contains('\0'))
            throw new ArgumentException("本机快速解锁标识无效。", nameof(keyId));
    }

    private static void EnsureSuccess(NativeOutcome outcome)
    {
        if (outcome != NativeOutcome.Success)
            throw new InvalidOperationException("无法访问系统钥匙串，请使用主密码解锁后重试。");
    }

    private static DeviceUnlockOutcome ToOutcome(NativeOutcome outcome) => outcome switch
    {
        NativeOutcome.Success => DeviceUnlockOutcome.Success,
        NativeOutcome.Cancelled => DeviceUnlockOutcome.Cancelled,
        NativeOutcome.Unavailable => DeviceUnlockOutcome.Unavailable,
        NativeOutcome.NotEnrolled => DeviceUnlockOutcome.NotEnrolled,
        _ => DeviceUnlockOutcome.Failed
    };

    private enum NativeOutcome
    {
        Success,
        Cancelled,
        Unavailable,
        NotEnrolled,
        Failed
    }

    private sealed class UnlockRequest : SafeHandleZeroOrMinusOneIsInvalid
    {
        public UnlockRequest() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle()
        {
            Native.ReleaseRequest(handle);
            return true;
        }
    }

    private static class Native
    {
        private const string Library = "kei_quick_unlock";

        [DllImport(Library, EntryPoint = "kei_quick_unlock_available")]
        internal static extern NativeOutcome IsAvailable();

        [DllImport(Library, EntryPoint = "kei_quick_unlock_has_key")]
        internal static extern NativeOutcome HasKey([MarshalAs(UnmanagedType.LPUTF8Str)] string keyId, out int hasKey);

        [DllImport(Library, EntryPoint = "kei_quick_unlock_store")]
        internal static extern NativeOutcome StoreKey([MarshalAs(UnmanagedType.LPUTF8Str)] string keyId, byte[] key, int length);

        [DllImport(Library, EntryPoint = "kei_quick_unlock_delete")]
        internal static extern NativeOutcome DeleteKey([MarshalAs(UnmanagedType.LPUTF8Str)] string keyId);

        [DllImport(Library, EntryPoint = "kei_quick_unlock_request_create")]
        internal static extern UnlockRequest CreateRequest([MarshalAs(UnmanagedType.LPUTF8Str)] string keyId,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string reason);

        [DllImport(Library, EntryPoint = "kei_quick_unlock_request_wait")]
        internal static extern NativeOutcome WaitRequest(UnlockRequest request);

        [DllImport(Library, EntryPoint = "kei_quick_unlock_request_cancel")]
        internal static extern void CancelRequest(UnlockRequest request);

        [DllImport(Library, EntryPoint = "kei_quick_unlock_request_get_key")]
        internal static extern NativeOutcome GetKey(UnlockRequest request, out IntPtr key, out int length);

        [DllImport(Library, EntryPoint = "kei_quick_unlock_request_release")]
        internal static extern void ReleaseRequest(IntPtr request);
    }
}
