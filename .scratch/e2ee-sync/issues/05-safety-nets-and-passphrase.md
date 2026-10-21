# 05 安全网与口令管理

Status: ready-for-human
Blocked by: 04

见 `../spec.md`「同步协调器」「触发与界面」。

**要做什么：** 让同步出错时可以恢复、批量删除时可以拦住、口令可以更换。用户能在同步页完成首次加入的选择、恢复，以及更换口令和停用同步。

- [x] 批量删除阈值：一次合并会删除超过 30% 或超过 50 个本机同步实体时，自动同步中止并标记「需要确认」；手动同步时弹出确认，走统一交互服务。
- [x] 首次加入（本机从未同步过、远端有同步包）：三选一（合并 / 以同步位置为准 / 以本机为准），默认合并；远端为空时直接初始化。
- [x] 合并前快照：每次合并写回之前保存一份，用 Vault Key 加密，只保留最近 10 份；在同步页可选择其中一份恢复，恢复后的数据在下次同步时胜出。
- [x] 历史同步包：每次上传前把当前同步包复制到 `history/`，只保留最近 5 份；在同步页可选择其中一份恢复。
- [x] 更换同步口令：先用旧口令完成一轮同步，再用新口令上传，同时清空历史同步包。其他设备解密失败时提示输入新口令，输入后继续同步。
- [x] 停用同步：可选删除同步位置中的同步包与历史；本机数据不受影响，同步口令从保险库删除。
- [x] 所有弹窗都走统一交互服务，不在 ViewModel 上加 `Func<>` 弹窗属性；界面交给 @designer。
- [x] 同步协调器测试覆盖：阈值中止、三种首次加入、两种恢复、更换口令后旧口令失效且历史已清空、停用。

## Comments

### 主会话最终自动化验收

- UI 接线及字号契约修复后，`git diff --check` 通过。
- `dotnet build Kei.Term.slnx`：0 警告、0 错误。
- `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --no-build`：1596 通过、40 条条件跳过、0 失败。
- 集成审查未发现可验证的新缺陷。代码与自动化验收已完成；仍待真实桌面视觉/交互验收，因此状态为 ready-for-human，不代表存在未完成的实现任务。
- 本轮 05 改动尚未提交；此前 WIP 提交为 `1891948`。下方为各阶段历史记录，UI 待接说明已由后续实现取代。

### 逻辑层 WIP 完成，UI 待 @designer；明文 Vault 边界现已获用户批准

本次未修改任何 Views/XAML，未提交。整票**没有 resolved**；上方包含「同步页可操作/弹窗」的复合验收项仍未勾选。用户已明确批准下述无主密码/去主密码策略，Status 改回 ready-for-agent，等待 @designer 接线。

### Core / Infrastructure 公开 API

