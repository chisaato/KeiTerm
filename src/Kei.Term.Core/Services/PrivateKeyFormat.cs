namespace Kei.Term.Core.Services;

using System.Buffers.Binary;
using System.Text;

// 私钥文本格式探测（不依赖 SSH 库）：判断是否需要口令才能解开
public static class PrivateKeyFormat
{
    private const string OpenSshBegin = "-----BEGIN OPENSSH PRIVATE KEY-----";
    private const string OpenSshEnd = "-----END OPENSSH PRIVATE KEY-----";
    private static readonly byte[] OpenSshMagic = Encoding.ASCII.GetBytes("openssh-key-v1\0");

    public static bool IsEncrypted(string content)
    {
        // PKCS#8 加密私钥
        if (content.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
        {
            return true;
        }

        // 传统 PEM（RSA / EC / DSA）加密头
        if (content.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal))
        {
            return true;
        }

        // PuTTY .ppk：Encryption 行不为 none
        if (content.StartsWith("PuTTY-User-Key-File", StringComparison.Ordinal))
        {
            return !content.Contains("Encryption: none", StringComparison.Ordinal);
        }

        // OpenSSH 新格式（ssh-keygen 默认）：正文为 Base64，必须解码后读 ciphername，
        // 不能在文本里找 "bcrypt"——该字段处于 Base64 内部，明文永远搜不到
        return IsEncryptedOpenSsh(content);
    }

    private static bool IsEncryptedOpenSsh(string content)
    {
        int begin = content.IndexOf(OpenSshBegin, StringComparison.Ordinal);
        int end = content.IndexOf(OpenSshEnd, StringComparison.Ordinal);
        if (begin < 0 || end <= begin)
        {
            return false;
        }

        string body = content[(begin + OpenSshBegin.Length)..end];
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(string.Concat(body.Where(c => !char.IsWhiteSpace(c))));
        }
        catch (FormatException)
        {
            return false;
        }

        // 结构：magic "openssh-key-v1\0" + string ciphername + string kdfname + …；ciphername 为 "none" 即未加密
        if (blob.Length < OpenSshMagic.Length + 4 || !blob.AsSpan(0, OpenSshMagic.Length).SequenceEqual(OpenSshMagic))
        {
            return false;
        }

        int offset = OpenSshMagic.Length;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset));
        if (length > 64 || blob.Length < offset + 4 + length)
        {
            return false;
        }

        string cipher = Encoding.ASCII.GetString(blob, offset + 4, (int)length);
        return !string.Equals(cipher, "none", StringComparison.Ordinal);
    }
}
