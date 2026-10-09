namespace Kei.Term.Infrastructure.Vault;

using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

// 保管库密码学原语：KDF 派生、Vault Key 包装、条目加解密与 AAD 绑定。
// 算法细节集中在这里，InternalVaultManager 只负责密钥状态与持久化编排。
internal static class VaultCryptography
{
    // 生产 Argon2id 参数：m=65536 KiB、t=3、p=1（NSec 明确拒绝其他并行度）
    internal const long Argon2MemorySizeKiB = 65_536;
    internal const long Argon2Passes = 3;
    internal const int Argon2Parallelism = 1;

    internal const int SaltLength = 16;
    internal const int KeyLength = 32;
    internal const int VaultIdLength = 16;

    // XChaCha20-Poly1305 块：nonce(24) || ciphertext || tag(16)
    internal const int AeadNonceLength = 24;
    internal const int AeadTagLength = 16;

    // 旧 AES-256-GCM 块：nonce(12) || tag(16) || ciphertext
    internal const int LegacyNonceLength = 12;
    internal const int LegacyTagLength = 16;

    // 旧库 KDF 标识；缺省（早期库未写入）也视为它
    internal const string Pbkdf2Sha512Id = "pbkdf2-sha512:600000";
    internal const int Pbkdf2Iterations = 600_000;

    internal const string PlainAlgorithm = "PLAIN";
    internal const string LegacyAlgorithm = "AES-256-GCM";
    internal const string SealedAlgorithm = "XCHACHA20-POLY1305";

    // 解锁校验用已知明文
    internal const string VerifierPlaintext = "keiterm-vault-verifier-v1";

    private static readonly byte[] WrapAadPrefix = Encoding.UTF8.GetBytes("keiterm/vk/v1");
    private static readonly byte[] ItemAadPrefix = Encoding.UTF8.GetBytes("keiterm/item/v1");
    private static readonly byte[] VerifierPlaintextBytes = Encoding.UTF8.GetBytes(VerifierPlaintext);

    // 生产参数对象；测试通过 InternalVaultManager 注入更小的参数避免跑 64 MiB
    internal static Argon2Parameters ProductionArgon2Parameters { get; } = new()
    {
        MemorySize = Argon2MemorySizeKiB,
        NumberOfPasses = Argon2Passes,
        DegreeOfParallelism = Argon2Parallelism,
    };

    // 生产 KDF 标识 "argon2id:m=65536,t=3,p=1"
    internal static string ProductionKdfId => FormatArgon2Id(ProductionArgon2Parameters);

    internal static string FormatArgon2Id(in Argon2Parameters parameters)
        => $"argon2id:m={parameters.MemorySize},t={parameters.NumberOfPasses},p={parameters.DegreeOfParallelism}";

    // 密码派生包装密钥：按标识解析，未知标识或非法参数抛 NotSupportedException（不得报成密码错误）
    internal static byte[] DeriveWrappingKey(string kdfId, string password, byte[] salt)
    {
        if (string.Equals(kdfId, Pbkdf2Sha512Id, StringComparison.Ordinal))
        {
            // 旧库不对口令做 NFKC，保持与历史口令字节完全一致
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, KeyLength);
        }

        if (!TryParseArgon2Id(kdfId, out Argon2Parameters parameters))
        {
            throw new NotSupportedException($"不支持的 Vault KDF: {kdfId}");
        }

