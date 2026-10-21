# 04 快照扩展到全部应用数据

Status: resolved
Blocked by: 03

见 `../spec.md`「同步实体与本地记账」「同步协调器」。

**要做什么：** 让同步包覆盖全部应用数据，只排除本机专属项。在一台设备上改任何设置、身份或 Profile，同步后都能在另一台设备上看到。

- [x] 身份连同秘密：导出时用本机 Vault Key 解密，导入时用目标设备的 Vault Key 重新加密，明文只在内存中流转。
- [x] 代理连同密码、端口转发、外部编辑器（含按操作系统区分的路径）、文件关联。
- [x] known_hosts 按自然键（主机、端口、公钥）合并，两台设备各自信任过的同一个主机密钥合并后只有一条。
- [x] 连接元数据按（会话、键）合并。
- [x] `settings.json` 按设置项合并：不同设置项的修改都保留，同一项以最后写入为准。
- [x] `profiles.json` 中每个 Profile 作为一个同步实体合并。
- [x] 本机专属项排除清单：窗口位置与尺寸、最近打开的目录、快速解锁登记、同步配置本身（含同步位置凭据）、设备标识。有测试保证它们不进入同步包。
- [x] 悬空引用（跳板、代理、身份、Profile、终端 Profile）：合并时不改写，读取时把对应会话标记为「配置不完整」，界面上有提示（提示样式交给 @designer）。
- [x] 同步协调器的双设备测试覆盖以上每类同步实体的往返与冲突裁决。

## Comments

### 公开 API 与装配

- `SqliteSyncSnapshotStore(factory, vault = null, settingsPath = null, profilesPath = null)`：保留 03 的仅树构造方式；生产 App 必须提供真实 Vault 与两个文件路径。存在秘密却没提供 Vault、收到文件实体却没配置对应文件时明确失败，不静默丢弃数据。
- 数据库内容 DTO 为 `SqliteSyncSnapshotStore.RecordData(Fields, Paths?, Secret?)`，其中 Fields 使用固定内容列白名单；编辑器路径为 `EditorPath(Os, Path)`，跟随编辑器做一次 LWW。秘密为原业务秘密 JSON 的内存字节，不携带源设备密文、nonce 或 Vault 元数据。Profile DTO 为 `ProfileData(Kind, Content)`，Kind 为 `guiProfiles` / `terminalProfiles`。
- 新实体类型：`identity`、`proxy`、`port-forward`、`editor`、`association`、`known-host`；除 known-host 外 id 均为原实体 Guid。known-host id 是自然键 JSON 元组 `(host, port, public_key)` 的 SHA-256 小写十六进制摘要；数据库 surrogate Guid 不参与合并，新设备使用摘要前 16 字节生成本机 Guid，已有设备保留原行 Guid。
- 沿用 01 的 `setting` / `profile` / `metadata` 类型与标识：设置点分路径、Profile id、`<session-guid>/<key>`；`profiles.*` 偏好仍属于 setting，并将记账中的 PascalCase 路径映射到 profiles.json 的 camelCase 根属性。
- `SqliteSyncSnapshotStore.Imported`：成功提交并释放数据库事务、秘密写锁、文件持久化门后，依次 await 宿主的 `Func<Task>` 处理器。App 在 UI 线程重载设置、Profile 与会话树，因此手动/自动同步共用，不依赖同步页按钮的刷新事件。08 无须另改快照或设置刷新逻辑。
- `JsonSettingsService.ReloadSynchronizedAsync` / `ProfileManagerService.ReloadSynchronizedAsync`：重读持久化内容，不重新盖版本；设置广播已提交事件，Profile 广播内容/默认选择变化。后者另补 `RemoveCustomGuiProfile` 对称删除入口。
- `SqliteTreeRepository` 可注入 `profileExists` / `defaultIdentityId` 读取回调，避免 Infrastructure 依赖 App 内置 Profile 表；App 已接入。`SessionNode.IsConfigurationIncomplete` / `ConfigurationWarning` 只在读取时计算，不进入包。检测会话身份/全局默认身份、直接跳板、代理、代理中的跳板引用，以及显式/默认终端 Profile 和 GUI Profile 选择。
- `SyncLocalOnly` 集中列出设置路径、文件名/前缀、`vault_metadata` / `local_vault_secrets` 表与 `is_expanded` 字段。同步只读取明确的业务表与 settings.json/profiles.json，不扫描数据目录或系统快速解锁存储。

