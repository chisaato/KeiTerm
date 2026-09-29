namespace Kei.Term.Core.Security;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

public enum KnownHostStatus
{
    Trusted = 0,
    // 对应 OpenSSH @revoked：命中即无条件拒绝
    Revoked = 1
}

public enum KnownHostSource
{
    // 首次连接自动信任（TOFU）
    FirstUse = 0,
    // 用户在确认框中显式接受
    UserConfirmed = 1,
    // 从 OpenSSH known_hosts 导入
    Imported = 2
}

// 主机密钥信任条目。
// Port > 0：精确条目，Host 为小写主机名/IP；
// Port = 0：模式条目，Host 保存 OpenSSH 原始模式串（哈希 |1|salt|hash、通配符、否定列表），端口语义编码在模式内。
public sealed class KnownHostEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string KeyType { get; set; } = string.Empty;
    public string PublicKeyBase64 { get; set; } = string.Empty;
    public string FingerprintSha256 { get; set; } = string.Empty;
    public KnownHostStatus Status { get; set; } = KnownHostStatus.Trusted;
    public KnownHostSource Source { get; set; } = KnownHostSource.FirstUse;
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }

    public bool IsPattern => Port == 0;
}

// 服务器在密钥交换中呈现的主机公钥（SSH wire-format blob）
public sealed record PresentedHostKey(string Host, int Port, string KeyType, byte[] PublicKey)
{
    public string PublicKeyBase64 => Convert.ToBase64String(PublicKey);

    public string FingerprintSha256 => HostKeyFingerprint.Sha256(PublicKey);
}

public enum HostKeyVerdict
{
    // 与已信任密钥一致
    Trusted,
    // 从未见过该主机（或该主机未登记这种算法的密钥）
    Unknown,
    // 该主机同算法的已信任密钥与呈现密钥不一致：疑似中间人或服务器重装
    Changed,
    // 呈现密钥已被标记吊销
    Revoked
}

public sealed record HostKeyEvaluation(
    HostKeyVerdict Verdict,
    PresentedHostKey Presented,
    // 命中该主机端点的全部条目（UI 可展示"之前记录的指纹"）
    IReadOnlyList<KnownHostEntry> KnownEntries);

public enum HostKeyPolicy
{
    // 未知主机弹窗确认，变更强警示后由用户决定（无确认 UI 时退化为 AcceptNew）
    Ask = 0,
    // 未知主机自动信任并记录（等价 StrictHostKeyChecking=accept-new），变更一律拒绝
    AcceptNew = 1,
    // 只接受已记录的主机
    Strict = 2
}

public enum HostKeyDecision
{
    Reject,
    AcceptOnce,
    AcceptAndRemember
}

public static class HostKeyFingerprint
{
    // 与 OpenSSH `ssh-keygen -l` 默认输出一致：SHA256: + 无填充 Base64
    public static string Sha256(byte[] publicKeyBlob)
        => "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKeyBlob)).TrimEnd('=');

    // 读取 wire-format blob 开头的算法名（uint32 长度 + ASCII）；格式非法返回 null
    public static string? ReadKeyType(byte[] publicKeyBlob)
    {
        if (publicKeyBlob.Length < 4)
        {
            return null;
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(publicKeyBlob);
        if (length == 0 || length > 64 || publicKeyBlob.Length < 4 + length)
        {
            return null;
        }

        return Encoding.ASCII.GetString(publicKeyBlob, 4, (int)length);
    }
}

public static class HostKeyVerifier
{
    // entries 须为已按端点匹配过滤的条目（见 KnownHostMatcher）
    public static HostKeyEvaluation Evaluate(PresentedHostKey presented, IReadOnlyList<KnownHostEntry> entries)
    {
        string presentedKey = presented.PublicKeyBase64;
        List<KnownHostEntry> sameKey = entries
            .Where(e => string.Equals(e.PublicKeyBase64, presentedKey, StringComparison.Ordinal))
            .ToList();

        // 吊销优先于信任：同一把钥匙既被信任又被吊销时按吊销处理
        if (sameKey.Any(e => e.Status == KnownHostStatus.Revoked))
        {
            return new HostKeyEvaluation(HostKeyVerdict.Revoked, presented, entries);
        }

        if (sameKey.Any(e => e.Status == KnownHostStatus.Trusted))
        {
            return new HostKeyEvaluation(HostKeyVerdict.Trusted, presented, entries);
        }

        // 同算法已有不同的信任密钥 → 变更；仅有其它算法的密钥 → 仍视为未知（服务器可能换了协商算法）
        bool sameTypeTrusted = entries.Any(e =>
            e.Status == KnownHostStatus.Trusted
            && string.Equals(e.KeyType, presented.KeyType, StringComparison.Ordinal));

        return new HostKeyEvaluation(
            sameTypeTrusted ? HostKeyVerdict.Changed : HostKeyVerdict.Unknown,
            presented,
            entries);
    }