        return DeriveArgon2Id(password, salt, parameters);
    }

    internal static byte[] DeriveArgon2Id(string password, byte[] salt, in Argon2Parameters parameters)
    {
        PasswordBasedKeyDerivationAlgorithm algorithm = PasswordBasedKeyDerivationAlgorithm.Argon2id(parameters);
        // 只有 Argon2id 路径把口令 NFKC 后再 UTF-8；派生后立即清零口令字节
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        try
        {
            return algorithm.DeriveBytes(passwordBytes, salt, KeyLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    // 解析 argon2id:m=..,t=..,p=..；p != 1 或格式错误一律失败
    private static bool TryParseArgon2Id(string kdfId, out Argon2Parameters parameters)
    {
        parameters = default;
        const string prefix = "argon2id:";
        if (kdfId == null || !kdfId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        long memoryKiB = 0;
        long passes = 0;
        int parallelism = 0;
        foreach (string part in kdfId[prefix.Length..].Split(','))
        {
            string[] kv = part.Split('=');
            if (kv.Length != 2 || !long.TryParse(kv[1], out long value))
            {
                return false;
            }

            switch (kv[0])
            {
                case "m": memoryKiB = value; break;
                case "t": passes = value; break;
                case "p":
                    // NSec/libsodium 只接受并行度 1
                    if (value != 1) return false;
                    parallelism = 1;
                    break;
                default: return false;
            }
        }

        if (memoryKiB <= 0 || passes <= 0 || parallelism != 1)
        {
            return false;
        }

        parameters = new Argon2Parameters
        {
            MemorySize = memoryKiB,
            NumberOfPasses = passes,
            DegreeOfParallelism = parallelism,
        };
        return true;
    }

    // 包装 AAD = UTF-8 "keiterm/vk/v1" || vault_id(16)
    internal static byte[] BuildWrapAad(byte[] vaultId)
    {
        byte[] aad = new byte[WrapAadPrefix.Length + vaultId.Length];
        WrapAadPrefix.CopyTo(aad, 0);
        vaultId.CopyTo(aad, WrapAadPrefix.Length);
        return aad;
    }

    // 条目 AAD = UTF-8 "keiterm/item/v1" || 主键 Guid 的 16 字节
    internal static byte[] BuildItemAad(Guid itemId)
    {
        byte[] aad = new byte[ItemAadPrefix.Length + 16];
        ItemAadPrefix.CopyTo(aad, 0);
        itemId.TryWriteBytes(aad.AsSpan(ItemAadPrefix.Length));
        return aad;
    }

    // 输出块：nonce(24) || ciphertext || tag(16)
    internal static byte[] Seal(byte[] key, byte[] aad, byte[] plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(AeadNonceLength);
        using Key nsecKey = ImportKey(key);
        byte[] ciphertext = AeadAlgorithm.XChaCha20Poly1305.Encrypt(nsecKey, nonce, aad, plaintext);
        byte[] block = new byte[AeadNonceLength + ciphertext.Length];
        nonce.CopyTo(block, 0);
        ciphertext.CopyTo(block, AeadNonceLength);
        return block;
    }

    internal static byte[] Open(byte[] key, byte[] aad, byte[] block)
    {
        if (block.Length < AeadNonceLength + AeadTagLength)
        {
            throw new CryptographicException("Vault 密文块长度非法");
        }

        ReadOnlySpan<byte> nonce = block.AsSpan(0, AeadNonceLength);
        ReadOnlySpan<byte> ciphertext = block.AsSpan(AeadNonceLength);
        using Key nsecKey = ImportKey(key);
        // NSec 在校验失败时返回 null（不抛异常），这里统一转成 CryptographicException
        byte[]? plaintext = AeadAlgorithm.XChaCha20Poly1305.Decrypt(nsecKey, nonce, aad, ciphertext);
        return plaintext ?? throw new CryptographicException("Vault 密文校验失败");
    }

    internal static byte[] SealVerifier(byte[] vaultKey, byte[] vaultId)
        => Seal(vaultKey, BuildWrapAad(vaultId), VerifierPlaintextBytes);

    internal static bool VerifierMatches(byte[] vaultKey, byte[] vaultId, byte[] sealedVerifier)
    {
        byte[] plaintext;
        try
        {
            plaintext = Open(vaultKey, BuildWrapAad(vaultId), sealedVerifier);
        }
        catch (CryptographicException)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(plaintext, VerifierPlaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // 旧 AES-256-GCM：blob = nonce(12) || tag(16) || ciphertext
    internal static byte[] LegacySeal(byte[] mek, byte[] plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(LegacyNonceLength);
        byte[] tag = new byte[LegacyTagLength];
        byte[] cipher = new byte[plaintext.Length];

        using (var aes = new AesGcm(mek, LegacyTagLength))
        {
            aes.Encrypt(nonce, plaintext, cipher, tag);
        }

        byte[] blob = new byte[LegacyNonceLength + LegacyTagLength + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, LegacyNonceLength);
        cipher.CopyTo(blob, LegacyNonceLength + LegacyTagLength);
        return blob;
    }

    internal static byte[] LegacyOpen(byte[] mek, byte[] blob)
    {
        if (blob.Length < LegacyNonceLength + LegacyTagLength)
        {
            throw new CryptographicException("Vault blob 长度非法");
        }

        ReadOnlySpan<byte> nonce = blob.AsSpan(0, LegacyNonceLength);
        ReadOnlySpan<byte> tag = blob.AsSpan(LegacyNonceLength, LegacyTagLength);
        ReadOnlySpan<byte> cipher = blob.AsSpan(LegacyNonceLength + LegacyTagLength);
        byte[] plain = new byte[cipher.Length];

        using var aes = new AesGcm(mek, LegacyTagLength);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    internal static bool TryLegacyVerifierMatches(byte[] mek, byte[] verifierBlob)
    {
        byte[] plaintext;
        try
        {
            plaintext = LegacyOpen(mek, verifierBlob);
        }
        catch (CryptographicException)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(plaintext, VerifierPlaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static byte[] SplitNonce(byte[] block) => block.AsSpan(0, AeadNonceLength).ToArray();

    internal static byte[] SplitTag(byte[] block) => block.AsSpan(block.Length - AeadTagLength).ToArray();

    private static Key ImportKey(byte[] key)
        => Key.Import(AeadAlgorithm.XChaCha20Poly1305, key, KeyBlobFormat.RawSymmetricKey);
}
