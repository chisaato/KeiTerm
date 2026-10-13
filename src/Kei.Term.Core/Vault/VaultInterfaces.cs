namespace Kei.Term.Core.Vault;

// 密钥材料：仅内存流转，禁止明文落库/日志
public class SecretPayload
{
    public string? Password { get; set; }
    public string? PrivateKeyContent { get; set; }
    public string? Passphrase { get; set; }
}

public interface IVaultManager
{
    bool IsUnlocked { get; }

    // 无主密码（明文）模式：IsUnlocked 恒真，加密操作直通字节
    bool IsPlainMode { get; }

    // 设置/更新主密码：派生 MEK 并写入 salt/verifier，随后 Vault 处于已解锁态
    Task SetMasterPasswordAsync(string masterPassword, CancellationToken ct = default);

    // 去掉主密码：解密全部材料后以明文落库，并删除本机解锁登记。
    Task RemoveMasterPasswordAsync(CancellationToken ct = default);

    Task<bool> TryAutoUnlockAsync(CancellationToken ct = default);

    Task UnlockAsync(string masterPassword, bool rememberOnThisDevice, CancellationToken ct = default);

    // 锁定 = 清内存 MEK（SessionOnly 口令缓存由上层一并清空）
    void Lock();
}

// Vault 级材料库：条目可命名、可复用，身份方法仅持有 SecretId 引用。
// 登录/锁定语义：ListAsync 只读元数据、无需解锁；GetPayloadAsync 需解锁；
// SaveAsync 的 payload=null 表示只改元数据（名称/指纹等）而不重写已加密载荷；
// DeleteAsync 不检查引用，由上层服务先校验后再调用。
public interface IVaultSecretLibrary
{
    Task<IReadOnlyList<VaultSecret>> ListAsync(CancellationToken ct = default);
    Task<SecretPayload?> GetPayloadAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(VaultSecret secret, SecretPayload? payload, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