- `SyncCoordinator` 现在必须传入 `ISyncSafetyStore`（生产传同一个 `InternalVaultManager`），可再传共享 `ISyncChangeSignal`。没有保留跳过安全网的旧构造路径。App 已装配；手动批准/恢复/换口令也抑制同步导入自己的修改信号。未改 08 的调度器策略。
- `SyncAsync(location, ct)` 是纯后台入口，不调用交互服务。`SyncResult.Confirmation` 返回 `SyncConfirmation(Token, Kind, DeletedCount, LocalLiveCount)`；Kind 为 `FirstJoin` / `BulkDeletion`。App 才调用 `SyncApprovedAsync(location, SyncApproval(token, choice), ct)`；`SyncJoinChoice` 为 `Merge`（默认）/ `Remote` / `Local`。
- 首次加入远端有包时，**导入前**返回选择要求；远端为空直接初始化，成功后在本机 `vault_metadata` 专用 key 记 joined。首次以远端为准若超过删除阈值，还需单独批准该删除预览。没有依据旧设备 id 猜测成功同步过；升级后缺少 joined 记账的设备会保守地再确认一次。
- 令牌绑定远端**密文字节摘要及版本标签**、本机同步信封、最终合并结果及选择用途。远端或本机变化即重新确认；即便新结果已经降到自动删除阈值以下，也不会复用旧授权（测试覆盖 51→48 个删除）。`ISyncSnapshotStore.ImportGuardedAsync(entities, expectedLocal, ct)` 在真正写事务内重新导出比较；变化时抛 `SyncPreviewChangedException`，协调器重新下载/预览，不执行旧授权下的新删除。阈值严格为 `count > 50 || count > localLive * .3`，只数本机存活实体实际变为墓碑的部分。
- `ISyncSafetyStore`：joined 记账、`SaveBackupAsync` / `ListBackupsAsync` / `ReadBackupAsync` / `DeleteSyncBackupsAsync`，以及待完成口令更换的新旧凭据保存/读取/清理。`InternalVaultManager` 实现；10 份快照作为一个加密本机条目保存在应用数据目录的现有 SQLite 内，保留创建时间和不透明 id，不创建明文文件，不进入同步包。所有新旧口令/序列化明文字节都按既有借用缓冲规则清零；JsonElement/托管字符串仍不能承诺 GC 前逐字节清零。
- 本机快照和待更换凭据使用现有 Vault Key/AAD 加密，随 `local_vault_secrets` 参与真实密钥轮换。**没有新建或修改迁移，沿用 v11。** 快照导入/恢复仍通过 Export/Import 的 v11 pending-redo 门禁及既有提交重放路径，不能越过待恢复记录。
- `ListBackupsAsync` / `RestoreLocalAsync(id)` / `RestoreHistoryAsync(location, name)`：恢复刷新版本至晚于当前/所选快照，并至少为当前时间；对当前存在但所选快照缺失的实体生成新墓碑，不只刷新快照中存在的实体。恢复前也保存安全快照；本机专属设置、树展开状态、Vault/存储凭据不被覆盖。仍受已批准的设备时钟偏差/LWW 边界约束。
- 每次普通上传前把已有主包原样存入 `history/`，轮转保留 5 份，全部仍是 age 密文；无变化不上传/不新增历史。历史名是时间与随机 id，不含业务名称。
- `ChangePassphraseAsync(location, newPassphrase)` 先用旧口令成功同步一轮，再读取校验当前包与本机状态，用新口令写入并清历史。写入前在 Vault 持久化新旧凭据；上传后断网/结果不确定、清历史失败均返回失败，不谎报成功。pending 存在时后台暂停，`ResumePassphraseChangeAsync(location)` 可直接使用 Vault 保留的新口令继续，不必让用户记住并重新输入。清历史失败后 `RestoreHistoryAsync` 也可用保留的旧口令读取旧历史。完成后才清 pending。
- `ISyncLocation.DeleteAsync` 已为本地/WebDAV/S3 实现主包删除。`ISyncPassphraseStore.DeletePassphraseAsync` 删除本机同步口令；`DisableAsync(location, deleteRemote)` 可选删除远端主包和历史，不删本机业务数据/本地快照，不删除或撤销 WebDAV/S3 存储访问凭据。
- 换口令不能撤回已下载数据，也不是按设备踢出；不支持条件写的位置仍仅 best-effort。读回只能发现部分覆盖窗口，不能保证无竞争数据丢失。停用远端删除也不阻止仍持有存储凭据的其他设备随后重新写入。

### 已获用户明确批准的兼容策略（启用前置检查，不只防护写快照）

现有 Vault 在未设主密码时没有可用于本地快照的 Vault Key；去主密码会把 `local_vault_secrets` 全部转成 PLAIN。如果直接沿用该转换，会把秘密备份落成明文，违反本票约束。

