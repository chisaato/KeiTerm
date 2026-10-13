# 数据清单与旧密文清理 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在设置的安全页列出七类数据的用途和条数，并允许用户确认后删掉已经迁入材料库的 `identity_secrets` 旧副本。

**Architecture:** Core 只定义清单项和清理模式。`InternalVaultManager` 在同一把 `_secretWriteLock` 里重判迁移标记后删除旧行，不解密。设置页用子 ViewModel 展示；确认走新的 `IInteractionService.ConfirmAsync`，不新增 `Func<>` 弹窗属性。

**Tech Stack:** .NET 10、SQLite/Dapper、Avalonia 12、CommunityToolkit.Mvvm、xUnit。

**Spec:** `docs/superpowers/specs/2026-10-14-data-inventory-design.md`

## Global Constraints

- 固定七项，Id 依次为 `sessions`、`identities`、`secrets`、`legacy-secrets`、`proxies`、`known-hosts`、`editors`。不读 `sqlite_master` 来发现表，不列出 `vault_metadata`。
- 会话条数只计 `tree_nodes.node_type = 1`（`NodeType.Session`）。文件夹不算。
- `vault_secrets` 条数不过滤 `deleted_at`。
- 表不存在时该项条数为 0，不抛错。初始化正常建表，这条只防半残库。
- 完成标记键 `secret_library_migrated`，已迁移身份键 `secret_library_migrated_identities`。标记值必须等于 `"1"` 才算完成。集合键缺失、空或 JSON 损坏时当作空集，不得因此删行。
- 清理不解密、不解锁、不改 `vault_secrets` / `identities` / `vault_metadata`，不 `DROP TABLE`，不加 schema 版本。
- 确认框弹出期间不持有 `_secretWriteLock`，也不持有数据库事务。删除在锁内重判，单事务，失败整批回滚。
- 日志只记模式和删除行数，不得出现密文、口令或私钥内容。
- 不新增 ViewModel 的 `Func<>` 弹窗属性。`ConfirmDeleteAsync` 的现有返回值不变（主窗口仍直接 `true`，设置页标题仍是移除主密码）。
- 文案只用本计划钉死的 `Strings.resx` 键和中文，不要另写口号。
- 不按 Task 提交，也不要每个 Task 跑全量测试。Task 1 只跑 `DataInventoryTests`。Task 2 跑它自己的测试，最后跑一次全量 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`。全部通过后一次提交。

## Review Focus

1. 部分完成时把还没迁走的身份整包删掉，下次解锁无法重试，材料丢失。Task 1 的 `Partial_DeletesOnlyMigratedRows_AndFallbackStillReads` 钉住。
2. 已迁移集合 JSON 损坏被当成「全部可删」。Task 1 的 `CorruptMigratedSet_DeletesNothing` 钉住。
3. 用户取消或没有交互服务时仍然删除。Task 2 的 `Purge_WhenConfirmDeclined_DoesNotDelete` 与 `Purge_WhenInteractionMissing_DoesNotDelete` 钉住。
4. 文件夹被算进会话数。Task 1 的 `Load_CountsSessionsNotFolders_AndOtherItems` 钉住。
5. 确认时还是部分完成，加锁时标记已变成 `"1"`，却留下无法解析的旧行，或误删 `vault_secrets`。Task 1 的 `Complete_DeletesAllLegacyRows_IncludingUnparseableIds` 钉住加锁后的规则。

---

## 文件

- 新建 `src/Kei.Term.Core/Vault/DataInventory.cs`：枚举、`DataInventoryItem`、`IDataInventory`。
- 新建 `src/Kei.Term.Infrastructure/Vault/InternalVaultManager.DataInventory.cs`：计数与删除。
- 新建 `src/Kei.Term.App/ViewModels/Settings/DataInventorySettingsViewModel.cs`：清单行与清理命令。
- 新建 `src/Kei.Term.App/Views/ConfirmDialog.cs`：主窗口和设置窗口共用的确认框。
- 新建 `tests/Kei.Term.Tests/DataInventoryTests.cs`、`tests/Kei.Term.Tests/DataInventoryViewModelTests.cs`。
- 改 `InternalVaultManager` 的接口列表、`IInteractionService` 及其四个实现、`SettingsViewModel`、`SecuritySettingsPage`、`SettingsWindow.axaml`、`App.axaml.cs`、`Strings.resx`、`docs/design/06-identity-and-auth.md`、本规格的状态行。

### Task 1: 清单计数与旧行删除

**Files:**
- Create: `src/Kei.Term.Core/Vault/DataInventory.cs`
- Create: `src/Kei.Term.Infrastructure/Vault/InternalVaultManager.DataInventory.cs`
- Modify: `src/Kei.Term.Infrastructure/Vault/InternalVaultManager.cs`（类声明追加 `IDataInventory`）
- Test: `tests/Kei.Term.Tests/DataInventoryTests.cs`

**Interfaces:**
- Consumes: `LegacySecretMigrator.MarkerKey`、`LegacySecretMigrator.MigratedIdentitiesKey`、`WithSecretWriteLockAsync`、`VaultTestDb`、仍存在的 `SaveSecretsAsync`。
- Produces:
  - `enum LegacySecretState { NotStarted, Partial, Complete }`
  - `enum LegacySecretCleanup { None, MigratedRowsOnly, AllRows }`
  - `sealed record DataInventoryItem(string Id, int Count, IReadOnlyList<int> ExtraCounts, LegacySecretState? LegacyState, LegacySecretCleanup Cleanup, int DeletableCount)`
  - `interface IDataInventory { Task<IReadOnlyList<DataInventoryItem>> LoadAsync(CancellationToken ct = default); Task<int> PurgeLegacyIdentitySecretsAsync(CancellationToken ct = default); }`
  - `InternalVaultManager` 实现该接口。只有 `legacy-secrets` 的 `LegacyState` 非 null，且只有它的 `Cleanup` 不是 `None`。

- [ ] **Step 1: 写失败测试**

用 `VaultTestDb.CreateVault` 和真实 SQLite。旧整包用 `SaveSecretsAsync` 写入。损坏整包沿用 `UPDATE identity_secrets SET secrets_blob = zeroblob(48), nonce = NULL, tag = NULL`。

断言：

- `Load_CountsSessionsNotFolders_AndOtherItems`：1 个文件夹 + 2 个会话、1 个身份、1 条 `vault_secrets`、1 个代理、1 条 `proxy_secrets`、1 条已知主机、1 个编辑器 + 1 条路径 + 1 条关联。七项 Id 与顺序如上。会话数是 2。代理 `ExtraCounts` 为 `[1]`。编辑器 `ExtraCounts` 为 `[1, 1]`。其余 `ExtraCounts` 为空。材料库 `Cleanup` 为 `None`。
- `NotStarted_PurgeDeletesNothing`：有旧行、没有完成标记、已迁移集合为空。`Load` 的状态是 `NotStarted`，`Cleanup` 是 `None`，可删行数是 0。直接 `Purge` 返回 0，旧行还在。
- `Partial_DeletesOnlyMigratedRows_AndFallbackStillReads`：两个身份各有整包，第二个整包损坏后解锁。完成标记不是 `"1"`。`Purge` 只删第一个身份的行。再解锁后，第二个身份的旧材料仍能经回退读到。`vault_secrets` 行数不变。
- `CorruptMigratedSet_DeletesNothing`：完成标记不是 `"1"`，集合值为 `not-json`，另有一条旧行。`Purge` 返回 0。
- `Complete_DeletesAllLegacyRows_IncludingUnparseableIds`：标记为 `"1"`，另插一行 `identity_id = 'not-a-guid'`。锁定保管库后 `Purge`（`IsUnlocked` 为 false 也必须成功）。旧表为 0 行。`vault_secrets` 仍在，标记仍是 `"1"`。捕获日志，断言不含测试用的私钥正文。`Load` 此时状态为 `Complete`，`Cleanup` 为 `AllRows`，可删行数为 0。

- [ ] **Step 2: 跑测试，确认失败**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter DataInventoryTests`
Expected: 编译失败或测试失败，因为 `IDataInventory` 不存在。

