# 03 贯通：本地目录 + 会话树手动同步

Status: resolved
Blocked by: 01, 02

见 `../spec.md`「同步协调器」「同步位置」「触发与界面」。

**要做什么：** 端到端打通最窄的一条路径：两台设备用同一个本地目录和同一个同步口令，手动同步会话树（含会话配置）。另一台设备能看到增、删、改、移动，并且符合合并规则。

- [x] Core 中定义同步位置抽象与同步协调器；Infrastructure 中实现本地目录同步位置（先写临时文件再原子改名，不支持条件写）。
- [x] 快照格式带版本号，包含写入方设备标识与写入时间；读到更高版本时拒绝合并并提示升级。
- [x] 合并规则：按修改时间取较新者，时间相同时比较设备标识；墓碑参与比较；修改晚于删除的实体保留，父节点已删除时移到根目录。
- [x] 合并结果在单个事务里写回本机，写回后刷新会话树。
- [x] 不支持条件写时，上传后读回并比较哈希，不一致就重新执行整轮，最多 3 次；用测试同步位置注入「上传后被覆盖」，验证双方修改都保留。
- [x] 同步后清理超过 180 天的墓碑。
- [x] 设备标识首次同步时生成，属于本机专属项；同步口令存进保险库，保险库锁定时同步返回「保险库已锁定」。
- [x] 同步位置契约测试骨架，先只跑本地目录。
- [x] 设置窗口新增最简的「同步」页：选择本地目录、设定或加入同步口令、立即同步、上次同步状态（时间、结果、最后写入设备、错误原因）。界面交给 @designer。
- [x] 同步协调器的双设备测试覆盖上述行为，并包括「口令错误时本机不变」。

## Comments

### 公开 API 与扩展点（04/05/06/07/08）

- Core `ISyncLocation`：`SupportsConditionalWrites`、`TestConnectionAsync`、`ReadAsync`（返回可空 `SyncObject(Content, VersionTag)`）、`WriteAsync(byte[], string? expectedVersionTag, ...)`，以及 `ListHistoryAsync` / `ReadHistoryAsync` / `WriteHistoryAsync` / `DeleteHistoryAsync`。条件写时 null 标签表示对象必须不存在；冲突统一抛 `SyncWriteConflictException`。
- 06/07 新增同步位置：在 Infrastructure 实现上述接口即可；可靠条件写声明 true，上传后不再读回；不可靠条件写声明 false，协调器自动走 SHA-256 读回检查。App 当前只直接构造 `LocalFolderSyncLocation(directory)`；新增类型选择/配置工厂时替换这里。契约骨架为 `SyncLocationContractTests`，子类实现 `CreateLocation` 与清理夹具；本地目录子类始终运行。
- Core `SyncSnapshot(FormatVersion, WriterDeviceId, WrittenAtUtc, List<SyncEntity>)`：当前版本 1；`SyncEntity(EntityType, EntityId, UpdatedAtUtc, DeviceId, DeletedAtUtc, JsonElement Data)` 是通用实体信封。会话树类型为 `SyncSnapshot.TreeEntityType`（`tree`），内容为 `SyncTreeData(FolderNode?, SessionNode?)`；配置随 SessionNode 整体同步。`SyncTreeData.Node` 只供内存访问，不进入 JSON。
- 04 新增一类同步实体：定义该类内容 DTO 与稳定的类型/id，将其导出为 `SyncEntity`，在 `SqliteSyncSnapshotStore.ExportAsync` 和 `ImportAsync` 添加分派。通用合并按 `(EntityType, EntityId)` 工作，无须改比较算法；树父引用修正是唯一特化步骤。导入必须复用同一连接/事务，保留 `UpdatedAtUtc` / `DeviceId` / `DeletedAtUtc`，不得调用会重新盖本机时间戳的普通保存入口；目前不认识的类型会拒绝导入并提示升级，避免旧客户端无声丢数据。
- `ISyncSnapshotStore.ExportAsync` 返回实体列表；`ImportAsync` 返回实际导入的实体列表（事务内重新合并可能出现的本机新修改）；`PurgeTombstonesBeforeAsync` 接现有清理器。Infrastructure 实现为 `SqliteSyncSnapshotStore(SqliteConnectionFactory)`；树实体的内容/版本戳同一次读取事务导出，树仓储有内部 `ExportSyncEntitiesAsync` 与携带导入信封的 `SaveNodeCoreAsync` 路径。
- `SyncCoordinator(ISyncSnapshotStore, ISyncPackage, ISyncPassphraseStore, ISyncDeviceId)`；`SyncAsync(ISyncLocation, ...)` 返回 `SyncResult(Success, Message, WrittenAtUtc?, WriterDeviceId?)`；纯合并接缝为 `SyncCoordinator.Merge`。单实例信号量串行化调用；自动触发的防抖、定时与运行中触发合并交给 08，目前没有自动触发。
- `ISyncDeviceId : ICurrentDeviceId` 添加 `EnsureCreated()`；`PersistentDeviceId(path)` 构造只读取已有标识，首次真正同步时才创建随机标识。App 共享这个实例给 `SqliteConnectionFactory`，普通仓储从此自动记录当前设备。
- `ISyncPassphraseStore` 提供 `IsUnlocked`、`GetPassphraseAsync`（可清零的 char[]）、`SavePassphraseAsync(ReadOnlyMemory<char>)`。`InternalVaultManager` 直接实现，专用本机 `local_vault_secrets` 条目使用现有 Vault 加解密/AAD/密钥轮换；不创建假身份或代理。现有明文 Vault 模式仍遵循其明文语义，设定主密码后一起加密，去主密码/主密钥轮换也一起处理。协调器清零取出的字符和序列化明文字节缓冲。
- App 触发入口：`SyncSettingsPage.SyncNowCommand` → 后台 `SyncCoordinator.SyncAsync` → `Synchronized` 事件 → `MainViewModel.ReloadTreeAsync`；`App.axaml.cs` 完成装配。独立页面 VM 约百行，目录选择复用现有 `IInteractionService.PickFolderAsync` / `SettingsWindowInteractionService`，没有新增 Func 弹窗属性。目录、设备标识、上次同步状态分别存在本机 `sync-location.txt` / `sync-device-id.txt` / `sync-status.txt`，不进入应用设置的同步记账或快照。同步页操作立即生效，不随设置窗口取消撤销。

