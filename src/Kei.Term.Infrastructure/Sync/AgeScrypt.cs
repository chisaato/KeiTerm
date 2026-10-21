using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NSec.Cryptography;

namespace Kei.Term.Infrastructure.Sync;

// NSec 公开 API 固定 32 字节 salt；age 需要 44 字节，因此只对同一 libsodium 的低层导出做桥接。
internal static class AgeScrypt
{
    private static readonly Lazy<ScryptFunction> Function = new(() =>
    {
        // 先触发 NSec 的原生初始化与平台检查；句柄在进程生命周期内保持有效。
        _ = PasswordBasedKeyDerivationAlgorithm.Scrypt(new ScryptParameters { Cost = 2, BlockSize = 8, Parallelization = 1 });
        nint library = NativeLibrary.Load("libsodium", typeof(Key).Assembly, null);
        return Marshal.GetDelegateForFunctionPointer<ScryptFunction>(NativeLibrary.GetExport(library, "crypto_pwhash_scryptsalsa208sha256_ll"));
    });

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ScryptFunction([In] byte[] password, nuint passwordLength, [In] byte[] salt, nuint saltLength,
        ulong cost, uint blockSize, uint parallelization, [Out] byte[] output, nuint outputLength);

    internal static byte[] Derive(byte[] password, byte[] salt, int factor)
    {
        byte[] output = new byte[32];
        if (Function.Value(password, (nuint)password.Length, salt, (nuint)salt.Length, 1UL << factor, 8, 1, output, 32) == 0)
            return output;
        CryptographicOperations.ZeroMemory(output);
        throw new CryptographicException("scrypt 密钥派生失败。");
    }
}
