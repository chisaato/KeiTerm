namespace Kei.Term.Core.Sync;

// WebDAV 密码与 S3 Secret Key 的保险库入口。访问密钥 ID 不是秘密，留在本机同步位置配置里。
public interface ISyncLocationCredentialStore
{
    Task<string?> GetWebDavPasswordAsync(CancellationToken ct = default);
    Task SaveWebDavPasswordAsync(string password, CancellationToken ct = default);
    Task<string?> GetS3SecretKeyAsync(CancellationToken ct = default);
    Task SaveS3SecretKeyAsync(string secretKey, CancellationToken ct = default);
}
