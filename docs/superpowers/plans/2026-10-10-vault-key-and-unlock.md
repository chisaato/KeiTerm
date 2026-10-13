# 保管库 Vault Key 与本机解锁 Implementation Plan

> **For agentic workers:** 按 deepwork 阶段执行。阶段 1 是快进已有提交；阶段 2 起才改密钥语义。用户要求开工，不按阶段做 git commit。

**Goal:** 主密码只包装随机 Vault Key，新库用 Argon2id，旧 PBKDF2 库仍能打开，并在此之上保留本机便捷解锁。

**Architecture:** 数据密钥是随机 32 字节 Vault Key。主密码经 Argon2id 得到包装密钥，只用它做 XChaCha20-Poly1305 包装，不加密业务行。条目密文用 Vault Key 和绑定 id 的 AAD。快速解锁保存 Vault Key，不保存主密码。macOS/Windows 沿用 `origin/codex/vault-quick-unlock`。Linux 用 Secret Service。

**Tech Stack:** .NET 10、SQLite、`NSec.Cryptography` 26.4.0（自带 libsodium 1.0.22）、现有 AES-GCM 只留给旧库。

**Spec:** 对话中已确认的范围，外加 `docs/design/14-vault-quick-unlock.md`（阶段 1 引入）。不实现 `~/Downloads/keiterm-vault-crypto-design.md` 的整行加密、SQLCipher、Secret Key、X25519 和自定义同步容器。

## Global Constraints

- 新 KDF 标识：`argon2id:m=65536,t=3,p=1`。NSec `Argon2Parameters`：`MemorySize = 65536`（KiB）、`NumberOfPasses = 3`、`DegreeOfParallelism = 1`（NSec 明确拒绝其他并行度）。
- 盐 16 字节。主密码只在 Argon2id 路径做 Unicode NFKC（`NormalizationForm.FormKC`）再 UTF-8。禁止用 NSec 的 `string` 重载（那是 UTF-16）。派生后清零口令字节。
- 旧标识 `pbkdf2-sha512:600000` 继续按现有 60 万次 HMAC-SHA512 解锁，不对旧口令做 NFKC。
- 算法标识：旧 `AES-256-GCM` 块格式不变；新条目标 `XCHACHA20-POLY1305`。
- 包装 AAD：UTF-8 `keiterm/vk/v1` 后接 16 字节 `vault_id`。条目 AAD：UTF-8 `keiterm/item/v1` 后接该行主键的 Guid 字节（identity 或 proxy）。
- 换主密码默认不改条目密文。先按旧 key id 删除快速解锁项。删除失败则生成新 Vault Key、重加密全部秘密行，使遗留钥匙串副本失效。
- 不改已发布的 schema 迁移。同步预留是新的 v7。不把删除改成软删除。
- 不把便捷解锁写成 Secure Enclave / TPM / 生物信息绑定。
- 工作区 `tests/Kei.Term.Tests/Kei.Term.Tests.csproj` 有未提交的测试包升级，不要还原。

## Review Focus

- 旧 PBKDF2 库用原口令仍能打开，且打开后变成 Vault Key + Argon2id，条目不丢。
- 换主密码后条目密文字节不变，新口令可解，旧口令不可。
- 快速解锁里留下的旧 Vault Key，在换密码且钥匙串删除失败之后，不能再解开当前库。
- 对调两行密文或改 AAD 所绑的 id，新格式解密失败。
- 明文模式行为不变：不弹主密码，不登记快速解锁。

---

### Task 1: 快进快速解锁

**Files:** 已在 `84b0f23`，快进即可。不要重写平台后端。

- [ ] `git merge --ff-only origin/codex/vault-quick-unlock`
- [ ] 跑保管库与快速解锁相关测试
- [ ] Oracle 只审：这份实现是否如 `docs/design/14-vault-quick-unlock.md` 所说，保存 32 字节密钥而不是主密码，以及阶段 2 把这份密钥从 MEK 换成 Vault Key 时哪些假设会失效

### Task 2: Vault Key 与 Argon2id

**Files:**
- Modify: `src/Kei.Term.Infrastructure/Kei.Term.Infrastructure.csproj` 添加 `NSec.Cryptography` 26.4.0
- Create: `src/Kei.Term.Infrastructure/Vault/VaultCryptography.cs`
- Modify: `src/Kei.Term.Infrastructure/Vault/InternalVaultManager.cs`
- Modify: `src/Kei.Term.Infrastructure/Vault/InternalVaultManager.QuickUnlock.cs`
- Modify: `tests/Kei.Term.Tests/VaultTests.cs`、`VaultQuickUnlockTests.cs`、`ProxyFirewallTests.cs` 中换密码断言

