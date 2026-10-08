namespace Kei.Term.Core.Vault;

// 代理口令。不进 config_json，也不进 ProxyProfile 的可序列化字段。
public interface IProxySecretStore
{
    Task<string?> GetPasswordAsync(Guid proxyId, CancellationToken ct = default);

    // null 或空白 = 删除已存口令
    Task SetPasswordAsync(Guid proxyId, string? password, CancellationToken ct = default);

    Task<bool> HasPasswordAsync(Guid proxyId, CancellationToken ct = default);
}