- [ ] **Step 3: 实现计数与删除**

`LoadAsync` 一次连接读完七项，不取写锁，不解密。`legacy-secrets` 的状态按规格第 3 节：标记 `"1"` 为 `Complete` / `AllRows` / 可删行数等于全表行数；否则集合非空为 `Partial` / `MigratedRowsOnly` / 可删行数等于 Id 能解析且落在集合内的行数；否则 `NotStarted` / `None` / 0。

`PurgeLegacyIdentitySecretsAsync` 包在 `WithSecretWriteLockAsync` 里。锁内重新读标记和集合，开事务，按同一规则删除，提交后返回变更行数。部分完成用参数化 `IN`，集合为空时一条都不删。`LogInformation("清理旧身份密文 模式={Mode} 删除行数={Count}", ...)`。

- [ ] **Step 4: 跑测试，确认通过**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter DataInventoryTests`
Expected: PASS。不提交。

### Task 2: 安全页清单与确认框

**Files:**
- Create: `src/Kei.Term.App/ViewModels/Settings/DataInventorySettingsViewModel.cs`
- Create: `src/Kei.Term.App/Views/ConfirmDialog.cs`
- Modify: `src/Kei.Term.App/Services/IInteractionService.cs`（接口、`NullInteractionService`）
- Modify: `tests/Kei.Term.Tests/TestDoubles.cs`（`ScriptedInteraction`）
- Modify: `src/Kei.Term.App/Views/MainWindowInteractionService.cs`
- Modify: `src/Kei.Term.App/Views/SettingsWindowInteractionService.cs`
- Modify: `src/Kei.Term.App/ViewModels/Settings/SecuritySettingsPage.cs`
- Modify: `src/Kei.Term.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/Kei.Term.App/ViewModels/SettingsViewModel.Interaction.cs`
- Modify: `src/Kei.Term.App/App.axaml.cs:165-175`
- Modify: `src/Kei.Term.App/Views/SettingsWindow.axaml`（安全页模板，本机解锁 `Border` 之后）
- Modify: `src/Kei.Term.App/Resources/Strings.resx`
- Modify: `docs/design/06-identity-and-auth.md:217,286,316`
- Modify: `docs/superpowers/specs/2026-10-14-data-inventory-design.md` 状态改为「已实现」
- Test: `tests/Kei.Term.Tests/DataInventoryViewModelTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `IDataInventory` 与 `DataInventoryItem`。`SecuritySettingsPage.Interaction` 已是 `Func<IInteractionService>?`。
- Produces:
  - `Task<bool> ConfirmAsync(string title, string message)`
  - `DataInventorySettingsViewModel(IDataInventory inventory, Func<IInteractionService?> interaction)`
  - `Task LoadAsync()`
  - `[RelayCommand(CanExecute = nameof(CanPurge))] Task PurgeLegacyAsync()`，`CanPurge` 为当前 `legacy-secrets` 的 `DeletableCount > 0`
  - 行：`Title`、`Description`、`CountText`、`ShowPurge`（仅 `legacy-secrets` 为 true）
  - `SecuritySettingsPage.DataInventory`，库存为 null 时该属性为 null
  - `SettingsViewModel` 末尾追加可选参数 `IDataInventory? dataInventory = null`

