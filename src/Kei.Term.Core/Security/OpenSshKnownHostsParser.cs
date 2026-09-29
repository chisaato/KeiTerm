namespace Kei.Term.Core.Security;

public sealed record KnownHostsParseResult(IReadOnlyList<KnownHostEntry> Entries, int SkippedLines);

// 解析 OpenSSH known_hosts：[@marker] hostpatterns keytype base64-key [comment]
// - 纯主机列表（含 [host]:port）拆为精确条目；哈希/通配符/否定列表保留为模式条目（Port = 0）
// - @revoked 导入为吊销条目；@cert-authority 暂不支持（SSH 证书为后续规划），计入跳过行
public static class OpenSshKnownHostsParser
{
    public static KnownHostsParseResult Parse(string content, DateTime? importedAtUtc = null)
    {
        DateTime now = importedAtUtc ?? DateTime.UtcNow;
        List<KnownHostEntry> entries = [];
        int skipped = 0;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!TryParseLine(line, now, entries))
            {
                skipped++;
            }
        }

        return new KnownHostsParseResult(entries, skipped);
    }

    private static bool TryParseLine(string line, DateTime now, List<KnownHostEntry> sink)
    {
        string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int index = 0;
        KnownHostStatus status = KnownHostStatus.Trusted;

        if (tokens[0].StartsWith('@'))
        {
            if (!string.Equals(tokens[0], "@revoked", StringComparison.Ordinal))
            {
                return false;
            }

            status = KnownHostStatus.Revoked;
            index = 1;
        }

        if (tokens.Length < index + 3)
        {
            return false;
        }

        string hostPatterns = tokens[index];
        string keyType = tokens[index + 1];
        string keyBase64 = tokens[index + 2];
        string? comment = tokens.Length > index + 3 ? string.Join(' ', tokens[(index + 3)..]) : null;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(keyBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        // 行内声明的算法必须与密钥 blob 自描述的算法一致，防止错位/篡改行被当成有效信任
        if (!string.Equals(HostKeyFingerprint.ReadKeyType(blob), keyType, StringComparison.Ordinal))
        {
            return false;
        }

        string fingerprint = HostKeyFingerprint.Sha256(blob);
        foreach ((string host, int port) in ExpandHosts(hostPatterns))
        {
            sink.Add(new KnownHostEntry
            {
                Host = host,
                Port = port,
                KeyType = keyType,
                PublicKeyBase64 = keyBase64,
                FingerprintSha256 = fingerprint,
                Status = status,
                Source = KnownHostSource.Imported,
                Comment = comment,
                CreatedAt = now
            });
        }

        return true;
    }

    private static IEnumerable<(string Host, int Port)> ExpandHosts(string hostPatterns)
    {
        bool isPattern = hostPatterns.StartsWith("|1|", StringComparison.Ordinal)
                         || hostPatterns.IndexOfAny(['*', '?', '!']) >= 0;
        if (isPattern)
        {
            // 模式整体保留，匹配时按列表语义求值
            yield return (hostPatterns, 0);
            yield break;
        }

        foreach (string item in hostPatterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return ParseHostItem(item);
        }
    }

    // "[host]:port" → (host, port)；裸主机名 → (host, 22)
    private static (string Host, int Port) ParseHostItem(string item)
    {
        if (item.StartsWith('['))
        {
            int close = item.IndexOf(']');
            if (close > 1
                && item.Length > close + 2
                && item[close + 1] == ':'
                && int.TryParse(item[(close + 2)..], out int port)
                && port is > 0 and <= 65535)
            {
                return (item[1..close].ToLowerInvariant(), port);
            }
        }

        return (item.ToLowerInvariant(), KnownHostMatcher.DefaultPort);
    }
}
