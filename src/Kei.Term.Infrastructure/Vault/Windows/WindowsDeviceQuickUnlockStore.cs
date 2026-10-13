using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Kei.Term.Core.Vault;

[assembly: InternalsVisibleTo("Kei.Term.Tests")]

namespace Kei.Term.Infrastructure.Vault.Windows;

// Hello 负责本应用的身份确认；DPAPI 与凭据管理器负责本机用户的密钥保管。
// 这是一项快捷解锁能力，不声称 DPAPI 的密钥只能通过 Hello 使用。
public sealed class WindowsDeviceQuickUnlockStore : IDeviceQuickUnlockStore
{
    private readonly Func<Task<nint>> _ownerWindow;
    private readonly IWindowsHelloVerifier _verifier;
    private readonly SemaphoreSlim _verificationGate = new(1, 1);

    public WindowsDeviceQuickUnlockStore(Func<Task<nint>> ownerWindow)
        : this(ownerWindow, new NativeWindowsHelloVerifier()) { }

    internal WindowsDeviceQuickUnlockStore(Func<Task<nint>> ownerWindow, IWindowsHelloVerifier verifier)
    {
        _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
        _verifier = verifier;
    }

    public string DisplayName => "Windows Hello";
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299);

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!IsSupported) return false;
        try { return await _verifier.IsAvailableAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (IsNativeFailure(ex)) { return false; }
    }

    public Task<bool> HasKeyAsync(string keyId, CancellationToken ct = default)
    {
        ValidateKeyId(keyId);
        ct.ThrowIfCancellationRequested();
        return IsSupported
            ? Task.Run(() => WindowsProtectedCredential.Exists(keyId), ct)
            : Task.FromResult(false);
    }

    public async Task StoreKeyAsync(string keyId, ReadOnlyMemory<byte> key, CancellationToken ct = default)
    {
        ValidateKeyId(keyId);
        if (key.Length != 32) throw new ArgumentException("保险库密钥必须为 32 字节。", nameof(key));
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            throw new PlatformNotSupportedException("此设备未配置可用的 Windows Hello。");

        byte[] copy = key.ToArray();
        try
        {
            await Task.Run(() => WindowsProtectedCredential.Store(keyId, copy), ct).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    public async Task<DeviceUnlockResult> UnlockKeyAsync(string keyId, string reason, CancellationToken ct = default)
    {
        ValidateKeyId(keyId);
        if (!IsSupported) return new(DeviceUnlockOutcome.Unavailable);
        bool entered = false;
        byte[]? key = null;
        try
        {
            await _verificationGate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            if (!await _verifier.IsAvailableAsync(ct).ConfigureAwait(false))
                return new(DeviceUnlockOutcome.Unavailable);

            nint owner = await _ownerWindow().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (owner == 0) return new(DeviceUnlockOutcome.Unavailable);
            WindowsHelloResult consent = await _verifier.VerifyAsync(owner, reason, ct).ConfigureAwait(false);
            if (consent != WindowsHelloResult.Verified)
                return new(MapConsent(consent));

            // 不在系统验证成功之前读取或解密本机密钥。
            ct.ThrowIfCancellationRequested();
            key = await Task.Run(() => WindowsProtectedCredential.Read(keyId), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (key is null) return new(DeviceUnlockOutcome.NotEnrolled);
            if (key.Length != 32) return new(DeviceUnlockOutcome.Failed);

            byte[] result = key;
            key = null;
            return new(DeviceUnlockOutcome.Success, result);
        }
        catch (OperationCanceledException) { return new(DeviceUnlockOutcome.Cancelled); }
        catch (Exception ex) when (IsNativeFailure(ex)) { return new(DeviceUnlockOutcome.Failed); }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (entered) _verificationGate.Release();
        }
    }

    public Task DeleteKeyAsync(string keyId, CancellationToken ct = default)
    {
        ValidateKeyId(keyId);
        ct.ThrowIfCancellationRequested();
        return IsSupported ? Task.Run(() => WindowsProtectedCredential.Delete(keyId), ct) : Task.CompletedTask;
    }

    private static DeviceUnlockOutcome MapConsent(WindowsHelloResult consent) => consent switch
    {
        WindowsHelloResult.Cancelled => DeviceUnlockOutcome.Cancelled,
        WindowsHelloResult.DeviceNotPresent or WindowsHelloResult.DisabledByPolicy => DeviceUnlockOutcome.Unavailable,
        WindowsHelloResult.NotConfiguredForUser => DeviceUnlockOutcome.Unavailable,
        _ => DeviceUnlockOutcome.Failed
    };

    private static bool IsNativeFailure(Exception ex) => ex is Win32Exception or COMException
        or CryptographicException or InvalidDataException or DllNotFoundException or EntryPointNotFoundException;

    private static void ValidateKeyId(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 512)
            throw new ArgumentException("保险库密钥标识无效。", nameof(keyId));
    }
}

internal enum WindowsHelloResult
{
    Verified = 0,
    DeviceNotPresent = 1,
    NotConfiguredForUser = 2,
    DisabledByPolicy = 3,
    DeviceBusy = 4,
    RetriesExhausted = 5,
    Cancelled = 6
}

internal interface IWindowsHelloVerifier
{
    Task<bool> IsAvailableAsync(CancellationToken ct);
    Task<WindowsHelloResult> VerifyAsync(nint owner, string reason, CancellationToken ct);
}
