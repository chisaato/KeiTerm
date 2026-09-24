using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Services;

namespace Kei.Term.App.ViewModels;

// 私钥文件读取与加密探测：App 层共用（连接物化与 Vault 密钥导入）
internal static class PrivateKeyImport
{
    // 读取私钥文件；不存在/不可读返回 null
    public static async Task<FileKeyReadResult?> ReadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var content = await File.ReadAllTextAsync(path, ct);
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            return new FileKeyReadResult(content, DetectEncrypted(content));
        }
        catch
        {
            return null;
        }
    }

    // 文本特征判断私钥是否需要口令（不引入 SSH 依赖）
    public static bool DetectEncrypted(string content)
    {
        // PKCS#8 加密私钥
        if (content.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
        {
            return true;
        }

        // 传统 OpenSSH/PEM 加密头
        if (content.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal)
            || content.Contains("DEK-Info:", StringComparison.Ordinal))
        {
            return true;
        }

        // OpenSSH 新格式（openssh-key-v1）加密私钥的 KDF 名固定为 bcrypt
        return content.Contains("bcrypt", StringComparison.OrdinalIgnoreCase);
    }

    // UTF-8 字节数（用于材料大小展示）
    public static int ByteCount(string content) => Encoding.UTF8.GetByteCount(content);
}