### 行为

- 导出树、各数据库实体与版本戳、元数据键记账使用同一读取事务。导入事务内重新导出并合并，保留网络往返期间的本机编辑；按远端 UpdatedAtUtc / DeviceId / DeletedAtUtc 写回，不调用会重新盖章的普通仓储保存入口。revision 只作为本机计数递增，不进同步内容。
- 导入先处理墓碑，再处理存活实体；树先于连接元数据键写入，外键仅延迟到提交。known_hosts 自然键冲突复用已有行。秘密与编辑器路径的清除/删除保留子表墓碑。
- Vault 快照入口与密钥轮换共用秘密写锁，并先于 SQLite 事务取锁。导出秘密使用源设备 Vault Key，导入在同一数据库事务内用目标设备 Vault Key 重加密；原始解密字节、反序列化后的秘密字节在 finally 清零。不写临时明文秘密文件或日志；普通字符串/JsonElement 属于托管不可变对象，不能承诺 GC 前逐字节清零。协调器仍负责同步包序列化明文字节/口令清零。
- `LocalPersistenceGate` 串行化设置/Profile 的文件替换 + 记账与快照持久化边界，不锁终端/文件传输或 UI 草稿。文件只补丁胜出的设置叶子与 Profile，保留本机专属值；初始未落盘或仅包含部分叶子的文件按服务默认值补齐读取，不把缺失当成删除。
- `IsExpanded` 不序列化，导入树时更新语句保留本机 is_expanded；新设备默认折叠。悬空引用保留原值，不清空、不制造假实体；列表与单个读取都会标记配置不完整。会话树条目追加最小文字提示。

### 偏离与后续边界

- 未追加 Schema 迁移，沿用 01/03 的 v9/v10；未修改同步位置实现、位置配置表单或触发调度。工作区中的 Infrastructure csproj 变动属于并行 06/07 lane，不是本工单修改。
- settings.json/profiles.json 仍不是与 SQLite 的跨资源分布式事务：导入先验证所有实体与外键，再原子替换文件，普通异常时补偿恢复原文件并回滚 SQLite；进程恰好在文件替换与数据库提交间被强杀的恢复不在现有存储能力内，与 01 的文件/记账边界一致。
- 视觉未做运行时人工验收。请交给 @designer：`src/Kei.Term.App/Views/Controls/SessionTreeItemView.axaml`，目标为「配置不完整提示的视觉、布局与可访问性打磨」；本工单只保证文字可见并遵守现有字体契约。
- 没有 commit。

### 验证

- 新增 `FullSyncSnapshotTests`，共 14 个双设备用例；每台设备使用真实 SQLite、不同主密码/独立随机 Vault Key、真实设置/Profile 服务，共享真实本地目录同步位置，通过生产协调器与 age 同步包验证。
- 覆盖所有实体的内容往返、每类实体新时间/同时间设备标识冲突裁决、仅秘密修改传播与不同 Vault 密文、空闲不重写/不重新盖章、各类删除、设置不同点分路径与 Profile 偏好独立合并、不同元数据键保留、known_hosts 自然键去重/删除、各类悬空引用单独标记、展开状态保留和本机专属值/同步口令不进解密包。排除测试输入独立于生产清单，删漏规则会真实泄露测试标记并失败。
- 首次全量发现本工单提示文字字号 10 违反 `CompactUiContractTests.Views_DoNotHardcodeColors_OrUndersizeBodyText`；改用既有 `Kei.Tree.FontSize`，没有修改测试契约或归因于并行 lane。
- 最终 `dotnet build Kei.Term.slnx`：0 错误 / 0 新警告，仅既有 `TerminalProfileApplicationTests.cs:948` CS8602 一条。
- 默认构建目录定向：`dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter 'FullyQualifiedName~FullSyncSnapshotTests|FullyQualifiedName~SyncCoordinatorTests|FullyQualifiedName~LocalEntityStampTests|FullyQualifiedName~LocalFolderSyncLocationTests|FullyQualifiedName~CompactUiContractTests'`：40 通过 / 0 失败 / 0 跳过。
- 最后默认构建目录全量：`dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`：1530 通过 / 0 失败 / 36 跳过；跳过是平台/外部依赖条件测试。未使用 --artifacts-path。`git diff --check` 通过。