**Interfaces:**
- Consumes: `IQuickUnlockVault` 现有签名不变
- Produces: 内存中的数据密钥是 Vault Key。`PrepareQuickUnlockAsync` 交出的 32 字节是 Vault Key。`CurrentQuickUnlockKeyId()` 仍由 KDF 盐的 SHA-256 构成，换密码换盐所以 key id 变。

- [ ] 新库或换密码：生成或保留 Vault Key；`PasswordBasedKeyDerivationAlgorithm.Argon2id(parameters).DeriveBytes(utf8, salt, 32)`；用 `AeadAlgorithm.XChaCha20Poly1305` 包装。元数据键：`vault_id`、`wrapped_vault_key`、`vault_key_verifier`、`kdf`、`kdf_salt`。块布局 `nonce(24) || ciphertext||tag`。
- [ ] 解锁：见 `kdf` 为 Argon2id 则解包；见 `pbkdf2-sha512:600000` 且没有 `wrapped_vault_key` 则走旧 MEK，成功后立刻迁移（新盐、Argon2id 包装、条目换成带 AAD 的新算法、删除旧快速解锁登记）。迁移用的是用户刚输入的口令。
- [ ] 换密码成功路径不调用重加密条目。删除旧快速解锁失败时才轮换 Vault Key 并重加密，同时重写 verifier 并递增 lock version。
- [ ] 启用快速解锁时，若写入发生在换密码之后、回滚删除又失败，不能只记日志。`IQuickUnlockVault` 增加 `DiscardDeviceKeyCopyAsync(string keyId, ReadOnlyMemory<byte> key, CancellationToken ct)`。删除失败且交出的字节仍等于当前 Vault Key 时轮换它。轮换需要包装密钥：口令解锁后把 Argon2id 输出留在内存里，跟 Vault Key 一起在 `Lock` 时清零。没有包装密钥时记下轮换标记、锁定保管库，下次口令解锁先轮换再投入使用。
- [ ] 改掉 `docs/design/14-vault-quick-unlock.md` 里「新盐让旧设备项失效」的句子。key id 仍是 `keiterm-v1-` + SHA-256(盐)，它只决定查找哪一项。
- [ ] 未知 `kdf` 仍抛 `NotSupportedException`，不得报成密码错误。
- [ ] 测试覆盖 Review Focus 前四条。Argon2id 测试可以把参数降到库接受的最小内存，但生产常量必须是 65536/3/1；用内部可见的参数对象，不要在测试里复制第二套生产参数。

### Task 3: Linux 钥匙环与安全页

**Files:**
- Create: `src/Kei.Term.Infrastructure/Vault/Linux/LinuxSecretServiceQuickUnlockStore.cs`
- Modify: 快速解锁的平台选择处，Linux 走该 store；库不存在则 `IsSupported = false`
- Create: `src/Kei.Term.App/ViewModels/Settings/SecuritySettingsPage.cs`
- Modify: `SettingsViewModel.cs`、`SettingsWindow.axaml`、`SshSettingsPage`、字符串资源
- 设计与 XAML：@designer。Linux store：@fixer。写范围不要交叉。

**行为:**
- Linux 只实现 `IDeviceQuickUnlockStore`。条目进默认登录钥匙环，属性含 `KeiTerm` 与 key id。不调用指纹。测试不得写入用户真实钥匙环。
- 安全页放在 SSH 页之前，接收：保管库是明文还是已设主密码、设置/更改主密码、锁定超时、本机快速解锁。锁定超时和快速解锁从 SSH 页移走，不留重复控件。
- 文案写明这是本机便捷解锁。macOS 不写 Touch ID 把密钥封在 Secure Enclave。Linux 写登录钥匙环。Windows 沿用现有 Hello 文案，不新增「密钥在 TPM 内」的说法。

### Task 4: 同步列预留

**Files:**
- Modify: `src/Kei.Term.Infrastructure/Storage/Schema/SchemaMigrations.cs` 只追加 v7
- Test: 迁移测试，断言 v1–v6 委托未改，新列存在且默认 `revision = 0`、`deleted_at` 为空

**列:** `revision INTEGER NOT NULL DEFAULT 0`、`deleted_at TEXT`，加到 `tree_nodes`、`identities`、`proxies`、`port_forwards`、`external_editors`、`file_associations`。不加到 `identity_secrets`、`proxy_secrets`、`vault_metadata`、`known_hosts`、`external_editor_paths`。仓储的删除仍是物理删除。
