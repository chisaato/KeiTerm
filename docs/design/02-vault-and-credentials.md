# 02 - 凭据管理与安全库 (Vault & Credentials) 设计

> ⚠️ 部分已被取代：单凭据模型（Credential/单类型）已演进为**多方法有序的 Identity（身份）模型**，
> 见 [06-identity-and-auth.md](./06-identity-and-auth.md)。本文的 Vault 加密流程
> （Argon2id / AES-256-GCM / blob 格式）与 OS Keyring 定位仍然有效并被 06 引用。

## 1. 核心诉求与设计原则
1. **解耦性**：凭据（Credential）与主机节点（SessionNode）独立。多个主机可复用同一凭据（如共用一套 SSH 密钥）。
2. **多 Key / 凭据类型覆盖**：
   - **密码 (Password)**：来自 Internal Vault（内置加密库）或 OS Keyring。
   - **密钥 (PrivateKey)**：支持三种来源形态：
     - *Vault 托管*：私钥明文加密存储在 Vault 中。
     - *文件引用 (File Path)*：仅保存本地文件路径（如 `~/.ssh/id_ed25519`），连接时即时读取。
     - *SSH-Agent 引用*：仅保存公钥指纹或注释，由 Agent 提供签名能力。
   - **SK 硬件密钥 (Security Key)**：**明确仅通过 SSH-Agent 支持**，由外部 Agent 与系统底层交互，应用内部不做与 Yubico/CTAP2 原生库的硬绑定，确保无跨平台驱动缺失问题。
   - **交互式 (Interactive)**：连接时临时弹出输入框。
   - **证书认证扩展点 (SSH Certificate)**：保留用于支持 ZTNA / CA 签名证书的元数据扩展字段（如 CA 验证、证书路径），一期留出接口，暂不深入实现。
3. **可扩展凭据库提供者 (Extensible Vault Provider)**：
   - 接口抽象解耦，不仅支持内置 SQLite 加密库和 OS Keyring，未来可无缝接入 **1Password / Bitwarden / OpenBao / HashiCorp Vault** 等外部密码管理器。
4. **设备环境不变时的“静默自动解锁”能力 (Remember Unlock / Auto-Unlock)**：
   - 用户可勾选“在此设备上记住解锁状态”。
   - 原理：用户输入主密码解锁后，系统将主密码派生出的解锁凭据（或主密码本身）利用当前 OS 的安全保护机制（Windows DPAPI / macOS Keychain / Linux Secret Service）密封存储；应用再次启动时优先尝试从系统安全区静默加载；若环境变动（如切换用户、换机、Keyring 访问受限或失败），则平滑降级提示用户手动输入主密码。

---

## 2. 领域模型结构 (Domain Model)

```csharp
namespace Kei.Term.Core.Vault;

public enum CredentialType
{
    Password = 0,
    PrivateKey = 1,
    AgentForward = 2,
    Interactive = 3 // 每次连接弹出提示输入密码/临时 Token
}

public class Credential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CredentialType Type { get; set; }
    
    // 公开元数据（无需加密存储，方便列表展示）
    public string? Username { get; set; }
    public string? KeyFingerprint { get; set; } // 针对私钥计算的 SHA256 指纹
    public string? PublicKeyOpenSsh { get; set; }

    // 存储后端配置标记
    public CredentialStorageBackend Backend { get; set; } = CredentialStorageBackend.InternalEncrypted;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum CredentialStorageBackend
{
    InternalEncrypted = 0, // 内置数据库密文存储
    OsKeyring = 1          // 系统安全库
}

/// <summary>
/// 敏感数据载荷（仅在内存中安全流转，绝不以明文写入磁盘日志）
/// </summary>
public class SecretPayload
{
    public string? Password { get; set; }
    public string? PrivateKeyContent { get; set; }
    public string? Passphrase { get; set; } // 私钥密码（如有）
}
```

---

## 3. 存储提供者抽象 (Storage Provider Architecture)

```csharp
public interface ICredentialSecretStore
{
    CredentialStorageBackend Backend { get; }
    
    /// <summary>
    /// 读取解密后的敏感数据
    /// </summary>
    Task<SecretPayload?> GetSecretAsync(Guid credentialId, CancellationToken ct = default);
    
    /// <summary>
    /// 加密并存储敏感数据
    /// </summary>
    Task SaveSecretAsync(Guid credentialId, SecretPayload secret, CancellationToken ct = default);
    
    /// <summary>
    /// 删除机密
    /// </summary>
    Task DeleteSecretAsync(Guid credentialId, CancellationToken ct = default);
}
```

### 3.1 内置安全存储实现流程 (InternalEncrypted)
1. **主密码派生**：
   - 算法：`Argon2id` (Salt: 16 字节随机数，Memory: 64MB, Iterations: 3, Parallelism: 4)。
   - 生成 32 字节 Master Encryption Key (MEK)。
2. **敏感字段加密**：
   - 算法：`AES-256-GCM`。
   - 格式：`[Salt: 16B] + [Nonce: 12B] + [Tag: 16B] + [Ciphertext: NB]`。
3. **状态管理**：
   - 应用启动时 Vault 处于 `Locked` 状态，提示用户输入主密码以换取会话内存中的解密凭证。
   - 允许用户配置“自动锁屏超时”（如闲置 15 分钟清空内存中的 MEK）。

### 3.2 OS Keyring 适配 (OsKeyring)
- 利用现有的成熟跨平台库或针对三大平台精简实现：
  - Windows: `Windows.Security.Credentials.PasswordVault` 或 DPAPI。
  - macOS: `Security.framework` (`SecItemAdd` / `SecItemCopyMatching`)。
  - Linux: 通过 DBus 访问 `org.freedesktop.secrets`。
- 优点：由系统授权机制接管（如 Touch ID、Windows Hello、Gnome Keyring），无需用户在 Kei.Term 内每次输入主密码。

---

## 4. SQLite 持久化表设计

```sql
-- 凭据元数据表（存储公开信息与类型）
CREATE TABLE IF NOT EXISTS credentials (
    id TEXT PRIMARY KEY NOT NULL,
    name TEXT NOT NULL,
    description TEXT,
    credential_type INTEGER NOT NULL,
    username TEXT,
    key_fingerprint TEXT,
    public_key_text TEXT,
    storage_backend INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

-- 内置密文存储表（仅当 storage_backend = 0 时在此表保存加密字节流）
CREATE TABLE IF NOT EXISTS credential_secrets (
    credential_id TEXT PRIMARY KEY NOT NULL,
    encrypted_blob BLOB NOT NULL,
    encryption_algorithm TEXT NOT NULL DEFAULT 'AES-256-GCM',
    nonce BLOB NOT NULL,
    tag BLOB NOT NULL,
    FOREIGN KEY(credential_id) REFERENCES credentials(id) ON DELETE CASCADE
);

-- 安全库全局配置（如 Salt、验证 Token 等）
CREATE TABLE IF NOT EXISTS vault_metadata (
    key TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
);
```