    // 策略可直接裁决时返回决定；需要用户确认时返回 null
    public static HostKeyDecision? DecideWithoutPrompt(HostKeyVerdict verdict, HostKeyPolicy policy) => verdict switch
    {
        HostKeyVerdict.Trusted => HostKeyDecision.AcceptOnce,
        HostKeyVerdict.Revoked => HostKeyDecision.Reject,
        HostKeyVerdict.Unknown => policy switch
        {
            HostKeyPolicy.Strict => HostKeyDecision.Reject,
            HostKeyPolicy.AcceptNew => HostKeyDecision.AcceptAndRemember,
            _ => null
        },
        // 密钥变更只允许人工确认，自动策略一律拒绝
        HostKeyVerdict.Changed => policy == HostKeyPolicy.Ask ? null : HostKeyDecision.Reject,
        _ => HostKeyDecision.Reject
    };
}

// OpenSSH known_hosts 主机匹配语义
public static class KnownHostMatcher
{
    public const int DefaultPort = 22;

    // OpenSSH 规范化的主机串：默认端口为裸主机名，否则 [host]:port
    public static string ToHostString(string host, int port)
    {
        string normalized = host.Trim().ToLowerInvariant();
        return port == DefaultPort ? normalized : $"[{normalized}]:{port}";
    }

    public static bool Matches(KnownHostEntry entry, string host, int port)
    {
        if (!entry.IsPattern)
        {
            return entry.Port == port && string.Equals(entry.Host, host.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return MatchesPattern(entry.Host, ToHostString(host, port));
    }

    // 模式串：单个哈希项，或逗号分隔的通配符列表（任一正向命中且无否定项命中）
    public static bool MatchesPattern(string pattern, string hostString)
    {
        if (pattern.StartsWith("|1|", StringComparison.Ordinal))
        {
            return MatchesHashed(pattern, hostString);
        }

        bool positive = false;
        foreach (string raw in pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool negated = raw.StartsWith('!');
            string glob = negated ? raw[1..] : raw;
            if (!GlobMatches(glob.ToLowerInvariant(), hostString))
            {
                continue;
            }

            if (negated)
            {
                return false;
            }

            positive = true;
        }

        return positive;
    }

    // |1|base64(salt)|base64(HMAC-SHA1(salt, hostString))
    private static bool MatchesHashed(string pattern, string hostString)
    {
        string[] parts = pattern.Split('|');
        if (parts.Length != 4)
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(hostString));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // OpenSSH match_pattern：* 匹配任意串，? 匹配单字符
    private static bool GlobMatches(string glob, string text)
    {
        int g = 0;
        int t = 0;
        int starG = -1;
        int starT = 0;
        while (t < text.Length)
        {
            if (g < glob.Length && (glob[g] == '?' || glob[g] == text[t]))
            {
                g++;
                t++;
            }
            else if (g < glob.Length && glob[g] == '*')
            {
                starG = g++;
                starT = t;
            }
            else if (starG >= 0)
            {
                g = starG + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }

        while (g < glob.Length && glob[g] == '*')
        {
            g++;
        }

        return g == glob.Length;
    }
}
