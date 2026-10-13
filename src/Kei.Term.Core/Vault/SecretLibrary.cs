namespace Kei.Term.Core.Vault;

// 材料类型判别值。未知 Kind 读取时原样保留、不抛错，旧版本可容忍新增类型。
public static class SecretKind
{
    public const string Password = "password";
    public const string SshPrivateKey = "ssh-private-key";
    public const string Passphrase = "passphrase";
}

// 材料库条目：仅公开元数据，不含载荷（载荷经 IVaultSecretLibrary.GetPayloadAsync 按需解密）
public sealed record VaultSecret
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = SecretKind.Password;
    // 公钥/证书指纹（可选，明文存放用于未解锁时展示）
    public string? Fingerprint { get; init; }
    // 密钥算法类型，如 ssh-ed25519（可选）
    public string? KeyType { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

// 一条已加密载荷：与 identity_secrets / proxy_secrets 同一套加密约定（AAD 绑定条目 Id）
public sealed record VaultSecretEnvelope(byte[] Blob, string Algorithm, byte[]? Nonce, byte[]? Tag);

// 仓储读出的「元数据 + 密文封套」；解密由上层（VaultManager）在解锁后完成
public sealed record VaultSecretEncrypted(VaultSecret Secret, VaultSecretEnvelope Envelope);