### 原 scope 复核：信号、UI 回调与进程中断（待恢复方案确认）

本次只修改 04 的测试与本文；没有修改 08 调度器、06/07 配置页或 05，没有提交，没有跑全量。Status 重开为 ready-for-human，原因是下述真实恢复缺陷尚未实现修复，不能继续声称全部 resolved。

1. **「设置/Profile 保存新开连接导致 08 信号丢失」是误报。** `SqliteLocalChangeTracker` 使用注入的共享 factory；factory 每次 `OpenAsync` 都附加 `SqliteSyncChangeObserver` trace，新的连接不是信号断点。新增 `SharedFactory_SettingsAndProfileSavesPublishRealChangeSignals` 使用真实 SQLite、设置/Profile 服务及 `SyncChangeSignal`，直接观测实际修改发出的 Changed，并检查实际 Profile 快照。下述生产组合测试进一步验证保存会实际预约 30 秒防抖、由真实协调器上传，另一设备读取到新设置/Profile，而非仅断言一次 Publish。

2. **当前实际装配下「Imported UI 重载重新盖章并无限预约」未复现，是误报。** 新增 `SchedulerAndUiImportedReload_DoNotRestampOrQueueAnotherRound_UserSavesDo`：真实 SyncScheduler → SyncCoordinator → SQLite snapshot store → 与 App 一致的 `Dispatcher.UIThread.InvokeAsync<Task>` 和两层 await → 两个 `ReloadSynchronizedAsync`。实测 UI 回调内 `IsSuppressed == true`，初次导入无 Changed 信号；导入实体的信封/时间/设备与远端逐字一致，调度器下一次等待是 5 分钟周期而非 30 秒防抖。随后真正的用户设置保存和 Profile 保存各预约并完成一轮同步。这里不能用「Dispatcher 必然丢 AsyncLocal」的猜测替代实际消息/ExecutionContext 行为；也不需要向保存入口额外补发信号或扩大全局抑制。

3. **「文件替换后、SQLite 提交前被杀导致内容/版本失配」已真实复现。** 现有代码没有启动恢复 journal，也没有在启动 `LoadSettingsAsync` / `ProfileManagerService.InitializeAsync` 前恢复的机制。临时故障诊断通过独立 `dotnet vstest` 子进程执行生产 ImportAsync；仅在诊断库改用 SQLite DELETE journal 模式并持有读事务，使 SQLite 提交成为可观测屏障（生产库通常是 WAL，不修改生产模式）。观察到两个 JSON 文件已替换后杀掉子进程，再用新的设置/Profile 服务按实际加载入口重启：字号 15→37、Profile 名 before→after，但相应两条 SQLite 记账仍与中断前完全相同。诊断源码/日志保存在 `/tmp/opencode/04-crash-repro-source.cs`、`/tmp/opencode/04-crash-repro.log`；该「确认坏行为」的临时测试已从仓库移除，避免把已知缺陷固化为通过标准。修复获准后应将同一故障边界改写为断言恢复成功的持久回归测试。

#### 最小持久恢复提案（尚未实现，请确认边界）