- **先为保险库设置主密码才能启用同步**，不是等需要写合并前快照时才失败。`ISyncSafetyStore.EnsureSyncMasterPasswordAsync` 由 Vault 初始化并检查真实模式；`SyncCoordinator.SyncAsync/SyncApprovedAsync` 在读取任何远端包前调用，`SyncScheduler` 在打开同步位置前调用，VM `SavePassphraseCommand` / `SyncNowCommand` 在保存/加入前调用，底层 `SavePassphraseAsync` 也独立拒绝无主密码保存。统一返回「请先为保险库设置主密码，再启用或加入同步；无主密码模式不能进行同步」。即使已留有旧同步口令、远端为空且完全不需要导入，也不能绕过检查。
- **不自动删除快照、不保留失去解密密钥的不可恢复备份，也不降级存明文。** 存在本机同步快照或 pending 口令凭据时拒绝去主密码，提示先显式删除本机快照/完成口令更换。`DeleteSyncBackupsAsync` 和 VM 的 `DeleteLocalBackupsCommand` 提供经确认的显式删除入口；正常更换主密码/轮换 Vault Key 不受影响，快照保持可解密。
- 没有使用同步的普通无主密码模式不受影响，仍可保存身份秘密、会话、设置等。未配置位置的后台调度静默跳过，不错误显示「必须先设主密码」。删除全部快照且无 pending 后仍可移除主密码，但后续同步被入口检查拒绝，设置主密码后才可继续。
- 决策已同步写入 `spec.md` 与 `docs/adr/0004-e2ee-sync-single-age-snapshot.md`，不再是未决策略。

### 给 @designer 的接线清单（不新增 Func 弹窗属性）

目标文件：`Views/Settings/SyncSettingsPageView.axaml`、现有窗口交互服务实现；目标为把下列 API 接成可操作且默认安全的同步页，不改后台策略。

- `IInteractionService.PromptSyncJoinAsync(preview)`：三选一，默认 `Merge`，取消为 null。
- `ConfirmSyncDeletionAsync(preview)`：显示真实删除数/本机存活数，默认拒绝。
- `PromptSyncPassphraseAsync(changing, error)`：设定新口令或解密失败后输入当前口令；取消 null，输入应遮蔽。不能把旧的 SavePassphrase 按钮当成更换口令，已加入时该命令会拒绝覆盖并引导安全流程。
- `PromptDisableSyncAsync()`：null 取消、false 仅停用、true 停用并删除远端主包/历史，不能承诺撤销存储账号权限。
- VM 新命令：`RefreshRecoveryCommand`、`RestoreLocalCommand`、`RestoreHistoryCommand`、`ChangePassphraseCommand`、`ResumePassphraseChangeCommand`、`DisableCommand`、`DeleteLocalBackupsCommand`；恢复数据：`LocalBackups` / `SelectedLocalBackup`、`RemoteHistory` / `SelectedRemoteHistory`、`HasPendingPassphraseChange`。页激活/用户刷新时执行 RefreshRecovery；恢复/删除快照已经走统一确认服务。
- `SyncNowCommand` 已处理结构化确认/重新确认和错误口令输入。新增交互接口的默认实现一律取消，**因此窗口实现未接好之前不会发生默认授权或后台弹窗**。当前 VM 两个职责文件合计小于 600 行。

### 验证证据

