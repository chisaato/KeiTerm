# Vault 材料库（Secret Library）设计

日期：2026-10-13　状态：已实现

## 1. 背景与目标

现状：`identity_secrets` 每个身份存一整包加密 blob，内容为 `Dictionary<methodId, SecretPayload>`（`InternalVaultManager.cs:451-501`）。同一把私钥用于多个身份（用户名不同、密钥相同）时只能重复导入，编辑器里只有「从硬盘导入私钥」。

目标：
- 把「认证方法链」与「密文材料」拆开：材料成为 Vault 级的独立条目，可命名、可复用；身份方法只持有对材料的引用（`SecretId`）。
- 不依赖硬盘路径记录，利于云同步与跨平台。
- 支持私钥、密码两种材料，并为未来新增凭据类型预留扩展点（`Kind` 判别）。
- 引用计数：被引用的材料不可删除。
- 提供独立「材料库」窗口，身份编辑器内改为选择已有材料。
- 从旧结构无损迁移，相同私钥自动合并。

非目标：云同步实现本身、证书类材料、`proxy_secrets` 合并、`FilePrivateKeyMethod` 的改造。

附带修复：`VaultPasswordMethod` 目前没有任何密码录入 UI，本设计一并补上。

## 2. 数据模型

### 2.1 Core（`Kei.Term.Core/Vault`）

- `SecretKind`：字符串常量 `password`、`ssh-private-key`、`passphrase`（文件私钥持久化口令，条目 Id = 方法 Id）；未知 Kind 读取时保留原样、不抛错，使旧版本可容忍新增类型。
- `VaultSecret`（公开元数据，不含载荷）：`Id`(Guid)、`Name`、`Kind`、`Fingerprint?`、`KeyType?`、`CreatedAt`、`UpdatedAt`。
- `SecretPayload` 沿用（`Password` / `PrivateKeyContent` / `Passphrase`），仅内存流转，禁止明文日志。
- `AuthMethodEntry` 的 `VaultPasswordMethod`、`VaultPrivateKeyMethod` 新增 `Guid? SecretId`。`null` 表示尚未绑定材料。
- `IVaultSecretLibrary`：
  - `ListAsync()`：返回元数据，不需要解锁。
  - `GetPayloadAsync(id)`：需要解锁。
  - `SaveAsync(VaultSecret, SecretPayload?)`：新建或更新（`null` 载荷表示只改元数据）。
  - `DeleteAsync(id)`：不检查引用，由上层服务先校验。
  - 引用统计在 Core 中以纯函数实现：`SecretReferences.Find(IEnumerable<Identity>, secretId)` 返回引用它的身份与方法。
- 旧 `IVaultSecretStore`（身份级整包）在迁移完成后移除。

### 2.2 持久化（Schema v8，仅追加迁移）

新表 `vault_secrets`：

| 列 | 说明 |
|---|---|
| `id` TEXT PK | 条目 Id |
| `name` TEXT | 显示名 |
| `kind` TEXT | `SecretKind` |
| `meta_json` TEXT | 指纹、密钥类型等公开元数据 |
| `secret_blob` BLOB / `encryption_algorithm` / `nonce` / `tag` | 与 `identity_secrets` 同一套加密约定；AAD 绑定条目 Id（`BuildItemAad`） |
| `created_at` / `updated_at` | `SqliteValue.FormatUtc` |
| `revision` / `deleted_at` | 预留同步列，口径与 v7 一致 |

- 元数据以明文存放，使未解锁时也能列出与选择。载荷仍逐条加密。
- 读取（`ListAsync` / `GetAsync`）不过滤 `deleted_at`：该列为预留同步列，仓储当前仍为物理删除。
- v8 只建新表，不删旧表。旧 `identity_secrets` 在运行时迁移校验通过后，由后续版本（v9）删除。
- 仓储使用 Dapper + 私有 `VaultSecretRow`，连接统一经 `SqliteConnectionFactory.OpenAsync`。

### 2.3 引用计数

不存计数器；每次需要时扫描所有身份的 `methods_json`（`SecretReferences.Find`）。避免与同步或迁移脱节。
- 删除材料：存在引用则拒绝，并返回引用它的身份名称列表。
- 移除某身份的方法 / 删除身份：材料保留在库中，不自动删除；用户在材料库窗口显式清理（可筛选「无引用」）。

## 3. 迁移

旧 blob 加密且 AAD 绑定 identityId，不能在 SQL 迁移中拆分，因此：