- 推荐只为本工单的 settings/profile 导入做**单份 SQLite redo journal**：在原导入事务里同时保存实体版本和待应用的设置叶子/Profile 操作。使用现有本机 `vault_metadata` 的专有 key 命名空间保存待恢复记录，不新增通用事务框架或迁移，不把 journal 同步出去。journal 只含本轮参与同步的 setting/profile 操作；不含身份/代理秘密，不复制整个原始设置文件及其中保留的本机专属配置。所记录的是本来就明文持久化在 settings.json/profiles.json 的同一类数据，因此可在保险库解锁前恢复。
- 先提交 SQLite（含 redo），再按原子文件替换应用 journal；全部文件应用成功后才清除 journal。崩溃前未提交则库/文件仍旧；崩溃后已提交则下次先重放，文件完成一半也可幂等重放。重放保留本机专属设置、不调用重新盖章的普通保存入口。
- 在任何设置/Profile 的权威读取、保存、记账基线建立以及下一次快照导入/导出之前完成重放；使用现有 LocalPersistenceGate 保证单份 pending journal 不被后续操作越过。恢复损坏/IO 失败时必须 fail closed，保留 journal 并报可读错误，不能按默认值启动并记账覆盖待恢复状态。
- **行为权衡，需确认：** 文件写入失败时 SQLite 可能已经提交；此时不再声称普通异常能回滚整个导入，而是进入待恢复状态，阻止继续读写相关设置，待重放成功后继续。这样的 redo 边界比「文件前置 + try/catch 补偿」更容易保证进程中断后的正确性，但需要更新现有错误行为说明。只承诺进程中断/重启恢复，不扩展到跨设备事务或突然断电的跨文件系统持久性协议。
- **所有权边界，需协调：** `Program.BuildAvaloniaApp` 在 App 正常初始化之前，已通过 `NativeMenuSettings.CreateX11Options(AppPaths.SettingsFile)` 预读 settings.json。只在 04 拥有的 App/设置加载路径恢复，无法保证这个最早预读也看到恢复后的值。建议允许在最早 settings 消费入口增加一个最小恢复调用（由拥有 Program/NativeMenuSettings 的编排者处理）；否则需要明确接受「权威设置/版本恢复一致，但本次启动 X11 原生菜单预读可能滞后一启」这一限制。本次未越界修改这些文件。
- 获准后的故障测试：SQLite 提交前、提交后但尚未写文件、只替换首个文件、两个文件均替换但 journal 未清除、恢复再次中断。每次以新连接/新服务实例启动，断言文件内容与实体版本一致、无重复盖章、journal 最终清理、本机项不被覆盖；另测 journal 损坏与 IO 失败保留待恢复状态。

#### 本次定向验证

