# Vault 材料库（Secret Library）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把「身份认证方法链」与「密文材料」拆开，使私钥/密码成为 Vault 级可复用条目，身份方法只持有 `SecretId` 引用，并从旧 `identity_secrets` 整包结构无损迁移。

**Architecture:** Core 新增 `VaultSecret` 元数据模型、`SecretKind` 判别常量、`IVaultSecretLibrary` 接口与纯函数 `SecretReferences`/`LegacySecretMigration`。Infrastructure 新建 Schema v8 `vault_secrets` 表、Dapper 仓储 `SqliteVaultSecretRepository`，`InternalVaultManager` 实现材料库并把新表并入重加密/主密码/轮换流程，`LegacySecretMigrator` 在全部解锁入口后一次性迁移。App 层消费路径改按 `SecretId` 取载荷，新增材料库窗口（`IInteractionService` 接入）、改造身份编辑器。

**Tech Stack:** .NET 10 (C# 13) · Avalonia 12.x · CommunityToolkit.Mvvm · Dapper + Microsoft.Data.Sqlite · NSec XChaCha20-Poly1305 · xUnit + Avalonia.Headless。

**Spec:** `docs/superpowers/specs/2026-10-13-vault-secret-library-design.md`

## Global Constraints

- `Kei.Term.Core` 不得引用 Avalonia 或平台 P/Invoke；材料仅在内存流转，禁止明文日志或未加密持久化（新增代码一律遵守）。
- 所有 SQLite 连接必须经 `SqliteConnectionFactory.OpenAsync`（统一 `PRAGMA foreign_keys = ON`）；仓储不得自行 `new SqliteConnection`。
- Schema 变更只能向 `Storage/Schema/SchemaMigrations.cs` 追加版本号 +1 的迁移（本次 v8）；已发布迁移永不修改；仓储内不得 `CREATE TABLE`。
- 仓储用 Dapper + 私有 `XxxRow` 行对象；时间列用 `SqliteValue.FormatUtc/ParseUtc`。
- `SecretKind` 常量值固定为 `password`、`ssh-private-key`；未知 Kind 读取时保留原样、不抛错。
- `vault_secrets` 载荷加密沿用 `VaultCryptography`：AAD 绑定条目 Id（`BuildItemAad`），明文模式写 `PLAIN`。
- v8 只建新表，不删 `identity_secrets`（v9 再清理）。
- 新增弹窗一律走 `IInteractionService`，不再往 ViewModel 加 `Func<>` 弹窗属性（可注入「数据/行为」委托，但弹窗必须经交互服务）。
- 测试必须能因生产行为出错而失败：不写只测 Mock、按源码文本/调用顺序断言、回显常量或默认值的测试（XAML 契约测试除外）。
- 行内注释优先，少用行后注释；`var` 规范见 `AGENTS.md`（简短调用/长链式允许，多个近乎相同的声明堆叠禁用）。
- 用户约定：任务合并相近步骤；TDD 红绿只在各 Task 内；整体回归放在最后；**全部完成后只做一次 commit**（计划中 commit 仅出现在 Task 7）。

## Review Focus

以下五类输入/条件，spec 隐含但未必逐条写明，最可能在使用中出问题；每一条都在对应 Task 中由专门测试钉死：

1. 加密 Vault **未解锁**时打开材料库窗口：列表必须仍能显示（元数据明文），不得抛锁异常、不得弹解锁框。
2. 删除**被引用**的材料（含文件私钥口令条目）：必须拒绝并报出引用它的身份名，不得静默删除导致后续连接失败。
3. 迁移在解锁失败/进程中断/中途异常时：旧 `identity_secrets` 与元数据必须完好，解锁本身不得因此失败，未迁移期间读取回退旧表。
4. 两个身份**共享同一私钥**：迁移后合并为单一条目，两个身份都能物化出相同材料；编辑其一不影响另一。
5. **明文模式**（无主密码）：创建/读取材料按 `PLAIN` 落库并往返一致，且启动时自动完成迁移。

## 决策与假定（spec 未明确，需评审确认）

- **文件私钥持久化口令的归宿**：spec 非目标排除 `FilePrivateKeyMethod` 改造，但又要移除 `IVaultSecretStore`（`FilePrivateKeyMethod.Persistent` 口令目前存于旧整包）。本计划选择：为口令新增 `SecretKind.Passphrase = "passphrase"`，把 `FilePrivateKeyMethod` 的持久化口令作为 `vault_secrets` 条目存储，**条目 Id = 方法 Id（隐式，不改 `FilePrivateKeyMethod` 字段）**。这样行为不变、无需新字段。若评审倾向其它方案（如旧表保留口令），需在 Task 1 前调整。
- **编辑器不再显示私钥字节数**：材料库元数据明文不含载荷，未解锁也要能回显，因此身份编辑器方法行由「已导入 · N 字节」改为显示所选材料**名称 + 指纹 + 引用数**（`VaultKeyInfo.Size` 语义废弃）。视觉由 @designer 定。
- **迁移失败不阻断解锁**：`TryMigrateLegacySecretsAsync` 捕获异常记警告、保持标记为空、解锁照常返回；读取路径回退旧表。
- **`ListAsync` 无旧表回退**：仅 `GetPayloadAsync` 回退旧 `identity_secrets`；列表在解锁后（迁移已跑）才被 UI 使用，此取舍记入计划。

---

### Task 1: Core 模型与接口 + v8 Schema + 仓储

**Files:**
- Create: `src/Kei.Term.Core/Vault/SecretLibrary.cs`
- Create: `src/Kei.Term.Core/Vault/SecretReferences.cs`
- Create: `src/Kei.Term.Infrastructure/Storage/SqliteVaultSecretRepository.cs`
- Modify: `src/Kei.Term.Core/Vault/IdentityModel.cs:30-33`（两个 Vault 方法类）
- Modify: `src/Kei.Term.Core/Vault/VaultInterfaces.cs`（新增 `IVaultSecretLibrary`；本 Task 暂**不**删 `IVaultSecretStore`）
- Modify: `src/Kei.Term.Infrastructure/Storage/Schema/SchemaMigrations.cs:12-21`（追加 v8）
- Test: `tests/Kei.Term.Tests/SecretReferencesTests.cs`（新建）· `tests/Kei.Term.Tests/SchemaMigratorTests.cs:150-153,215`（更新）· `tests/Kei.Term.Tests/IdentityJsonTests.cs`（补 SecretId 往返）

**Interfaces:**
- Consumes: 现有 `SecretPayload`（`VaultInterfaces.cs:4`）、`Identity`/`AuthMethodEntry`（`IdentityModel.cs`）、`SqliteConnectionFactory`/`SqliteValue`、`SchemaMigrations.All`。
- Produces:
  - `public static class SecretKind { public const string Password = "password"; public const string SshPrivateKey = "ssh-private-key"; public const string Passphrase = "passphrase"; }`
  - `public sealed record VaultSecret { Guid Id {get;init;} string Name {get;init;} string Kind {get;init;} string? Fingerprint {get;init;} string? KeyType {get;init;} DateTime CreatedAt {get;init;} DateTime UpdatedAt {get;init;} }`
  - `public interface IVaultSecretLibrary { Task<IReadOnlyList<VaultSecret>> ListAsync(CancellationToken ct = default); Task<SecretPayload?> GetPayloadAsync(Guid id, CancellationToken ct = default); Task SaveAsync(VaultSecret secret, SecretPayload? payload, CancellationToken ct = default); Task DeleteAsync(Guid id, CancellationToken ct = default); }`
  - `public readonly record struct SecretReference(Guid IdentityId, string IdentityName, Guid MethodId);`
  - `public static class SecretReferences { IReadOnlyList<SecretReference> Find(IEnumerable<Identity> identities, Guid secretId); Guid? SecretIdOf(AuthMethodEntry method); }`
  - `VaultPasswordMethod.SecretId` / `VaultPrivateKeyMethod.SecretId`（`Guid?`）
  - `public sealed record VaultSecretEnvelope(byte[] Blob, string Algorithm, byte[]? Nonce, byte[]? Tag); public sealed record VaultSecretEncrypted(VaultSecret Secret, VaultSecretEnvelope Envelope);`
  - `public sealed class SqliteVaultSecretRepository { ctor(SqliteConnectionFactory); ListAsync; GetAsync(Guid)->VaultSecretEncrypted?; UpsertAsync(VaultSecret, VaultSecretEnvelope); DeleteAsync(Guid); }`
  - Schema v8 版本号 = 8，名 `vault_secrets`。

- [ ] **Step 1: 写失败测试（纯函数 + JSON + 版本号）**

`SecretReferencesTests.cs`：
```csharp
// 引用统计：Vault 方法按 SecretId；文件私钥持久化口令按方法 Id；其余不计
[Fact] public void Find_CountsVaultMethodsBySecretId_AndFileKeyByMethodId()
{
    Guid keyEntry = Guid.NewGuid();
    Guid pwEntry = Guid.NewGuid();
    Guid keyMethodId = Guid.NewGuid();
    var a = new Identity { Id = Guid.NewGuid(), Name = "A", Methods =
    [
        new VaultPrivateKeyMethod { Id = Guid.NewGuid(), SecretId = keyEntry },
        new VaultPasswordMethod { Id = Guid.NewGuid(), SecretId = pwEntry },
        new FilePrivateKeyMethod { Id = keyMethodId, PassphraseMode = PassphrasePersistence.Persistent },
        new FilePrivateKeyMethod { Id = Guid.NewGuid(), PassphraseMode = PassphrasePersistence.AlwaysAsk },
    ]};
    Assert.Equal(new[]{"A"}, SecretReferences.Find([a], keyEntry).Select(r => r.IdentityName));
    Assert.Single(SecretReferences.Find([a], pwEntry));
    Assert.Single(SecretReferences.Find([a], keyMethodId)); // 文件私钥持久化口令条目 Id = 方法 Id
    Assert.Empty(SecretReferences.Find([a], Guid.NewGuid()));
}
[Fact] public void SecretIdOf_VaultMethodsUseSecretId_FileKeyOnlyWhenPersistent() { /* 断言四类方法的返回值 */ }
```
`IdentityJsonTests.cs` 追加：
```csharp
[Fact] public void RoundTrip_PreservesVaultMethodSecretId()
{
    var m = new VaultPrivateKeyMethod { SecretId = Guid.NewGuid() };
    var back = JsonSerializer.Deserialize<List<AuthMethodEntry>>(JsonSerializer.Serialize<List<AuthMethodEntry>>([m]));
    Assert.Equal(m.SecretId, Assert.IsType<VaultPrivateKeyMethod>(back![0]).SecretId);
}
```
`SchemaMigratorTests.cs` 改 `MigrationList_...` 的末尾断言为 `Assert.Equal(8, ...All[^1].Version)`、`Assert.Equal("vault_secrets", ...All[^1].Name)`；`UpgradeFromV6...` 的 `Assert.Equal(7, version)` 改为 `8`，并在 `excludedTables` 语义下断言 `vault_secrets` 不含 `revision/deleted_at` 之外的迁移被误改（v8 自身有这两列，故从 excluded 移除并单列断言）。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~SecretReferencesTests|FullyQualifiedName~SchemaMigratorTests"`
Expected: 编译失败（`SecretReferences`/`SecretKind` 未定义）或断言失败（版本号仍为 7）。

- [ ] **Step 3: 实现 Core 模型与接口**

`SecretLibrary.cs`：`SecretKind`、`VaultSecret`（如上）。`IdentityModel.cs` 给 `VaultPasswordMethod`/`VaultPrivateKeyMethod` 各加 `public Guid? SecretId { get; set; }`（行内注释：材料库条目引用，null = 未绑定）。`VaultInterfaces.cs` 追加 `IVaultSecretLibrary`（含行内注释说明 `ListAsync` 不解锁、`GetPayloadAsync` 需解锁、`SaveAsync` 的 `payload=null` 只改元数据、`DeleteAsync` 不检查引用）。

`SecretReferences.cs`：`SecretIdOf` 用 `switch` 返回 `VaultPasswordMethod.SecretId`、`VaultPrivateKeyMethod.SecretId`、`FilePrivateKeyMethod{PassphraseMode: Persistent}` 的 `method.Id`，其余 `null`；`Find` 遍历身份/方法，命中给 `SecretReference`（`secretId == Guid.Empty` 直接返回空）。**纯函数，无 IO。**

- [ ] **Step 4: 实现 Schema v8 与仓储**

`SchemaMigrations.cs`：`All` 追加 `new(8, "vault_secrets", ApplyVaultSecretsAsync)`，并在文件内实现（行内注释：公开元数据明文，载荷逐条加密、AAD 绑定条目 Id，不改 v1–v7）：
```sql
CREATE TABLE IF NOT EXISTS vault_secrets (
  id TEXT PRIMARY KEY NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL,
  meta_json TEXT, secret_blob BLOB NOT NULL, encryption_algorithm TEXT NOT NULL,
  nonce BLOB, tag BLOB, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
  revision INTEGER NOT NULL DEFAULT 0, deleted_at TEXT);
```
`SqliteVaultSecretRepository.cs`：私有 `VaultSecretRow`（Dapper，`DefaultTypeMap.MatchNamesWithUnderscores` 已全局开启）；`meta_json` 存取 `{"fingerprint":..,"keyType":..}`（私有 DTO + `JsonSerializer`）；`ListAsync` 只读元数据列、构造 `VaultSecret`；`GetAsync` 读全行返回 `VaultSecretEncrypted`；`UpsertAsync` 用 `ON CONFLICT(id) DO UPDATE`，`created_at` 保留、`updated_at = SqliteValue.FormatUtc(DateTime.UtcNow)`；`DeleteAsync` 物理删除。

- [ ] **Step 5: 运行确认通过**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~SecretReferencesTests|FullyQualifiedName~SchemaMigratorTests|FullyQualifiedName~IdentityJsonTests"`
Expected: PASS。

---

### Task 2: InternalVaultManager 材料库 + LegacySecretMigrator + 全部解锁挂载点

**Files:**
- Create: `src/Kei.Term.Infrastructure/Vault/InternalVaultManager.SecretLibrary.cs`
- Create: `src/Kei.Term.Infrastructure/Vault/LegacySecretMigrator.cs`
- Create: `src/Kei.Term.Core/Vault/LegacySecretMigration.cs`（纯规划函数）
- Modify: `src/Kei.Term.Infrastructure/Vault/InternalVaultManager.cs:18`（加 `IVaultSecretLibrary`）· `:246-256`（`TryAutoUnlockAsync` 明文分支挂载）· `:261-365`（`UnlockCoreAsync` 两处出口挂载）· `:774-779`（`ReEncryptAllSecretsAsync` 并入 `vault_secrets`）· `:669-682`（`RemoveMasterPasswordCoreAsync` 并入 `vault_secrets`）
- Modify: `src/Kei.Term.Infrastructure/Vault/InternalVaultManager.QuickUnlock.cs:74-85`（成功后挂载）
- Test: `tests/Kei.Term.Tests/VaultSecretLibraryTests.cs`（新建）· `tests/Kei.Term.Tests/VaultSecretMigrationTests.cs`（新建）· `tests/Kei.Term.Tests/LegacySecretMigrationTests.cs`（新建，纯函数）

**Interfaces:**
- Consumes: Task 1 的 `IVaultSecretLibrary`/`VaultSecret`/`SecretKind`/`LegacySecretMigration`/`SqliteVaultSecretRepository`；`InternalVaultManager` 私有 `EncryptItem(Guid, byte[])`/`DecryptItem(Guid, byte[], string)`；`VaultCryptography`；现有 `_secretWriteLock`/`EnsureInitializedAsync`/`LoadValueAsync`/`UpsertMetadataAsync`。
- Produces:
  - `LegacySecretMigration.Plan(IEnumerable<Identity>, IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, SecretPayload>>) -> SecretMigrationPlan`；`sealed record SecretMigrationPlan(IReadOnlyList<MigratedSecret> Secrets, IReadOnlyList<MethodSecretBinding> Bindings)`；`sealed record MigratedSecret(Guid Id, string Kind, string Name, SecretPayload Payload)`；`sealed record MethodSecretBinding(Guid IdentityId, Guid MethodId, Guid SecretId)`。
  - `internal sealed class LegacySecretMigrator { LegacySecretMigrator(SqliteConnectionFactory); Task<bool> RunAsync(Func<Guid,byte[],string,byte[]> decryptItem, Func<Guid,byte[],(byte[] Blob,string Algorithm,byte[]? Nonce,byte[]? Tag)> encryptItem, ILogger logger, CancellationToken ct = default); }`
  - `InternalVaultManager` 私有 `TryMigrateLegacySecretsAsync(CancellationToken)`；迁移标记键 `secret_library_migrated = "1"`。

- [ ] **Step 1: 写失败测试（纯规划：拆分/合并/命名）**

`LegacySecretMigrationTests.cs`：
```csharp
[Fact] public void Plan_SplitsMethodsIntoEntries_AndBackfillsSecretId()
{
    var idA = Guid.NewGuid(); var m1 = new VaultPrivateKeyMethod(); var m2 = new VaultPasswordMethod();
    var ident = new Identity { Id = idA, Name = "Prod", Methods = [m1, m2] };
    var secrets = new Dictionary<Guid, IReadOnlyDictionary<string, SecretPayload>>
    { [idA] = new Dictionary<string, SecretPayload> {
        [m1.Id.ToString()] = new(){ PrivateKeyContent = "KEY", Passphrase = "pp" },
        [m2.Id.ToString()] = new(){ Password = "pw" } } };
    var plan = LegacySecretMigration.Plan([ident], secrets);
    Assert.Equal(2, plan.Secrets.Count);
    Assert.Equal(SecretKind.SshPrivateKey, plan.Secrets.Single(s => s.Id == m1.Id).Kind);
    Assert.Equal("Prod · 私钥", plan.Secrets.Single(s => s.Id == m1.Id).Name);
    Assert.Contains(plan.Bindings, b => b.MethodId == m2.Id && b.SecretId == m2.Id);
}
[Fact] public void Plan_MergesIdenticalPrivateKeys_AndRepointsSecondMethod()
{
    // 两身份两个 VaultPrivateKeyMethod，私钥内容与口令完全相同；断言只剩 1 个 ssh-private-key 条目，
    // 第二个方法的 SecretId 指向保留条目 Id（= 第一个方法的 Id），保留条目名为第一个身份名。
}
[Fact] public void Plan_KeepsFileKeyPassphraseEntriesUnmerged_ByIdMethodId() { /* FilePrivateKeyMethod Persistent 的条目 Id == 方法 Id，不参与合并 */ }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~LegacySecretMigrationTests"`
Expected: 编译失败（`LegacySecretMigration` 未定义）。

- [ ] **Step 3: 实现 `LegacySecretMigration`（Core，纯函数）**

按上表实现：遍历输入的身份与方法，按 `methodId -> payload` 建条目；`KindOf(payload)`（`PrivateKeyContent` → 私钥；仅 `Passphrase` → 口令；否则密码）；命名 `"{身份名} · 私钥/密码/口令"`；**仅**对 `ssh-private-key` 条目按 `(PrivateKeyContent, Passphrase)` 去重合并（保留首个 Id）；`Bindings` 覆盖所有 `Vault*` 方法（合并后指向保留 Id），文件私钥条目 `Id = method.Id`、不产生 binding；未匹配任何方法的键也按命名规则建条目（无 binding）。

- [ ] **Step 4: 写失败测试（材料库加解密 + 迁移端到端 + 挂载点）**

`VaultSecretLibraryTests.cs`：
```csharp
[Fact] public async Task Save_ThenGet_RoundTrips_AndCipherIsNotPlaintext() { /* SaveAsync 后 GetPayloadAsync 相等；raw vault_secrets 行 algorithm 为 XCHACHA20-POLY1305 且不含明文 */ }
[Fact] public async Task GetPayload_WhenLocked_Throws() { /* 加密模式 Lock() 后 GetPayloadAsync 抛 InvalidOperationException；ListAsync 仍返回元数据 */ }
[Fact] public async Task WrongEntryId_FailsToDecrypt() { /* 把 A 条目密文换到 B 行，GetPayloadAsync(B) 抛 CryptographicException（AAD 绑定 Id）*/ }
[Fact] public async Task PlainMode_WritesPlainAndRoundTrips() { /* 明文模式 algorithm=PLAIN、可读 */ }
[Fact] public async Task Save_NullPayload_UpdatesMetadataOnly_KeepsPayload() { /* 改名后 payload 不变 */ }
[Fact] public async Task RotateAndMasterPassword_KeepMaterialReadable() { /* SetMasterPasswordAsync→RemoveMasterPasswordAsync→再 Set；材料仍可读 */ }
```
`VaultSecretMigrationTests.cs`（端到端，经 `InternalVaultManager`，用 `VaultTestDb` 夹具）：
```csharp
[Fact] public async Task UnlockWithMasterPassword_MigratesAndSplits_AndIsIdempotent()
{
    // 造加密库：SetMasterPassword + SaveSecretsAsync(identityId, {methodId: payload}) （旧整包 API 本 Task 仍在）
    // Lock → UnlockAsync("pw") → 断言 vault_secrets 有对应条目、metadata methods_json 已回填 SecretId、标记 =1
    // 再次 UnlockAsync 不重复建条目（条目数不变）
}
[Fact] public async Task StartupPlainMode_Migrates() { /* 明文模式造库 → TryAutoUnlockAsync() → 断言已迁移 */ }
[Fact] public async Task QuickUnlock_AlsoMigrates() { /* 走 IQuickUnlockVault.UnlockWithDeviceKeyAsync 成功后断言迁移 */ }
[Fact] public async Task LockedVault_DoesNotMigrate() { /* 未解锁时构造的库不被改动 */ }
[Fact] public async Task MigrationFailure_LeavesOldData_AndUnlockStillSucceeds() { /* 令迁移事务失败（如 methodId 非法），断言解锁成功、identity_secrets 完好、标记为空、再读回退旧表 */ }
[Fact] public async Task UnmigratedRead_FallsBackToLegacyBundle() { /* 明文模式不调 TryAutoUnlockAsync，直接 GetPayloadAsync(methodId) 命中旧整包 */ }
```

- [ ] **Step 5: 运行确认失败**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~VaultSecretLibraryTests|FullyQualifiedName~VaultSecretMigrationTests"`
Expected: FAIL（接口未实现 / 方法不存在）。

- [ ] **Step 6: 实现 `InternalVaultManager.SecretLibrary.cs`**

`partial class InternalVaultManager : IVaultSecretLibrary`（`using` 补 `Kei.Term.Infrastructure.Storage`）：
- 惰性 `SecretRepository` 字段（`new SqliteVaultSecretRepository(_factory)`）。
- `ListAsync` = `EnsureInitializedAsync` + 仓储列表。
- `GetPayloadAsync(id)`：`EnsureInitializedAsync`；仓储 `GetAsync`；若命中 → `DecryptItem(id, blob, alg)` + 反序列化；未命中 → `ReadLegacyPayloadAsync(id)`（遍历 `identity_secrets` 解密、找键 == `id.ToString()`，命中返回，否则 null）。
- `SaveAsync`：`WithSecretWriteLockAsync`；`payload` 提供时 `EncryptItem(secret.Id, json)` 后 `UpsertAsync`；`payload == null` 时要求行已存在（否则 `ArgumentException`），仅更新 `name/kind/meta/updated_at`。
- `DeleteAsync`：`WithSecretWriteLockAsync` + 仓储删除。
- 私有 `TryMigrateLegacySecretsAsync(ct)`：`IsUnlocked`（明文恒真）时不返回；`LoadValueAsync(KeySecretLibraryMigrated) == "1"` 则跳过；否则 `try { await new LegacySecretMigrator(_factory).RunAsync(DecryptItem, EncryptItem, _logger, ct); } catch (Exception ex) { _logger.LogWarning(...); }`（**不抛出**）。常量 `KeySecretLibraryMigrated = "secret_library_migrated"`。

- [ ] **Step 7: 实现 `LegacySecretMigrator` 与挂载**

`LegacySecretMigrator.RunAsync`：单事务内——读 `vault_metadata` 标记（已迁移返回 false）；用 Dapper 读 `identities(id, name, methods_json)` 与 `identity_secrets(identity_id, secrets_blob, encryption_algorithm)`；对每条解密为整包（`decryptItem`）；调用 `LegacySecretMigration.Plan`；对每个 `MigratedSecret` 用 `encryptItem(secret.Id, json)` 写 `vault_secrets`；对每个 identity 应用 `Bindings` 写回 `methods_json`；写 `secret_library_migrated = 1`；提交。合并发生时 `_logger.LogInformation("材料库迁移合并相同私钥 条目数={Count}")`（**绝不记私钥内容**）。任一步异常 → 事务回滚、异常向上（由调用方吞掉）。

在 `InternalVaultManager` 挂载（行内注释标明各解锁入口；迁移方法自带连接与事务，须在既有 `using` 连接作用域**结束之后**、方法返回前调用，避免嵌套事务）：
- 密码解锁 `UnlockCoreAsync`：明文分支 `return` 前、正常路径 `CompletePendingCleanupAsync` 的连接作用域结束后、旧库迁移 `MigrateLegacyVaultAsync` 返回后，各调用 `await TryMigrateLegacySecretsAsync(ct)`。
- 快速解锁 `UnlockWithDeviceKeyAsync`：`installed = true` 后、`return true` 前调用。
- 自动/明文启动 `TryAutoUnlockAsync`：明文模式时（`!_hasMasterPassword`），在 `WithSecretWriteLockAsync` lambda 内、其连接作用域结束后调用（加密模式该路径不产生解锁，故无挂载点）。
- `ReEncryptAllSecretsAsync`：追加 `ReEncryptTableAsync(conn, tx, oldKey, newKey, "vault_secrets", "id", ct)`。
- `RemoveMasterPasswordCoreAsync`：追加 `WritePlaintextAsync(conn, tx, vaultKey, "vault_secrets", "id", ct)`。

- [ ] **Step 8: 运行确认通过**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~VaultSecretLibraryTests|FullyQualifiedName~VaultSecretMigrationTests|FullyQualifiedName~LegacySecretMigrationTests|FullyQualifiedName~VaultTests|FullyQualifiedName~VaultQuickUnlockTests|FullyQualifiedName~VaultMaintenanceTests"`
Expected: PASS（新材料库与迁移测试通过；旧 `identity_secrets` 测试本 Task 仍绿）。

---

### Task 3: 消费路径与接口收敛（含移除 IVaultSecretStore 与存量测试/假实现改造）

**Files:**
- Modify: `src/Kei.Term.Core/Vault/VaultInterfaces.cs`（删除 `IVaultSecretStore`）
- Modify: `src/Kei.Term.Core/Services/AuthMaterializer.cs:51-75,160-189`（Vault 方法按 SecretId；文件私钥口令按方法 Id）
- Modify: `src/Kei.Term.App/Services/Connection/AuthMaterialCollector.cs:222-260,294-319,351-366`
- Modify: `src/Kei.Term.App/Services/VaultSessionService.cs:22,36-48,218-340`
- Modify: `src/Kei.Term.App/ViewModels/AuthPromptModels.cs:17,20-26`（`VaultKeyOption(Guid SecretId, string DisplayName)`；`AuthPromptResult.SecretId`）
- Modify: `src/Kei.Term.App/Views/AuthPromptWindow.axaml.cs:39,73,174-181`
- Modify: `src/Kei.Term.App/ViewModels/MainViewModel.cs:159-190`（`IVaultSecretStore` → `IVaultSecretLibrary`）
- Modify: `src/Kei.Term.App/ViewModels/IdentityManagerViewModel.cs:26,68-86,174-177`（去掉 `IVaultSecretStore` 与 `DeleteSecretsAsync`）
- Modify: `src/Kei.Term.App/ViewModels/IdentityEditViewModel.cs`·`IdentityMethodRowViewModel.cs`·`Views/IdentityManagerWindow.axaml.cs:31-42`·`Views/MainWindow.axaml.cs:163-165`（编辑器/管理器 VM 契约改造到条目模型）
- Modify: `src/Kei.Term.App/Views/MainWindowInteractionService.cs`·`SettingsWindowInteractionService.cs`（`AuthPromptResult.SecretId` 编译适配）
- Test: 改造 `tests/Kei.Term.Tests/{VaultTests.cs, VaultQuickUnlockTests.cs, VaultMaintenanceTests.cs, VaultSessionServiceTests.cs, IdentityManagerVaultTests.cs, AutomaticReconnectVaultTests.cs, ConnectionOrchestratorVaultTests.cs, TerminalFontZoomTests.cs, TerminalProfileApplicationTests.cs}`；更新 7 个假实现（`WorkspaceHostTests.cs:436`、`ReconnectTests.cs:296`、`ProxyFirewallTests.cs:883`、`ConnectionOrchestratorTests.cs:411`、`TreeRenameTests.cs:153` 的 `PlainVault`，`TerminalFontZoomTests.cs:89`、`TerminalProfileApplicationTests.cs:190` 的 `DummyVaultSecretStore`）→ 全部改实现 `IVaultSecretLibrary`。

**Interfaces:**
- Consumes: Task 2 的 `IVaultSecretLibrary`（`InternalVaultManager` 已实现）。
- Produces:
  - `AuthMaterializerContext` 语义：`GetVaultSecret`/`SaveVaultSecretAsync` 的键改为**条目 Id**（Vault 方法 = `SecretId`；文件私钥持久化口令 = 方法 Id），签名不变（`Func<Guid, SecretPayload?>` / `Func<Guid, SecretPayload, Task>`）。
  - `VaultSessionService`：`Task<IReadOnlyList<VaultSecret>> ListLibrarySecretsAsync(CancellationToken ct = default)`（不解锁）；`Task<SecretPayload?> GetSecretPayloadAsync(Guid secretId, bool allowInteraction = true, CancellationToken ct = default)`；`Task<bool> SaveLibrarySecretAsync(VaultSecret secret, SecretPayload? payload, CancellationToken ct = default)`；`Task<bool> DeleteLibrarySecretAsync(Guid secretId, CancellationToken ct = default)`（后两者需可写，取消解锁返回 false）。**删除** `LoadIdentitySecretsAsync`/`PersistSecretAsync`/`GetVaultKeyInfoAsync`/`PersistVaultKeyImportsAsync`。
  - `VaultKeyOption(Guid SecretId, string DisplayName)`；`AuthPromptResult` 末参更名 `Guid? SecretId`。
  - 身份编辑器 VM 契约（行为层）：`IdentityMethodRowViewModel.IsVaultMethod`、`AvailableSecrets`（`IReadOnlyList<VaultSecret>`）、`SelectedSecretId`（`Guid?`，回写 `Model.SecretId`）、`SelectedSecretStatus`（名称+指纹）、`CreateSecretAsync`（`Func<bool,Task<VaultSecret?>>`，bool = 私钥）、`[RelayCommand] CreateNewSecretAsync`；`IdentityEditViewModel.SecretsLoader`、`CreateSecretAsync`、`ReferenceCountLoader`、`AvailableSecrets`、`LoadSecretsAsync()`；**移除** `VaultKeyPassphrasePrompt`/`VaultKeyInfoLoader`/`PersistVaultKeysAsync`/`CollectVaultKeyImports`。

- [ ] **Step 1: 改 AuthMaterializer 测试（红）**

`AuthMaterializerTests.cs`：新增/调整 `VaultPassword`/`VaultPrivateKey` 用例，令 `Context(vault: id => ...)` 以 `method.SecretId` 命中；新增 `SecretId` 为 null 时返回 null；文件私钥 Persistent 用例仍以 `method.Id` 命中口令（语义不变）。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~AuthMaterializerTests"`
Expected: 新增用例 FAIL（当前按下 `method.Id` 取，SecretId 用例命中不到）。

- [ ] **Step 3: 实现 AuthMaterializer + Collector 改造**

`AuthMaterializer`：Vault 两分支改为 `Guid? key = method.SecretId; var secret = key is Guid k ? context.GetVaultSecret(k) : null;`；文件私钥分支的 `GetVaultSecret`/`SaveVaultSecretAsync` 仍用 `method.Id`（可复用 `SecretReferences.SecretIdOf`）。`AuthMaterialCollector`：把 `secrets` 换成 `Dictionary<Guid, SecretPayload>`；新增私有 `LoadReferencedSecretsAsync(Identity, bool)`（对每个方法用 `SecretReferences.SecretIdOf` 取条目 Id，`_vault.GetSecretPayloadAsync` 填充，去重）；`GetVaultSecret`/`SaveVaultSecretAsync` 键改为条目 Id，`SaveVaultSecretAsync` 调 `_vault.SaveLibrarySecretAsync(new VaultSecret{ Id=entryId, Name=..., Kind=...}, payload)`（文件私钥口令条目 `Kind=SecretKind.Passphrase`）；`BuildVaultKeyOptions` 改 `async` 并按 `VaultPrivateKeyMethod.SecretId` + `ListLibrarySecretsAsync` 名称生成 `VaultKeyOption`；`ResolvePromptResultAsync` 的 `PublicKeyVault` 用 `result.SecretId`。

- [ ] **Step 4: 实现 VaultSessionService 条目 API 与移除 IVaultSecretStore**

`VaultSessionService` 构造改为接收 `IVaultSecretLibrary`；实现四个条目 API（读路径 `ListLibrarySecretsAsync` 直接转发；写路径先 `EnsureReadyForWriteAsync`）；删除四个旧方法与其私有 helper 中仅旧的逻辑。删除 `VaultInterfaces.cs` 的 `IVaultSecretStore`，并更新 `MainViewModel`/`IdentityManagerViewModel`/`App.axaml.cs:145-165`（`new IdentityManagerViewModel(identityRepo, vault, logger)`）。

- [ ] **Step 5: 改编辑器/管理器 VM 契约（行为层，XAML 留 Task 5）**

`IdentityMethodRowViewModel`：加 `IsVaultMethod`/`AvailableSecrets`/`SelectedSecretId`/`SelectedSecretStatus`/`CreateSecretAsync`/`CreateNewSecretAsync`，`Apply()` 写回 `Model.SecretId`（Vault 方法）；移除旧暂存/导入字段与命令。`IdentityEditViewModel`：加 `SecretsLoader`/`CreateSecretAsync`/`ReferenceCountLoader`/`AvailableSecrets`/`LoadSecretsAsync()`（加载后按 kind 过滤下发到各行并解析已选状态），`ApplyCoreAsync` 去掉 imports 持久化，只落库身份。`IdentityManagerWindow.axaml.cs` 与 `MainWindow.axaml.cs` 的接线改用新的会话委托。

- [ ] **Step 6: 更新假实现与存量测试到条目 API**

7 个假实现改实现 `IVaultSecretLibrary`（内存字典即可）。`VaultTests`：`InternalVaultManagerTests`/`VaultKeyFormatTests` 的加解密/旧库迁移/AAD 用例改用 `SaveAsync`/`GetPayloadAsync`（条目 id 用固定 `Guid`）；删除 `VaultSecretStoreTests`（身份整包）——其覆盖由 Task 2 的 `VaultSecretLibraryTests` 承接。`VaultQuickUnlockTests`/`VaultMaintenanceTests`/`VaultSessionServiceTests`/`IdentityManagerVaultTests`/`AutomaticReconnectVaultTests`/`ConnectionOrchestratorVaultTests`（含 `KeyFixture` 与 `PublicKeyVault` 结果构造）改用条目 API / `SecretId`。

- [ ] **Step 7: 补共享私钥端到端测试（Review Focus 4）**

`ConnectionOrchestratorVaultTests.cs` 增加：两个身份各一个 `VaultPrivateKeyMethod`，`SecretId` 指向**同一条目**；断言两身份物化出的 `MaterializedAuthMethod.Secret.PrivateKeyContent` 相同，且编辑其中一身份（改其 `SecretId`）不影响另一身份。

- [ ] **Step 8: 运行确认通过**

Run: `dotnet build Kei.Term.slnx && dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`
Expected: 全绿（此时尚未有材料库窗口/编辑器 XAML 改动，UI 相关既有 headless 测试仍通过）。

---

### Task 4: 材料库窗口 ViewModel + IInteractionService 接入

**Files:**
- Create: `src/Kei.Term.App/ViewModels/VaultSecretLibraryViewModel.cs` · `VaultSecretListItemViewModel.cs`
- Modify: `src/Kei.Term.App/Services/IInteractionService.cs`（新增入口 + `NullInteractionService` 实现）
- Modify: `src/Kei.Term.App/Views/MainWindowInteractionService.cs`（实现入口）
- Modify: `src/Kei.Term.App/ViewModels/MainViewModel.cs`（`OpenVaultSecretLibraryCommand`）
- Modify: `src/Kei.Term.App/Views/MainWindow.Dialogs.cs`（打开窗口接线）
- Modify: `tests/Kei.Term.Tests/TestDoubles.cs:18`（`ScriptedInteraction` 实现新入口）
- Test: `tests/Kei.Term.Tests/VaultSecretLibraryViewModelTests.cs`（新建，headless）

**Interfaces:**
- Consumes: `VaultSessionService`（Task 3 四方法）、`IIdentityRepository`、`SecretReferences`、`IInteractionService`。
- Produces:
  - `public sealed record VaultSecretDraft(string Name, bool PrivateKey, string? PrivateKeyContent, string? Passphrase, string? Password);`
  - `IInteractionService`：`Task OpenVaultSecretLibraryAsync(VaultSecretLibraryViewModel viewModel);` 与 `Task<VaultSecretDraft?> PromptNewSecretAsync(bool privateKey);`（`NullInteractionService` 返回 `Task.CompletedTask`/`null`）。
  - `VaultSecretLibraryViewModel(VaultSessionService session, IIdentityRepository identities, IInteractionService interaction, ILogger? logger = null)`：`ObservableCollection<VaultSecretListItemViewModel> Items`、`bool UnreferencedOnly`、`Task LoadAsync()`、`Task CreatePrivateKeyAsync()`、`Task CreatePasswordAsync()`、`Task RenameAsync(VaultSecretListItemViewModel item)`、`Task DeleteAsync(VaultSecretListItemViewModel item)`。
  - `VaultSecretListItemViewModel`：`Guid Id`、`string Name`、`VaultSecret Secret`、`int ReferenceCount`、`string KindLabel`、`string Fingerprint`。

- [ ] **Step 1: 写失败测试（headless：删除保护 + 无引用筛选 + 未解锁可列出）**

`VaultSecretLibraryViewModelTests.cs`（`HeadlessAvalonia.RunAsync`，用 `VaultTestDb` + 真 `InternalVaultManager`）：
```csharp
[Fact] public Task Delete_ReferencedSecret_IsRejectedWithIdentityNames() => /* 两身份引用同一条目 → DeleteAsync 后条目仍在，Interaction.Notifications 含引用身份名 */
[Fact] public Task UnreferencedFilter_ShowsOnlyUnreferenced() => /* 建 2 条目，1 被引用；UnreferencedOnly=true 后 Items 仅 1 条 */
[Fact] public Task List_WhenLocked_StillShowsMetadata() => /* 加密库 Lock() 后 LoadAsync 不抛、Items 有元数据、不触发主密码提示 */
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~VaultSecretLibraryViewModelTests"`
Expected: 编译失败（ViewModel 未定义）。

- [ ] **Step 3: 实现 ViewModel**

`LoadAsync`：`Items = await session.ListLibrarySecretsAsync()` 映射；`referenceCounts = await identities.GetAllAsync()` → 对每条目 `SecretReferences.Find` 计数；`UnreferencedOnly` 过滤。`DeleteAsync`：`var refs = SecretReferences.Find(await identities.GetAllAsync(), item.Id); if (refs.Count>0) { await interaction.NotifyAsync(Strings.Get("VaultSecretLibrary.DeleteBlockedTitle"), string.Join("、", refs.Select(r=>r.IdentityName))); return; } await session.DeleteLibrarySecretAsync(item.Id); LoadAsync()`。`CreatePrivateKeyAsync`/`CreatePasswordAsync`：`draft = await interaction.PromptNewSecretAsync(privateKey)`，非空则 `session.SaveLibrarySecretAsync(new VaultSecret{...}, payload)` 后 `LoadAsync`。`RenameAsync`：`interaction.PromptTextAsync` → `SaveLibrarySecretAsync(secret with { Name }, null)`。日志只记名称/类型/引用数，**不记载荷**。

- [ ] **Step 4: 接入 IInteractionService 与主窗口命令**

`IInteractionService` + `NullInteractionService` + `MainWindowInteractionService`（`OpenVaultSecretLibraryAsync` 用 `Safe.RunAsync` + `new VaultSecretLibraryWindow(vm).ShowDialog(DialogOwner)`；`PromptNewSecretAsync` 打开新建材料对话框，owner = `DialogOwner`）+ `ScriptedInteraction`。`MainViewModel` 加 `[RelayCommand] OpenVaultSecretLibraryAsync`（构造 VM 并调 `Interaction.OpenVaultSecretLibraryAsync`），`MainWindow.Dialogs.cs` 预留窗口创建（XAML 由 Task 5）。

- [ ] **Step 5: 运行确认通过**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~VaultSecretLibraryViewModelTests"`
Expected: PASS。

---

### Task 5: 身份编辑器改造 + AuthPrompt/材料库视图（UI 交 @designer）

> **由 @designer 实现。** 本 Task 只固定行为/接口/ViewModel 契约（已在 Task 3、4 定义）与验收点；视觉、布局、密度、图标、动画细节由 @designer 决定，遵循 `avalonia-compact-tokens` 与既有暗色紧凑 token。

**Files:**
- Modify: `src/Kei.Term.App/Views/IdentityEditWindow.axaml:191-231`（Vault 方法行改为「材料下拉 + 新建/导入…」）
- Create: `src/Kei.Term.App/Views/VaultSecretLibraryWindow.axaml`（+ `.axaml.cs`）
- Create: `src/Kei.Term.App/Views/VaultSecretInputWindow.axaml`（+ `.axaml.cs`，新建密码/导入私钥 + 口令）
- Modify: `src/Kei.Term.App/Views/AuthPromptWindow.axaml`（保管库密钥项显示名称；若已由 `VaultKeyOption.DisplayName` 驱动则仅核对）
- Modify: `src/Kei.Term.App/Resources/Strings.resx`（新增 `VaultSecretLibrary.*`、`IdentityEdit.VaultKey.SelectMaterial` 等文案键；中英同步现有风格）

**Interfaces:**
- Consumes: Task 3 的 `IdentityMethodRowViewModel`/`IdentityEditViewModel` 成员；Task 4 的 `VaultSecretLibraryViewModel` 与 `IInteractionService.PromptNewSecretAsync`。
- Produces: 绑定到既有 VM 成员的 XAML；`VaultSecretInputWindow` 返回 `VaultSecretDraft?`；`VaultSecretLibraryWindow` 承载 `VaultSecretLibraryViewModel`。

- [ ] **Step 1: 编辑器方法行（@designer）** — Vault 密码/私钥行用下拉绑定 `AvailableSecrets` + `SelectedSecretId`，显示 `SelectedSecretStatus`（名称+指纹+引用数），并提供「新建/导入…」按钮绑定 `CreateNewSecretAsync`；文件私钥行保持原样。
- [ ] **Step 2: 材料库窗口（@designer）** — 列表（名称、类型、指纹、引用数）、操作（导入私钥、新建密码、重命名、删除）、「仅显示无引用」开关绑定 `UnreferencedOnly`；列宽/选择/空态由 @designer 定。
- [ ] **Step 3: 新建材料对话框（@designer）** — 密码输入或选私钥文件 + 口令，产出 `VaultSecretDraft`；口令框沿用现有风格。
- [ ] **Step 4: 主窗口入口（@designer）** — 菜单/命令入口打开材料库窗口（接 Task 4 命令）。
- [ ] **Step 5: 契约测试（headless，可 @designer 自行补齐控件名）** — 新增 `IdentityEditorSecretTests.cs`：编辑器 headless 打开后，Vault 私钥行下拉可选已有材料、切选后 `SelectedSecretId` 回写、`CreateNewSecretAsync` 调 `CreateSecretAsync` 并出现在下拉；材料库窗口 headless 打开、存在删除按钮与「无引用」开关。断言行为而非像素。

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~IdentityEditorSecretTests|FullyQualifiedName~VaultSecretLibraryViewModelTests"`
Expected: PASS。

---

### Task 6: 测试补齐（引用/回退/headless 契约收口）与覆盖核对

**Files:**
- Modify/Add: `tests/Kei.Term.Tests/{SecretReferencesTests.cs, VaultSecretMigrationTests.cs, VaultSecretLibraryViewModelTests.cs, IdentityEditorSecretTests.cs}`（补齐 Review Focus 未覆盖项）
- Modify: `tests/Kei.Term.Tests/SecureCrtImporterTests.cs`（确认行为不变：导入只建空方法身份）

**Interfaces:**
- Consumes: Task 1–5 全部产出。

- [ ] **Step 1: 逐条核对 Review Focus 并补测**
  - RF1 未解锁列元数据 → 已由 Task 4 `List_WhenLocked_StillShowsMetadata` 覆盖；补一条「未解锁时打开材料库窗口不弹主密码框」断言（`ScriptedInteraction.MasterPasswordPrompts == 0`）。
  - RF2 删除保护 → 已由 Task 4 覆盖；补文件私钥口令条目（`Kind=passphrase`，Id=方法 Id）被引用时同样拒绝。
  - RF3 迁移失败/回退 → 已由 Task 2 覆盖；补「迁移失败后再次解锁可成功重试并完成迁移」。
  - RF4 共享私钥 → 已由 Task 3 覆盖；补「迁移合并后两个身份的方法 `SecretId` 指向同一条目」。
  - RF5 明文模式 → 已由 Task 2 覆盖；补「明文模式新建材料后 Container 行为/PLAIN 属性」断言。
- [ ] **Step 2: 运行确认**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`
Expected: 全绿，新增断言均能在生产行为回归时失败。

---

### Task 7: 全量回归 + 文档 + 单次提交

**Files:**
- Modify: `docs/design/06-identity-and-auth.md:202-210,306`（`identity_secrets` 整包段落 → 材料库条目、`SecretId`、v8 表；三态口令的持久化归宿）
- Modify: `docs/design/02-vault-and-credentials.md`（若仍描述身份整包加密，补指向 06 的说明）

- [ ] **Step 1: 全量构建与测试（含集成）**

Run: `dotnet build Kei.Term.slnx && dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`
Expected: 全绿。
Run（Linux，可选）：`KEITERM_SSHD_TESTS=1 dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`
Expected: 通过或按环境既有跳过。

- [ ] **Step 2: 更新文档**

更新 `06-identity-and-auth.md`：身份模型加 `SecretId`；SQL 段落新增 `vault_secrets`（v8）与迁移规则；「三态口令」改述为口令条目（`passphrase`）与 `FilePrivateKeyMethod` 的隐式 Id 关联；§11 测试要点补迁移/引用/共享私钥。

- [ ] **Step 3: 单次提交（所有任务完成后）**

```bash
git add -A
git commit -m "feat(vault): 材料库条目化（v8）与身份方法 SecretId 引用 + 旧整包迁移"
```

- [ ] **Step 4: 提交自查** — `git status` 干净；`git show --stat` 仅含本计划涉及文件；无明文材料入日志或提交。
