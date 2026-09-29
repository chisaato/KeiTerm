namespace Kei.Term.Core.Security;

using System.Text;

// 信任库导出为 OpenSSH known_hosts 格式（可被 ssh / ssh-keygen -F 直接使用，并可再次导入）
public static class OpenSshKnownHostsWriter
{
    public static string Write(IEnumerable<KnownHostEntry> entries)
    {
        var builder = new StringBuilder();
        builder.Append("# Exported by KeiTerm ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")).Append(" UTC\n");

        foreach (KnownHostEntry entry in entries)
        {
            if (entry.Status == KnownHostStatus.Revoked)
            {
                builder.Append("@revoked ");
            }

            // 模式条目（哈希/通配符）原样输出，端口已编码在模式中
            builder.Append(entry.IsPattern ? entry.Host : KnownHostMatcher.ToHostString(entry.Host, entry.Port))
                .Append(' ').Append(entry.KeyType)
                .Append(' ').Append(entry.PublicKeyBase64);

            if (!string.IsNullOrWhiteSpace(entry.Comment))
            {
                builder.Append(' ').Append(entry.Comment.Trim());
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }
}