- 首轮新增两个信号/UI 生产组合测试：2 通过 / 0 失败。
- 独立子进程中断诊断：1 次通过确认上述内容新/版本旧的实际坏状态；它不是恢复修复通过。
- 最终默认目录 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter 'FullyQualifiedName~FullSyncSnapshotTests'`：16 通过 / 0 失败 / 0 跳过；仅既有 `TerminalProfileApplicationTests.cs:948` CS8602。`git diff --check` 通过。没有跑全量，没有提交。

### 已批准的 redo 恢复实现（取代上文的待确认提案/旧补偿边界）

#### API 与持久格式

- 追加迁移 **v11 `sync_file_redo_journal`**，新建本机单行表 `sync_file_journal(id CHECK(id=1), payload)`。没有修改 v1–v10；本次采用专用表而非先前提案借用 vault_metadata，隔离保险库元数据与文件恢复职责。新表列入 `SyncLocalOnly.DatabaseTables`，不导出同步包。
- `SyncFileRecovery(factory, settingsPath, profilesPath).PrepareStartupAsync(ct)`：在 LocalPersistenceGate 下先幂等迁移，再幂等重放，返回实际 Schema 版本。重复调用不会修改实体版本或重新建立基线。重放与删除 journal 持有同一 SQLite 写事务，并发恢复者取得锁后重新读记录，不能继续使用旧的已重放副本。
- journal 仅序列化本轮胜出的 `setting` / `profile` 信封；不持久化身份/代理秘密、Vault Key、同步口令，不复制含本机专属项的完整文件。目标路径不出现在 journal 的控制结构里，只从可信本机装配传入的 settingsPath/profilesPath 选择；记录中混入其他实体或被排除的设置则拒绝恢复。设置值里即使含字符串路径，也只是设置值，不能改变写入目标。
- `ILocalPersistenceReadiness` 由 `SqliteLocalChangeTracker` 实现；设置/Profile 服务在同一 LocalPersistenceGate 内，先检查 pending，再进行加载、基线建立或保存。pending 时不自动先重放再保存旧内存草稿，而是明确拒绝，等待启动恢复后重新加载。快照导入/导出也在事务内检查 pending，禁止新导入覆盖 journal。
- `SyncFileRecoveryException` 表示待恢复/恢复失败。内部 checkpoint 仅供真实故障测试注入，生产无回调；不是可配置插件或通用事务框架。

#### 精确的提交与启动顺序

1. 导入在一个 SQLite 事务内合并并写入全部实体/版本；预验证文件操作、目标配置及外键后，在**同一事务**插入 journal。此时不替换任何 JSON 文件。
2. 提交 SQLite；逐个读取本机目标文件并幂等应用叶子/Profile 操作，保留未触及项与本机项。通过同目录临时文件、Flush(true)、原子替换写入；全部成功后删除 journal 并提交清理事务。
3. 只有上述步骤全部成功且锁释放后才触发 Imported。提交后文件出错时不触发 Imported、不上传或声称同步成功；返回明确「同步数据已提交，但设置/Profile 待恢复」错误。journal 和已提交的实体版本保留，旧 JSON 不会被当成本地新修改。
4. 已核查真实最早读取：`Program.BuildAvaloniaApp` 在末尾调用 `NativeMenuSettings.CreateX11Options`，后者创建无 tracker 的 JsonSettingsService 并同步读取菜单偏好。因此 Program 现在在构造 AppBuilder/菜单预读前调用 `AppPaths.PrepareStorage()`，先迁移和恢复；恢复失败直接向 stderr 输出中文错误并以非零状态退出，不进入菜单设置的默认值回退。
5. 独立启动 App（未经过 Program）时，在创建设置/Profile 服务与 ViewModel 前再次调用同一幂等准备入口；失败明确输出错误并 Shutdown(1)。App 不再在后面重复执行原迁移，而使用已准备的版本。Vault 初始化仍可按现有约定再次检查 Schema，最新版本检查为无变化。
6. 读取设置、FinalizeStartupLayout、初始化 Profile 基线之后才启动既有 SyncScheduler，避免恢复/首次基线与启动同步交错；没有修改 08 调度器实现或策略。

#### 故障/恢复行为

- SQLite 提交前中断：全部数据库改动（含 journal）回滚，文件未写，重启仍为旧的一致状态。
- 提交后、首文件完成后、全部文件完成但 journal 未清除时中断：启动按同一操作幂等重放；过程中再次中断也不重复 Profile、不重新盖章。恢复只改文件与 journal，不改同步版本记录。
- IO 失败、损坏/非法 journal：保留记录并 fail closed；设置/Profile 加载或保存、快照导入/导出均报待恢复错误，不走坏文件备份/默认值回退或基线记账。修复文件访问问题后重启即可重放；没有后台自动清空记录或覆盖旧内存编辑。
- 此处保证的是批准的进程中断/重启恢复。设置/Profile 仍以本来的明文格式存在；journal 不额外包含业务秘密或本机专属配置。没有实现跨文件系统断电协议、任意外部编辑器并发协调、或跨设备事务。多个独立应用进程同时正常编辑同一组 JSON 的原有并发问题不在本工单中扩展处理；恢复者本身通过 SQLite 写事务串行化。

#### 回归与验证证据

- 新增 `FullSyncSnapshotTests.Recovery.cs` **13 个用例**。5 个 checkpoint 使用独立 `dotnet vstest` 子进程，在真实 WAL 库/文件操作边界暂停后杀掉整个进程树：提交前、提交后未写文件、settings 文件完成、profiles 文件完成、清理 journal 前。随后以新连接/新服务实例迁移恢复，比较 JSON 内容、完整同步信封与记账，锁住之前已复现的「内容新、版本旧」缺陷。
- 另一个真实子进程测试在启动重放途中再次被杀；再次启动恢复无重复 Profile。覆盖写文件失败后恢复仍失败→修复后成功、pending 期间所有相关保存/加载/同步受阻、journal 不被后续草稿覆盖、Imported 不误报、本机项保留、无 journal 的正常启动、v10→v11、Program/App 共用准备入口重复调用、菜单预读取得恢复后的偏好、损坏记录保留、journal 不含身份秘密且不能选择远端路径。
- 保留此前两个真实设置/Profile 信号与 Dispatcher/调度器组合回归：重载不重新盖章、不自激同步；用户保存仍真实触发既有 08。
- `dotnet build Kei.Term.slnx`：0 错误 / 0 新警告；仅既有 `TerminalProfileApplicationTests.cs:948` CS8602。
- 默认目录定向命令：`dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter 'FullyQualifiedName~FullSyncSnapshotTests|FullyQualifiedName~SyncCoordinatorTests|FullyQualifiedName~LocalEntityStampTests|FullyQualifiedName~SchemaMigratorTests|FullyQualifiedName~StorageConstraintTests|FullyQualifiedName~SqliteProfilePersistenceTests'`：94 通过 / 0 失败 / 0 跳过。
- 未跑全量，留给编排者统一执行；没有提交，没有修改 06/07/08 的无关实现。`git diff --check` 通过。上文初版实现/复核中的旧限制与旧验证数字保留为历史记录，本节描述当前行为。
