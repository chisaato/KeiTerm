using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kei.Term.Core.Sync;
using ChaCha20Poly1305 = System.Security.Cryptography.ChaCha20Poly1305;

namespace Kei.Term.Infrastructure.Sync;

/// <summary>age v1 的二进制单一 scrypt recipient 子集，不处理非口令 recipient 或 ASCII armor。</summary>
public sealed class AgeSyncPackage : ISyncPackage
{
    public const int DefaultWorkFactor = 18;
    public const int MaximumWorkFactor = 18;
    private const int ChunkSize = 65536;
    private const string Version = "age-encryption.org/v1";

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<char> passphrase)
    {
        byte[] fileKey = RandomNumberGenerator.GetBytes(16);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] wrapKey = [];
        byte[] headerKey = [];
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        byte[] payloadKey = [];
        try
        {
            wrapKey = DeriveWrapKey(passphrase, salt, DefaultWorkFactor);
            headerKey = Derive(fileKey, [], "header"u8);
            payloadKey = Derive(fileKey, nonce, "payload"u8);
            byte[] wrapped = new byte[32];
            using (var aead = new ChaCha20Poly1305(wrapKey))
                aead.Encrypt(new byte[12], fileKey, wrapped.AsSpan(0, 16), wrapped.AsSpan(16));
            string prefix = $"{Version}\n-> scrypt {Base64(salt)} {DefaultWorkFactor}\n{Base64(wrapped)}\n---";
            byte[] mac = HMACSHA256.HashData(headerKey, Encoding.ASCII.GetBytes(prefix));
            byte[] header = Encoding.ASCII.GetBytes($"{prefix} {Base64(mac)}\n");
            int chunks = Math.Max(1, (int)(((long)plaintext.Length + ChunkSize - 1) / ChunkSize));
            byte[] output = new byte[checked(header.Length + 16 + plaintext.Length + chunks * 16)];
            header.CopyTo(output, 0);
            nonce.CopyTo(output, header.Length);
            int position = header.Length + 16;
            using var cipher = new ChaCha20Poly1305(payloadKey);
            Span<byte> chunkNonce = stackalloc byte[12];
            for (int i = 0; i < chunks; i++)
            {
                int length = Math.Min(ChunkSize, plaintext.Length - i * ChunkSize);
                SetNonce(chunkNonce, i, i == chunks - 1);
                cipher.Encrypt(chunkNonce, plaintext.Slice(i * ChunkSize, length), output.AsSpan(position, length), output.AsSpan(position + length, 16));
                position += length + 16;
            }
            return output;
        }
        finally
        {
            Zero(fileKey, wrapKey, headerKey, payloadKey);
        }
    }

    public byte[] Decrypt(ReadOnlySpan<byte> package, ReadOnlySpan<char> passphrase)
    {
        int position = 0;
        if (!package.StartsWith("age-encryption.org/"u8))
            throw Error(SyncPackageError.NotAge);
        if (ReadLine(package, ref position) != Version)
            throw Error(SyncPackageError.UnsupportedFormat);
        string[] args = ReadLine(package, ref position).Split(' ');
        if (args.Length != 4 || args[0] != "->" || args[1] != "scrypt")
            throw Error(SyncPackageError.CorruptOrTruncated);
        byte[] salt = Decode(args[2], 16);
        string factorText = args[3];
        if (factorText.Length == 0 || factorText[0] == '0' || factorText.Any(c => c < '0' || c > '9'))
            throw Error(SyncPackageError.CorruptOrTruncated);
        if (!int.TryParse(factorText, NumberStyles.None, CultureInfo.InvariantCulture, out int factor) || factor > MaximumWorkFactor)
            throw Error(SyncPackageError.WorkFactorTooHigh);
        byte[] wrapped = Decode(ReadLine(package, ref position), 32);
        int macStart = position;
        string macLine = ReadLine(package, ref position);
        if (!macLine.StartsWith("--- ", StringComparison.Ordinal)) throw Error(SyncPackageError.CorruptOrTruncated);
        byte[] expectedMac = Decode(macLine[4..], 32);
        byte[] wrapKey = DeriveWrapKey(passphrase, salt, factor);
        byte[] fileKey = new byte[16];
        byte[] headerKey = [];
        try
        {
            try
            {
                using var cipher = new ChaCha20Poly1305(wrapKey);
                cipher.Decrypt(new byte[12], wrapped.AsSpan(0, 16), wrapped.AsSpan(16), fileKey);
            }
            catch (AuthenticationTagMismatchException) { throw Error(SyncPackageError.WrongPassphrase); }
            headerKey = Derive(fileKey, [], "header"u8);
            byte[] actualMac = HMACSHA256.HashData(headerKey, package[..(macStart + 3)]);
            if (!CryptographicOperations.FixedTimeEquals(actualMac, expectedMac))
                throw Error(SyncPackageError.HeaderAuthenticationFailed);
            return DecryptPayload(package[position..], fileKey);
        }
        finally { Zero(wrapKey, fileKey, headerKey); }
    }

    // 独立 STREAM 接缝供 CCTV 向量使用；只在全部分块验证成功后移交结果所有权。
    internal static byte[] DecryptPayload(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> fileKey)
    {
        if (payload.Length < 32) throw Error(SyncPackageError.CorruptOrTruncated);
        byte[] key = Derive(fileKey, payload[..16], "payload"u8);
        payload = payload[16..];
        int chunks = (int)(((long)payload.Length + ChunkSize + 15) / (ChunkSize + 16));
        int plainLength = payload.Length - chunks * 16;
        if (plainLength < 0 || payload.Length - (chunks - 1) * (ChunkSize + 16) < 16)
        { Zero(key); throw Error(SyncPackageError.CorruptOrTruncated); }
        byte[] plain = new byte[plainLength];
        bool success = false;
        try
        {
            using var cipher = new ChaCha20Poly1305(key);
            Span<byte> nonce = stackalloc byte[12];
            int outputPosition = 0;
            for (int i = 0; i < chunks; i++)
            {
                int length = Math.Min(ChunkSize + 16, payload.Length);
                bool final = i == chunks - 1;
                if (length < 16 || (final && length == 16 && i != 0))
                    throw Error(SyncPackageError.CorruptOrTruncated);
                SetNonce(nonce, i, final);
                cipher.Decrypt(nonce, payload[..(length - 16)], payload.Slice(length - 16, 16), plain.AsSpan(outputPosition, length - 16));
                payload = payload[length..];
                outputPosition += length - 16;
            }
            success = true;
            return plain;
        }
        catch (AuthenticationTagMismatchException) { throw Error(SyncPackageError.CorruptOrTruncated); }
        finally
        {
            Zero(key);
            if (!success) Zero(plain);
        }
    }

    private static byte[] DeriveWrapKey(ReadOnlySpan<char> passphrase, byte[] salt, int factor)
    {
        byte[] password = new byte[Encoding.UTF8.GetByteCount(passphrase)];
        Encoding.UTF8.GetBytes(passphrase, password);
        byte[] fullSalt = new byte[28 + salt.Length];
        "age-encryption.org/v1/scrypt"u8.CopyTo(fullSalt);
        salt.CopyTo(fullSalt, 28);
        try
        {
            return AgeScrypt.Derive(password, fullSalt, factor);
        }
        finally { CryptographicOperations.ZeroMemory(password); }
    }

    private static byte[] Derive(ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info)
    {
        byte[] output = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, output, salt, info);
        return output;
    }

    private static string Base64(ReadOnlySpan<byte> value) => Convert.ToBase64String(value).TrimEnd('=');

    private static byte[] Decode(string value, int length)
    {
        byte[] result;
        try { result = Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '=')); }
        catch (FormatException) { throw Error(SyncPackageError.CorruptOrTruncated); }
        if (result.Length != length || Base64(result) != value)
            throw Error(SyncPackageError.CorruptOrTruncated);
        return result;
    }

    private static string ReadLine(ReadOnlySpan<byte> data, ref int position)
    {
        // 本子集的最长合法行远小于 128；先限长，避免恶意头部的大量分配。
        int length = data[position..].IndexOf((byte)'\n');
        if (length < 0 || length > 128) throw Error(SyncPackageError.CorruptOrTruncated);
        ReadOnlySpan<byte> line = data.Slice(position, length);
        foreach (byte b in line)
            if (b < 32 || b > 126) throw Error(SyncPackageError.CorruptOrTruncated);
        position += length + 1;
        return Encoding.ASCII.GetString(line);
    }

    private static void SetNonce(Span<byte> nonce, int index, bool final)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(3, 8), (ulong)index);
        nonce[11] = final ? (byte)1 : (byte)0;
    }

    private static void Zero(params byte[][] buffers)
    {
        foreach (byte[] buffer in buffers) CryptographicOperations.ZeroMemory(buffer);
    }

    private static SyncPackageException Error(SyncPackageError error) => new(error, error switch
    {
        SyncPackageError.NotAge => "同步包不是 age 文件。",
        SyncPackageError.WrongPassphrase => "同步口令不正确，或口令包装头部已损坏。",
        SyncPackageError.HeaderAuthenticationFailed => "同步包头部校验失败。",
        SyncPackageError.WorkFactorTooHigh => "同步包工作因子超过安全上限。",
        SyncPackageError.UnsupportedFormat => "不支持的同步包版本。",
        _ => "同步包损坏或被截断。",
    });
}