- [ ] **Step 1: 写失败测试**

`ScriptedInteraction` 增加 `bool ConfirmResult`（默认 false）、`int ConfirmCalls`、最后一次的 title 与 message。

- `Purge_WhenConfirmDeclined_DoesNotDelete`：库存可删行数大于 0，`ConfirmResult` 为 false。命令执行后 `Purge` 调用次数为 0，`ConfirmCalls` 为 1。
- `Purge_WhenInteractionMissing_DoesNotDelete`：`interaction` 返回 null。不调用 `Purge`，不抛异常。
- `Purge_WhenConfirmed_PurgesOnceAndReloads`：第一次 `Load` 可删 2 行，确认消息包含 `2`，`Purge` 返回 2；第二次 `Load` 可删 0。断言 `Purge` 只被调用一次，命令随后不可执行。
- `SecurityCategory_LoadsInventory`：`SettingsViewModel` 传入假库存，把 `SelectedCategory` 切到安全页。`LoadAsync` 被调用一次。再切到别的分类，调用次数不变。

- [ ] **Step 2: 跑测试，确认失败**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter DataInventoryViewModelTests`
Expected: 编译失败或测试失败。

- [ ] **Step 3: 实现界面与确认**

`ConfirmAsync`：`NullInteractionService` 与 `ScriptedInteraction` 不弹窗；前者恒 false，后者返回 `ConfirmResult` 并记下 title、message、次数。`ConfirmDialog.ShowAsync(Window owner, string title, string message)` 复用设置页现有确认框的按钮和 token。设置页的 `ConfirmDeleteAsync` 改为调用它，标题仍是 `Settings.Security.RemoveMasterPassword`。主窗口的 `ConfirmDeleteAsync` 保持 `Task.FromResult(true)`；它的 `ConfirmAsync` 用主窗口当 owner 弹出同一个对话框。

`PurgeLegacyAsync` 的顺序：先 `LoadAsync`；可删行数变成 0 就停；再 `ConfirmAsync`；返回 false 或交互服务为 null 就停；最后 `PurgeLegacyIdentitySecretsAsync`，然后再 `LoadAsync`。确认文案用下面的消息格式，参数是这次 `Load` 的可删行数。

切到安全页时 `SettingsViewModel` 调用 `DataInventory.LoadAsync()`。`App.axaml.cs` 把同一个 `vault` 作为 `dataInventory` 传入。

字符串，键和值逐字使用：

| 键 | 值 |
|---|---|
| `Settings.Data.Section` | 数据清单 |
| `Settings.Data.Sessions.Title` | 会话 |
| `Settings.Data.Sessions.Description` | 会话树里的连接，以及各自的连接参数和端口转发。表：tree_nodes、session_details、port_forwards。 |
| `Settings.Data.Identities.Title` | 身份 |
| `Settings.Data.Identities.Description` | 登录用的用户名和认证方法，不含密码或私钥。表：identities。 |
| `Settings.Data.Secrets.Title` | 材料库 |
| `Settings.Data.Secrets.Description` | 正在使用的密码、私钥和口令。无引用的条目在材料库窗口删除。表：vault_secrets。 |
| `Settings.Data.Legacy.Title` | 旧身份密文 |
| `Settings.Data.Legacy.NotStarted` | 材料库启用前按身份打包的旧密文。表：identity_secrets。先解锁一次，让迁移跑完。 |
| `Settings.Data.Legacy.Partial` | 材料库启用前按身份打包的旧密文。表：identity_secrets。可以删除已迁走的 {0} 行。还留下 {1} 行尚未迁走或解密失败，下次解锁会重试。 |
| `Settings.Data.Legacy.Complete` | 材料库启用前按身份打包的旧密文。表：identity_secrets。这些行已是材料库中的旧副本。 |
| `Settings.Data.Legacy.Purge` | 清理旧副本 |
| `Settings.Data.Legacy.ConfirmTitle` | 清理旧身份密文 |
| `Settings.Data.Legacy.ConfirmMessage` | 将删除 {0} 行已迁入材料库的旧副本。材料库里正在使用的密钥不会删除。 |
| `Settings.Data.Proxies.Title` | 代理 |
| `Settings.Data.Proxies.Description` | 代理配置，以及 {0} 条代理口令。表：proxies、proxy_secrets。 |
| `Settings.Data.KnownHosts.Title` | 已知主机 |
| `Settings.Data.KnownHosts.Description` | 主机密钥的信任记录。在已知主机窗口管理。表：known_hosts。 |
| `Settings.Data.Editors.Title` | 编辑器与关联 |
| `Settings.Data.Editors.Description` | 外部编辑器，以及 {0} 条可执行路径、{1} 条文件关联。表：external_editors、external_editor_paths、file_associations。 |
| `Settings.Data.Count` | {0} |

`Partial` 的 `{1}` 是该项 `Count - DeletableCount`。条数文本用 `Settings.Data.Count`。

XAML 只改安全页模板：本机解锁那一节后面加一节 `settingsSection`，`IsVisible` 绑定 `DataInventory` 非空。标题用 `settingsSectionTitle`。列表沿用现有字号、`Kei.Text.Secondary` 和间距，不新配色、不新开窗口。每行是名称、说明、条数；只有 `ShowPurge` 的那一行显示按钮，按钮文本是 `Settings.Data.Legacy.Purge`，命令是父级 `PurgeLegacyCommand`。给列表和按钮分别加 `x:Name="DataInventoryList"`、`x:Name="PurgeLegacySecretsButton"`。

`06-identity-and-auth.md` 三处「v9 清理」改成：已迁入材料库的行由数据清单在用户确认后删除，不自动 `DROP TABLE`。身份删除仍级联清掉该身份的旧行。规格状态改为「已实现」。

- [ ] **Step 4: 跑测试并全量回归**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "DataInventoryTests|DataInventoryViewModelTests"`
Expected: PASS。

Run: `dotnet build Kei.Term.slnx` 然后 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`
Expected: 构建 0 警告 0 错误；测试 0 失败。环境跳过可以保留。

- [ ] **Step 5: 一次提交**

```bash
git add src tests docs/design/06-identity-and-auth.md docs/superpowers/specs/2026-10-14-data-inventory-design.md docs/superpowers/plans/2026-10-14-data-inventory.md
git commit -m "feat(settings): 增加数据清单，允许清理已迁移的旧身份密文"
```