### 行为与边界

- 下载完整解密、拒绝未来格式版本之后才导出/导入本机；错误口令返回「同步口令不正确」，锁定返回「保险库已锁定」，损坏同步包返回可读失败，以上情况不改本机实体。无变化时不重新加密/上传，最后写入设备/时间保持远端值。
- 合并按修改/删除 UTC 时间取较新者，平局按设备标识 ordinal 裁决；完全相同版本但异常不同内容再作确定性 JSON 裁决。墓碑参与合并，不把远端清单缺失视为删除。修改比墓碑新时保留；存活节点引用缺失、已删、非目录或循环父节点时移到根目录，不刷新原版本戳。
- 写回在单个 SQLite 事务中，使用 `SqliteConnectionFactory`，延迟外键检查到提交以容纳任意父子顺序。在事务内重新加载并合并，保留网络往返期间刚保存的本机编辑；未变实体不重写。导入绝不重新盖本机当前时间/设备。
- 上传前先把合并结果留在本机；读回 SHA-256 不一致或条件写冲突则从下载开始重做，最多总共 3 轮。即使重试耗尽，本机仍保有之前合入的数据，页面也刷新树；不存在「上传失败就回滚用户刚做的本机修改」的行为。
- 成功后调用现有清理器，截止时间为 UTC 当前时间减 180 天；严格早于截止时间才清理。同步位置只写密文：主包 `keiterm-sync.age`，临时文件同目录写完后 `File.Move(overwrite: true)` 原子替换；本地目录不声称支持条件写。历史存取在 `history/`，拒绝路径穿越。
- 只追加迁移 v10：创建本机 Vault 条目表，并重建 `session_details` 去掉 `identity_id` 外键、保留节点外键。原因：03 已要求整份会话配置往返，而另一设备的身份要等 04 才同步；不能清空身份引用或制造假身份。原外键测试相应改为验证可保留悬空身份引用，其他外键行为照旧。v1–v9 未修改。完整悬空配置标记属于 04。

### 未做 / 偏离

- 04 的其他实体与秘密/设置全覆盖、05 的合并前快照/历史轮转/恢复/首次加入三选一/删除阈值确认/换口令流程、06/07 网络位置、08 自动触发都未实现；历史存取能力已就绪，但不会自动创建历史。
- 当前「保存同步口令」用于设定/加入，不是安全的换口令流程。不要把它当成 05 已实现的换口令/撤销旧设备入口。
- UI 仅沿用现有 SettingsStyles 搭好最简页面，尚未做运行时视觉/交互人工验收或设计打磨。尝试派 @designer 但当前子代理深度上限禁止再派代理；编排者如需打磨请将 `Views/Settings/SyncSettingsPageView.axaml` 与目标交给 @designer。

### 验证

- 新增同步协调器/本地位置行为测试共 16 项：各自真实 SQLite + 加密 Vault 的双设备，会话配置/移动/删除、墓碑与较新子会话移根、同时间设备裁决、远端版本戳保留/空闲不重写、错误口令与锁定不改实体、损坏包/更高格式拒绝、导入原子回滚/事务内新修改保留、180 天清理、设备重启持久化、Vault 重启/换主密码/去主密码、覆盖注入读回检查/条件写冲突/三轮上限、本地位置主包与历史契约、历史路径穿越。
- 首次全量暴露 3 项因 v10 产生的回归：旧约束测试仍要求身份外键（2 项），部分旧表迁移夹具没有 session_details（1 项）；已更新契约测试并兼容部分旧表夹具，没有放宽其余外键断言。
- 最新 `dotnet build Kei.Term.slnx`：0 错误 / 0 新警告；仅既有 `TerminalProfileApplicationTests.cs:948` CS8602 警告 1 条。
- 默认构建目录定向 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter 'FullyQualifiedName~SyncCoordinatorTests|FullyQualifiedName~LocalFolderSyncLocationTests|FullyQualifiedName~StorageConstraintTests|FullyQualifiedName~PortForwardTests.V5'`：46 通过，0 失败，0 跳过。较早同步/Vault/记账相关组合为 112 通过。
- 最后默认目录全量 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`：1516 通过 / 36 跳过 / 0 失败；跳过均为平台/外部依赖条件测试。未使用 `--artifacts-path`，没有 commit。
