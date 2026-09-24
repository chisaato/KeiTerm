namespace Kei.Term.Core.Vault;

using System.Text.Json.Serialization;

// 文件私钥口令三态
public enum PassphrasePersistence
{
    AlwaysAsk = 0,   // 每次连接询问
    SessionOnly = 1, // 本次运行内存缓存（锁定/退出清空）
    Persistent = 2   // 永久保存（入 Vault 密文，材料映射内按 methodId 键控）
}

// 认证方法基类：多态 JSON 以 $kind 判别；未知 $kind 反序列化直接抛错（防静默丢方法）
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind",
                 UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(VaultPasswordMethod), "ssh/vault-password")]
[JsonDerivedType(typeof(VaultPrivateKeyMethod), "ssh/vault-key")]
[JsonDerivedType(typeof(FilePrivateKeyMethod), "ssh/file-key")]
[JsonDerivedType(typeof(AgentMethod), "ssh/agent")]
[JsonDerivedType(typeof(InteractiveMethod), "ssh/interactive")]
public abstract class AuthMethodEntry
{
    // identity_secrets 材料映射（methodId -> SecretPayload）的键
    public Guid Id { get; set; } = Guid.NewGuid();
    public int SortOrder { get; set; }
    public bool Enabled { get; set; } = true;
}

// 密码，密文存 Vault
public sealed class VaultPasswordMethod : AuthMethodEntry { }

// 私钥密文托管
public sealed class VaultPrivateKeyMethod : AuthMethodEntry { }

// 文件引用（只存路径）
public sealed class FilePrivateKeyMethod : AuthMethodEntry
{
    public string? KeyFilePath { get; set; }
    public PassphrasePersistence PassphraseMode { get; set; } = PassphrasePersistence.AlwaysAsk;
}

// 走系统 Agent（可排进方法序列）
public sealed class AgentMethod : AuthMethodEntry
{
    // 可选；空 = 全部身份（指纹 UI 二期）
    public string? AgentFingerprint { get; set; }
}

// 每次连接弹窗，仅记用户名
public sealed class InteractiveMethod : AuthMethodEntry { }

// 命名隔离的身份档案：会话绑定身份，身份内部配置有序的多种认证方法
public class Identity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    // 预填用户名（Interactive/弹窗兜底时使用）
    public string? Username { get; set; }
    // 持久化为 methods_json（多态 JSON），数组序 = 尝试序
    public List<AuthMethodEntry> Methods { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