- 新增 **21 项**真实 SQLite/Vault/文件测试（`FullSyncSnapshotTests.Safety.cs` 15 项、`SyncSafetyThresholdTests.cs` 6 项）。覆盖三种首次加入、实际后台调度首次加入门禁、远端空初始化、严格 30%/50 边界、远端/本机预览变化和事务内本机并发编辑、以远端为准的二次删除确认、两种恢复中的缺失项删除及秘密恢复/本机项保留、10/5 轮转、Vault Key 轮换/去主密码保护、改口令旧口令失效及历史清理、上传成功后断网与清历史失败的恢复凭据/重启续作、两种停用、pending redo 不可越过。
- 原有普通往返测试现在明确模拟用户选择合并/批准精确预览；不再使用绕过安全网的生产构造路径。调度器/UI 重载回归仍验证真实 Dispatcher/信号组合。
- 最终 `dotnet build Kei.Term.slnx`：**0 错误 / 0 新警告**，仅已有 `TerminalProfileApplicationTests.cs:948` CS8602 1 条。没有为得到 0 警告修改无关测试。
- 最终 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --no-build --filter 'FullyQualifiedName~FullSyncSnapshotTests|FullyQualifiedName~SyncSafetyThresholdTests|FullyQualifiedName~SyncCoordinatorTests|FullyQualifiedName~SyncSchedulerTests|FullyQualifiedName~VaultMaintenanceTests|FullyQualifiedName~VaultTests'`：**88 通过 / 0 失败 / 0 跳过**。日志见 `/tmp/opencode/05-validation-final.log`；构建日志 `/tmp/opencode/05-build-final.log`。
- 加强密文断言时曾因 xUnit `Throws` 要求异常类型精确相等而失败（JsonReaderException 是 JsonException 子类）；改用 `ThrowsAny<JsonException>`，仍严格拒绝将密文解析为 JSON，未放宽密文算法/秘密恢复断言；随后上述 88 项完整重跑全绿。
- `git diff --check` 通过。没有跑全量，由主会话最后统一执行；没有跑真实 WebDAV/S3 后端或视觉验收，也未声称通过。

### 主密码前置条件补充验证及写入 scope

- 新增 3 项真实行为用例：远端空/非空两个用例均保留旧同步口令再去主密码，验证手动及真实调度器拒绝初始化/加入，本机快照和远端字节不变，设主密码后正常启用；另一项验证无主密码仍可保存/读取身份秘密与应用设置，未配置调度器静默，VM 保存口令/立即同步都给可读提示且不创建配置或远端目录。
- 既有上传成功后断网用例新增「没有任何恢复快照，只有 pending 换口令凭据」时拒绝去主密码的断言，独立覆盖两类保护条件。
- 本轮定向：`dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter 'FullyQualifiedName~FullSyncSnapshotTests.Safety_|FullyQualifiedName~SyncSafetyThresholdTests|FullyQualifiedName~SyncCoordinatorTests|FullyQualifiedName~SyncSchedulerTests|FullyQualifiedName~VaultMaintenanceTests|FullyQualifiedName~VaultTests|FullyQualifiedName~SyncLocationConfigurationSafetyTests'`：**65 通过 / 0 失败 / 0 跳过**；只有既有 CS8602 警告。前一节 88 项是前次完整逻辑定向结果，本轮未跑全量。
- 随后增量 `dotnet build Kei.Term.slnx --no-restore`：**0 警告 / 0 错误**；`git diff --check` 通过。没有提交。
- 本轮仅写 Core 的 `SyncContracts` / `SyncCoordinator` / `SyncCoordinator.Safety` / `SyncScheduler`、Infrastructure 的 `InternalVaultManager.Sync` / `InternalVaultManager.SyncSafety`、App 的 `ViewModels/Settings/SyncSettingsPage.cs`、`FullSyncSnapshotTests.Safety.cs` 与本工单/spec/ADR；没有写 Views/XAML 或窗口交互实现。@designer 可独立拥有 Views 与交互实现；上节 UI 接口和命令保持不变，新增的主密码前置检查已由 VM/服务负责，UI 不需要自行判断 Vault 密钥状态。

### @designer UI 与统一交互实现交付

1. **统一交互服务实现接线**：
   - 在 `IInteractionService` 的两个窗口实现（`SettingsWindowInteractionService` 和 `MainWindowInteractionService`）中完整接入同步专用交互窗口。
   - `PromptSyncJoinAsync`：实现 `SyncJoinChoiceWindow`，三选一单选框（`Merge` 推荐、`Remote` 远端覆盖、`Local` 本机覆盖），默认合并，Escape 或取消返回 `null`。
   - `ConfirmSyncDeletionAsync`：实现 `SyncDeletionConfirmationWindow`，明确提示待删除数（标红）与本机存活数，默认焦点留在取消按钮，防止误回车确认。
   - `PromptSyncPassphraseAsync`：实现 `SyncPassphrasePromptWindow`，口令输入框默认采用 `●` 掩码并提供显示明文切换；明确标注更换口令不会撤回已下载数据且无法撤销云存储账号访问权限。
   - `PromptDisableSyncAsync`：实现 `SyncDisablePromptWindow`，默认不勾选删除远端主包及历史（取消 `null`，仅停用 `false`，停用并删除 `true`）；文案明确指出不承诺撤销已下载数据或 WebDAV/S3 凭据。
   - 所有窗口均使用 `CenterOwner`、遵照 `KeiTokens`（`Kei.Bg.Panel`、`Kei.Font.UI`、`Kei.Radius.Card`、`Kei.Border.Subtle` 等设计令牌）与桌面紧凑模式，支持 Escape 键取消和 Enter 快捷键。未给 ViewModel 引入任何 `Func<>` 弹窗属性。

2. **同步设置页布局检查与现代化改造**：
   - 检查并重构 `SyncSettingsPageView.axaml`：
     - 对齐 `SecuritySettingsPageView` 与 `TransferSettingsPageView` 标准结构，外层应用 `Kei.Settings.ContentMaxWidth` 限制在宽屏下的无限延展。
     - 引入未完成口令更换横幅（绑定 `HasPendingPassphraseChange` 与 `ResumePassphraseChangeCommand`），给用户明显的续作入口。
     - 按职责划分为：同步位置与存储（支持本地目录、WebDAV、S3）、端到端加密与口令（含保存口令与更换口令按钮）、同步状态与手动触发（立即同步、停用同步）、数据恢复安全网（刷新恢复列表、清空全部快照、本机合并前快照列表+恢复、远端历史同步包列表+恢复）。
     - 细化表单行与说明，使用等宽字体显示快照 ID 和历史同步包名称，增强可读性。

3. **契约与行为定向验证**：
   - 新增 `SyncSettingsPageUiContractTests.cs`，通过 Avalonia Headless 测试 5 项 UI 契约：
     - 同步页核心控件与两个列表（快照、历史）成功渲染与视觉定位。
     - 首次加入选择窗口默认选中合并且支持取消。
     - 批量删除警告窗口正确显示数量且默认取消。
     - 口令输入窗口具备字符遮蔽与安全风险提示文案。
     - 停用同步窗口默认取消且默认不勾选删除远端。
   - 验证运行 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter 'FullyQualifiedName~SyncSettingsPageUiContractTests|FullyQualifiedName~FullSyncSnapshotTests.Safety_|FullyQualifiedName~SyncSafetyThresholdTests'`：**29 项全绿通过**。
   - 修复与契约复测：针对 `CompactUiContractTests.Views_DoNotHardcodeColors_OrUndersizeBodyText` 报告的 `SyncSettingsPageView.axaml` 处 11pt 正文字号违规，将规则说明文本、快照 ID、历史包名统一按项目既有正文字号规范调整为 12pt；同时排查并修正了新增的 4 个对话框（`SyncJoinChoiceWindow`、`SyncPassphrasePromptWindow`、`SyncDisablePromptWindow`）代码中的 11pt 次级文字至 12pt，不放宽测试。
   - 执行定向测试 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter 'FullyQualifiedName~CompactUiContractTests|FullyQualifiedName~SyncSettingsPageUiContractTests'`：**6 项全绿通过**。
   - 构建 `dotnet build Kei.Term.slnx` 成功，0 错误，无新增警告。
   - `git diff --check` 无空白或格式问题。未进行 commit，由主会话统筹。
