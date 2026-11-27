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

    // 无主密码模式下 IsUnlocked 恒真，只允许持久化非私钥凭据。
    bool IsPlainMode { get; }

    // 设置/更新主密码：派生 MEK 并写入 salt/verifier，随后 Vault 处于已解锁态
    Task SetMasterPasswordAsync(string masterPassword, CancellationToken ct = default);

    // 去掉主密码前须移除托管私钥及同步恢复材料；成功后清除本机解锁登记。
    Task RemoveMasterPasswordAsync(CancellationToken ct = default);

    Task<bool> TryAutoUnlockAsync(CancellationToken ct = default);

    Task UnlockAsync(string masterPassword, bool rememberOnThisDevice, CancellationToken ct = default);

    // 锁定 = 清内存 MEK（SessionOnly 口令缓存由上层一并清空）
    void Lock();
}

// 身份级整包密钥材料存储：Dictionary<methodId, SecretPayload>
public interface IVaultSecretStore
{
    Task<Dictionary<string, SecretPayload>> GetSecretsAsync(Guid identityId, CancellationToken ct = default);
    Task SaveSecretsAsync(Guid identityId, Dictionary<string, SecretPayload> secrets, CancellationToken ct = default);
    Task DeleteSecretsAsync(Guid identityId, CancellationToken ct = default);
}
