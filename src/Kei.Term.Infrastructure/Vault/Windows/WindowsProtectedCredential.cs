using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Kei.Term.Infrastructure.Vault.Windows;

// 泛型凭据只包含 DPAPI 密文，不保存主密码；LOCAL_MACHINE 持久化不允许域漫游。
internal static class WindowsProtectedCredential
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;
    private const uint UiForbidden = 1;

    public static bool Exists(string keyId)
    {
        if (!CredRead(Target(keyId), GenericCredential, 0, out nint credential))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == NotFound) return false;
            throw new Win32Exception(error);
        }
        CredFree(credential);
        return true;
    }

    public static void Store(string keyId, byte[] key)
    {
        byte[] encrypted = Protect(keyId, key, decrypt: false);
        nint blob = Marshal.AllocHGlobal(encrypted.Length);
        try
        {
            Marshal.Copy(encrypted, 0, blob, encrypted.Length);
            Credential credential = new()
            {
                Type = GenericCredential,
                TargetName = Target(keyId),
                CredentialBlobSize = (uint)encrypted.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = "Kei.Term"
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            ZeroNative(blob, encrypted.Length);
            Marshal.FreeHGlobal(blob);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public static byte[]? Read(string keyId)
    {
        if (!CredRead(Target(keyId), GenericCredential, 0, out nint pointer))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == NotFound) return null;
            throw new Win32Exception(error);
        }
        byte[]? encrypted = null;
        try
        {
            Credential credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize is 0 or > 2560 || credential.CredentialBlob == 0)
                throw new InvalidDataException("本机解锁凭据已损坏。");
            encrypted = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, encrypted, 0, encrypted.Length);
            return Protect(keyId, encrypted, decrypt: true);
        }
        finally
        {
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            CredFree(pointer);
        }
    }

    public static void Delete(string keyId)
    {
        if (CredDelete(Target(keyId), GenericCredential, 0)) return;
        int error = Marshal.GetLastPInvokeError();
        if (error != NotFound) throw new Win32Exception(error);
    }

    // 熵绑定保险库版本，防止把另一个保险库的密文移动到这个凭据名称后解密。
    internal static byte[] Protect(string keyId, byte[] input, bool decrypt)
    {
        byte[] entropy = SHA256.HashData(Encoding.UTF8.GetBytes("Kei.Term.DeviceQuickUnlock.v1:" + keyId));
        nint inputPointer = Marshal.AllocHGlobal(input.Length);
        nint entropyPointer = Marshal.AllocHGlobal(entropy.Length);
        DataBlob output = default;
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            Marshal.Copy(entropy, 0, entropyPointer, entropy.Length);
            DataBlob inputBlob = new() { Size = (uint)input.Length, Data = inputPointer };
            DataBlob entropyBlob = new() { Size = (uint)entropy.Length, Data = entropyPointer };
            bool success = decrypt
                ? CryptUnprotectData(ref inputBlob, 0, ref entropyBlob, 0, 0, UiForbidden, out output)
                : CryptProtectData(ref inputBlob, null, ref entropyBlob, 0, 0, UiForbidden, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (output.Size is 0 or > 2560 || output.Data == 0)
                throw new InvalidDataException("系统返回了无效的解锁材料。");
            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ZeroNative(inputPointer, input.Length);
            ZeroNative(entropyPointer, entropy.Length);
            Marshal.FreeHGlobal(inputPointer);
            Marshal.FreeHGlobal(entropyPointer);
            CryptographicOperations.ZeroMemory(entropy);
            if (output.Data != 0)
            {
                ZeroNative(output.Data, checked((int)output.Size));
                LocalFree(output.Data);
            }
        }
    }

    private static string Target(string keyId) => "Kei.Term/DeviceQuickUnlock/v1/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyId)));

    private static void ZeroNative(nint pointer, int length)
    {
        for (int index = 0; index < length; index++) Marshal.WriteByte(pointer, index, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint Size;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description,
        ref DataBlob entropy, nint reserved, nint prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description,
        ref DataBlob entropy, nint reserved, nint prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
