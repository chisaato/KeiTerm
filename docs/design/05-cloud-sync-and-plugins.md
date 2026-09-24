# 05 - E2EE 云同步与插件化扩展设计 (Cloud Sync & Plugins)

## 1. 核心诉求与设计定位
1. **端到端加密（E2EE, End-to-End Encryption）**：
   - 保证“零知识”（Zero-Knowledge）。云端存储介质只接收完全加密的密文包（Encrypted Payload Blob），无论传输通道或云服务商是否可信，数据都不会发生泄露。
   - 用户使用同步密码（Sync Passphrase）在本地派生加密密钥，明文数据绝不离机。
2. **抽象化与插件化架构（Plugin-Friendly）**：
   - 同步核心（加密、打包人、差异对比、合并冲突）与同步存储介质（WebDAV、S3、GitHub Gist、自建 API、本地目录）彻底解耦。
   - 第三方或未来扩展只需实现单一只读/读写接口（`ISyncStorageProvider`），即可作为插件接入。
3. **数据同步范围与安全性控制**：
   - **默认同步内容**：目录树结构（Folder / Session）、级联配置、颜色偏好、标签、凭据公开元数据（用户名、公钥指纹、Key 别名）。
   - **私钥与敏感密码的同步策略（用户可选）**：
     - **模式 A（默认安全）**：仅同步主机树与凭据元数据，私钥与密码保留在各设备本地 Keyring/Vault，通过同一别名跨端关联。
     - **模式 B（全量便携）**：私钥与密码在本地经二次加密后一并打入密文 Payload，用户在新设备输入主密码完成完全恢复。

---

## 2. E2EE 加密规范：参考/对齐 AGE (Actually Good Encryption) 规范

为了遵循“使用被广泛验证的现有密码学设计”原则，同步容器的加密规范全面参考 **AGE 加密规范 (RFC / age-encryption.org)** 设计：

### 2.1 核心密码学套件 (Cryptographic Primitives)
AGE 标准以简洁、杜绝降级攻击、现代算法为核心特征：
- **KDF（基于密码加密场景）**：采用 `scrypt`（18 轮参数：`N=2^18, r=8, p=1`）或 `Argon2id`，安全抵御 GPU/ASIC 离线彩虹表暴力破解。
- **Payload 载荷加密**：采用 `ChaCha20-Poly1305`（AEAD 流式加密，基于 AGE 规范标准），或 `AES-256-GCM`。
- **抗延展与分块完整性（CHUNK streaming）**：
  - 参考 AGE 规范，密文载荷按固定块大小（如 64KB 为一个 Chunk）进行分块加密。
  - 每个 Chunk 携带独立的自增 Nonce / Counter，最后一个 Chunk 使用特殊终止符。
  - 这种设计天然防止大文件传输被截断攻击，并在流式解压/解密时消耗最小内存。

### 2.2 密文封装格式 (AGE-Compatible Header 风格)

```
age-encryption.org/v1
-> scrypt <salt_base64> 18
<encrypted_file_key_base64>
--- <header_hmac_base64>
[64KB encrypted chunk 0 (ChaCha20-Poly1305)]
[64KB encrypted chunk 1 (ChaCha20-Poly1305)]
...
```

- **收益**：
  1. 完全消除“自研加密格式”的潜在逻辑漏洞。
  2. 未来可原生兼容外部 `age` 命令行工具，用户甚至可以用自己的 `age` 密钥文件直接加解密备份文件。
  3. 天然保证前后向安全性与密钥敏捷性。

---

## 3. 插件化存储抽象设计

定义精简、正交的同步存储契约：

```csharp
namespace Kei.Term.Core.Sync;

public interface ISyncStorageProvider
{
    /// <summary>
    /// 插件唯一标识（例如 "local-file", "webdav", "s3", "custom-api"）
    /// </summary>
    string ProviderId { get; }
    
    /// <summary>
    /// UI 显示名称
    /// </summary>
    string DisplayName { get; }
    
    /// <summary>
    /// 检查远程连接或配置是否有效
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
    
    /// <summary>
    /// 获取远程最新的同步元数据（版本、修改时间、文件哈希）
    /// </summary>
    Task<RemoteSyncMetadata?> GetRemoteMetadataAsync(CancellationToken ct = default);
    
    /// <summary>
    /// 拉取远程密文包
    /// </summary>
    Task<Stream> DownloadPayloadAsync(CancellationToken ct = default);
    
    /// <summary>
    /// 上传本地密文包
    /// </summary>
    Task UploadPayloadAsync(Stream payloadStream, CancellationToken ct = default);
}

public record RemoteSyncMetadata(
    string RemoteEtagOrHash,
    DateTime LastModifiedUtc,
    long PayloadSizeBytes
);
```

---

## 4. 同步协调引擎 (Sync Engine) 流程

```
[ 本地触发同步 ]
       |
       v
+-----------------------------+
| 1. 获取本地数据快照         | -> 导出目录树、配置、选中的凭据
+--------------+--------------+
               |
               v
+-----------------------------+
| 2. GZip 压缩 + AES-GCM 加密 | -> 生成包含 Salt / Nonce / Tag 的 Payload
+--------------+--------------+
               |
               v
+-----------------------------+
| 3. 调用 ISyncStorageProvider| -> 对比远程与本地版本
+--------------+--------------+
               |
       +-------+-------+
       |               |
       v (本地更新)    v (远程更新)
  [ 上传 Payload ]   [ 下载 Payload -> 解密 -> 差异合并 (Merge) -> 写回 SQLite ]
```

### 4.1 冲突解决策略 (Conflict Resolution)
- **节点级基于时间戳自动合并（Last-Write-Wins by Entity）**：
  - 每个节点独立记录 `UpdatedAt`。
  - 若远程节点更新，覆盖本地对应节点；若本地新增节点，追加保留；避免粗暴地全库覆盖。
- **删除冲突**：通过软删除标记（`IsDeleted` + `DeletedAt`），确保在同步周期内删除操作能正确广播到多端。
