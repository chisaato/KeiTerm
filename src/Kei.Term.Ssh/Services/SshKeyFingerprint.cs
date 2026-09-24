namespace Kei.Term.Ssh.Services;

using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;

// SSH 私钥指纹工具：从私钥内容手工构造公钥 SSH wire-format blob 后取 SHA-256，
// 输出与 OpenSSH `ssh-keygen -l -E sha256` 一致的冒号分隔小写 hex。
public static class SshKeyFingerprint
{
    // 解析私钥并计算指纹；解析失败（格式错误/口令错/不支持类型）返回 null
    public static string? Compute(string pemContent, string? passphrase = null)
    {
        if (string.IsNullOrWhiteSpace(pemContent))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(pemContent));
            using var keyFile = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(stream)
                : new PrivateKeyFile(stream, passphrase);

            var blob = BuildPublicKeyBlob(keyFile.Key);
            if (blob == null)
            {
                // sk-* 等不支持类型：无法归一化公钥，返回 null 不阻塞导入
                return null;
            }

            var hash = SHA256.HashData(blob);
            return string.Join(":", hash.Select(b => b.ToString("x2")));
        }
        catch
        {
            // 格式错误 / 口令错 / 底层解析异常统一视为无法计算
            return null;
        }
    }

    // 按 key 类型构造 SSH 公钥 wire-format blob；不支持的类型返回 null
    private static byte[]? BuildPublicKeyBlob(Key key)
    {
        using var ms = new MemoryStream();
        switch (key)
        {
            case RsaKey rsa:
                // ssh-rsa：string "ssh-rsa" + mpint e + mpint n
                WriteString(ms, "ssh-rsa");
                WriteMpint(ms, rsa.Exponent);
                WriteMpint(ms, rsa.Modulus);
                break;

            case ED25519Key ed:
                // ssh-ed25519：string "ssh-ed25519" + string pub32
                WriteString(ms, "ssh-ed25519");
                WriteString(ms, ed.PublicKey);
                break;

            case EcdsaKey ec:
            {
                // 曲线由密钥位数决定；Q 为 0x04 || X || Y 非压缩点
                var (algorithm, curve) = ec.Ecdsa.KeySize switch
                {
                    256 => ("ecdsa-sha2-nistp256", "nistp256"),
                    384 => ("ecdsa-sha2-nistp384", "nistp384"),
                    521 => ("ecdsa-sha2-nistp521", "nistp521"),
                    _ => (null, null),
                };
                if (algorithm == null || curve == null)
                {
                    return null;
                }

                var parameters = ec.Ecdsa.ExportParameters(false);
                if (parameters.Q.X == null || parameters.Q.Y == null)
                {
                    return null;
                }

                var point = new byte[1 + parameters.Q.X.Length + parameters.Q.Y.Length];
                point[0] = 0x04;
                parameters.Q.X.CopyTo(point, 1);
                parameters.Q.Y.CopyTo(point, 1 + parameters.Q.X.Length);

                WriteString(ms, algorithm);
                WriteString(ms, curve);
                WriteString(ms, point);
                break;
            }

            default:
                // DSA / sk-ssh-ed25519 / sk-ecdsa 等一期不支持
                return null;
        }

        return ms.ToArray();
    }

    // SSH string：4 字节大端长度 + 原始字节
    private static void WriteString(Stream stream, string value)
        => WriteString(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteString(Stream stream, byte[] value)
    {
        stream.WriteByte((byte)(value.Length >> 24));
        stream.WriteByte((byte)(value.Length >> 16));
        stream.WriteByte((byte)(value.Length >> 8));
        stream.WriteByte((byte)value.Length);
        stream.Write(value, 0, value.Length);
    }

    // SSH mpint：大端补码、去多余前导零；最高位为 1 时补一个 0x00 保持正号
    private static void WriteMpint(Stream stream, BigInteger value)
    {
        var bytes = value.ToByteArray();
        Array.Reverse(bytes);

        var start = 0;
        while (start < bytes.Length - 1 && bytes[start] == 0x00 && (bytes[start + 1] & 0x80) == 0)
        {
            start++;
        }

        var length = bytes.Length - start;
        stream.WriteByte((byte)(length >> 24));
        stream.WriteByte((byte)(length >> 16));
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)length);
        stream.Write(bytes, start, length);
    }
}
