namespace Kei.Term.Core.Security;

// SSH 层在握手前后调用的主机密钥校验契约（HostKeyTrustService 为唯一生产实现）
public interface IHostKeyVerifier
{
    // 该端点已信任的密钥算法：握手前据此调整算法协商顺序
    Task<IReadOnlyList<string>> GetKnownKeyTypesAsync(string host, int port, CancellationToken ct = default);

    // 握手回调内调用：只查库与策略裁决，不得弹窗
    Task<HostKeyCheckOutcome> VerifyAsync(PresentedHostKey presented, CancellationToken ct = default);
}

// 主机密钥算法协商偏好（对齐 OpenSSH：已记录某类密钥时优先协商该类算法）。
// 否则服务器同时持有 ed25519 与 rsa 密钥时，客户端默认顺序可能协商到未记录的那一把，
// 每次都被误判为"未知主机"，还会在 Strict 策略下被拒绝。
public static class HostKeyAlgorithmPreference
{
    // 签名算法 → 密钥类型：rsa-sha2-* 均使用 ssh-rsa 密钥；证书算法保持原样（与普通密钥不互认）
    public static string KeyTypeOf(string algorithm) => algorithm switch
    {
        "rsa-sha2-256" or "rsa-sha2-512" => "ssh-rsa",
        _ => algorithm
    };

    // 稳定分区：已知密钥类型对应的算法提前，其余保持原相对顺序
    public static IReadOnlyList<string> Order(IReadOnlyList<string> algorithms, IReadOnlyCollection<string> knownKeyTypes)
    {
        if (knownKeyTypes.Count == 0)
        {
            return algorithms;
        }

        List<string> preferred = algorithms.Where(a => knownKeyTypes.Contains(KeyTypeOf(a))).ToList();
        return preferred.Concat(algorithms.Where(a => !preferred.Contains(a))).ToList();
    }
}