- `LegacySecretMigrator`（Infrastructure）在 Vault 可读后运行一次：明文模式在启动时；有主密码时挂在解锁成功事件（含快速解锁、自动解锁路径）之后。
- 流程（单一事务）：
  1. 读取所有 `identity_secrets`，解密为 `Dictionary<methodId, SecretPayload>`。
  2. 每个 methodId 对应一个条目，**条目 Id = 旧 methodId**，使 `methods_json` 中方法 `Id` 与 `SecretId` 一致，几乎不需改 JSON；方法的 `SecretId` 回填为其 `Id`。
  3. 私钥内容（含口令）完全相同的条目合并为一个，被合并方法的 `SecretId` 指向保留条目。
  4. 名称：`"{身份名} · 私钥"` / `"{身份名} · 密码"`；合并后沿用第一个。
  5. 写入新表、更新 `methods_json`、在 `vault_metadata` 写入迁移完成标记。
- 幂等：已有标记则跳过；事务失败整体回滚，旧数据不动。
- 未迁移期间，读取路径回退到旧表（仅限只读）；迁移完成后旧表保留，v9 再清理。
- 主密码设置 / 移除 / 密钥轮换：`ReEncryptAllSecretsAsync` 与 `WritePlaintextAsync` 需把 `vault_secrets` 并入处理。

## 4. 消费路径

- `AuthMaterializer`：`GetVaultSecret` 改按 `SecretId` 取载荷；`SaveVaultSecretAsync`（口令持久化回写）改为写回对应条目。
- `AuthMaterialCollector`：`LoadIdentitySecretsAsync` 改为按需读取所引用的条目；`BuildVaultKeyOptions` 改为当前身份引用的私钥材料。
- `VaultSessionService`：`GetVaultKeyInfoAsync`、`PersistVaultKeyImportsAsync` 改为面向条目的 API（创建、重命名、读取元数据）。
- `SecureCrtImporter` 行为不变（创建空方法的身份）。

## 5. UI

遵循 `docs/design/10-refactor-plan.md`：新增弹窗一律走 `IInteractionService`，不再往 ViewModel 加 `Func<>` 属性。

- **材料库窗口**（新增，独立 ViewModel）：列表列为名称、类型、指纹、引用数；操作为导入私钥、新建密码、重命名、删除；删除有引用时提示引用它的身份；支持筛选「无引用」。
- **身份编辑器**：`VaultPrivateKeyMethod` / `VaultPasswordMethod` 方法行改为「材料下拉 + 新建/导入…」；选中后显示指纹与引用数，替换原有「私钥材料：未导入」。原有的暂存导入流程改为导入时直接创建条目，方法只记 `SecretId`。
- **AuthPromptWindow**：`VaultKeyOption` 改为携带 `SecretId` 与显示名。
- 视觉与布局细节交 @designer 实现，保持现有暗色紧凑设计 token。

## 6. 测试

按 AGENTS.md：测试必须能因生产行为出错而失败。

- 迁移：拆分正确、相同私钥合并、幂等、中途失败回滚、未解锁不迁移。
- 加解密：AAD 绑定条目 Id；设置 / 移除主密码、轮换后材料仍可读。
- 引用：有引用拒绝删除；移除方法后条目保留。
- 物化 / 连接：共享同一私钥的两个身份都能物化出相同材料。
- UI：headless Avalonia 覆盖编辑器的选择与新建流程；材料库窗口的删除保护。
- 散落的 `PlainVault` 假实现需同步更新。

## 7. 实施分工建议（供写计划时参考）

正交方向可并行：
1. Core 模型 + 引用统计 + 接口，v8 Schema 与仓储。
2. `InternalVaultManager` 实现、`LegacySecretMigrator`、重加密适配。
3. 消费路径改造（Materializer / Collector / SessionService）。
4. UI（材料库窗口、编辑器改造、AuthPrompt），由 @designer 负责。
5. 测试与旧 `PlainVault` 假实现更新。

依赖关系：1 → 2 → 3，4 依赖 1 的接口，5 贯穿其后。

## 8. 风险

- 迁移是运行时而非 SQL 迁移，需覆盖所有解锁入口；需要在设计的实现计划中列出解锁事件的具体挂载点。
- 合并相同私钥会改变「按身份隔离」的语义，必须在迁移日志中记录（不记录私钥内容）。
- 旧表在 v9 前长期保留明文之外的同级加密数据，属已有风险，无新增暴露。
