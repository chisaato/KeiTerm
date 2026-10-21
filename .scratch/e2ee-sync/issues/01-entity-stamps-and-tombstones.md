# 01 同步实体记账与墓碑（预重构）

Status: resolved
Blocked by: None (can start immediately)

见 `../spec.md`「同步实体与本地记账」。

**要做什么：** 让本机数据具备合并所需的记账，但本机的可见行为不变。每个同步实体都记录最后修改时间与设备标识；删除改为写墓碑，读取时一律看不到墓碑。这一张不涉及任何同步逻辑或界面。

- [x] 会话树节点、身份、代理、端口转发、外部编辑器、文件关联的删除改为写墓碑。原本靠外键级联删除的子实体（子树、会话配置、端口转发、秘密、编辑器路径、文件关联）由仓储显式写墓碑。
- [x] 所有读取路径（含启动时组树）排除墓碑；在 UI 上删除后的表现与之前一致。
- [x] 通过追加迁移给端口转发、known_hosts 补修改时间列，新建记账表，记录设置项、Profile、连接元数据键的修改时间、设备标识与墓碑。不修改已发布的迁移。
- [x] 保存设置项、Profile、连接元数据键时刷新对应记账；连接元数据仍不改所属会话的 `revision`。
- [x] 秘密（身份秘密、代理密码）的写入会刷新所属身份或代理的修改时间。
- [x] 本机 `revision` 在每次用户修改时递增，后台写入的元数据除外。
- [x] 提供按「早于某时间」物理清理墓碑的仓储能力（供 03 调用）。
- [x] 现有仓储测试改为断言：删除后读取不到、墓碑存在、子实体也写了墓碑；全量测试通过。

## Comments

### 公开 API

- `Kei.Term.Core.Storage.ICurrentDeviceId.DeviceId`：可注入的当前设备标识；`SqliteConnectionFactory(string connectionString, ICurrentDeviceId? currentDevice = null)` 在每次写入时读取它。未启用同步时允许 `null`；03 可直接接入首次同步生成并持久化的设备标识，不需要修改仓储构造函数。
- `ILocalChangeTracker` / `SqliteLocalChangeTracker`：
  - `TrackAsync(string entityType, string entityId, bool deleted = false, CancellationToken cancellationToken = default)`。
  - `GetAllAsync(...)`：读取 `local_entity_stamps`，含墓碑；返回 `LocalEntityStamp(EntityType, EntityId, UpdatedAtUtc, DeviceId, DeletedAtUtc, Revision)`。仅是设置/Profile/元数据键的记账，不是数据库实体的整库导出接口。
  - `PurgeTombstonesBeforeAsync(DateTime cutoffUtc, ...)`：在一个事务中物理清理严格早于截止时间的数据库墓碑及记账墓碑。先清子实体；旧父墓碑不能级联删除较新的子墓碑或存活子实体。
- `LocalChangeTracking.Flatten` / `TrackChangesAsync`：设置按点分隔叶子路径记账，数组按一个有序值记账；不为未变化项递增 revision。
- `JsonSettingsService` 构造函数末尾新增可选 `ILocalChangeTracker? tracker`；`ProfileManagerService` 构造函数末尾同样新增可选 tracker。App 已为两者接入同一 SQLite tracker。

### 行为

- 只追加 v9：为数据库同步实体加 `device_id`，补端口转发/known_hosts 的 `updated_at`，为 known_hosts 和跟随父实体的子表补墓碑，创建 `local_entity_stamps`。v1–v8 未修改。旧连接元数据键按所属节点时间回填记账。
- 数据库主实体保存时 revision 从 1 起计，更新/删除/移动递增；删除为幂等墓碑写入。身份秘密和代理密码保存/清除在同一事务中刷新所属实体时间、设备与 revision。展开/折叠仍保持原来的视图状态语义，不刷新内容时间/revision。
- 删除子树先单次加载 parent_id，在内存收集后代，再事务内显式标记节点、会话配置、端口转发与元数据键；启动组树、单个查询、列表和秘密读取均看不到墓碑。
- 删除身份仍清空会话的身份引用，保持原先本机表现；受影响会话同时刷新记账。删除编辑器显式标记路径和文件关联。端口占用唯一索引改为只约束存活转发，旧墓碑不阻止重新使用同一监听地址。
- 设置记账类型为 `setting`，id 为属性路径（如 `FontSize`、`FileTransfer.VerifyHashEnabled`）；Profile 为 `profile`，id 为 Profile id；连接元数据为 `metadata`，id 为 `<session-guid>/<key>`。profiles.json 中的默认选择和标签栏偏好作为 `setting` 的 `profiles.*` 项记账。
- 设置/Profile 初始化为缺失的项建立记账，但重启不刷新已有项；草稿、预览、取消不写墓碑，实际成功保存/补偿保存才比较持久化快照。后台元数据写入与清除只更新该键，不改会话 revision。

### 未做 / 偏离

- 没有同步逻辑、同步包、设备生成、同步配置或 UI；未改 02 的文件。设备标识默认空值，等待 03 接入。
- known_hosts 也保留墓碑，并继续按 `(host, port, public_key)` 自然键去重；重新信任同一密钥会复用原行而非插入重复项。
- 墓碑保留 180 天由 03 调用清理接口时计算截止时间；本工单不注册自动清理任务。
- settings.json / profiles.json 保持原有原子文件写入，然后写 SQLite 记账；没有引入跨文件与 SQLite 的分布式事务。

### 验证

- 新增 `LocalEntityStampTests` 9 项真实 SQLite/文件测试，覆盖子树与子实体墓碑、秘密刷新、设备来源切换、设置逐项比较与重启、Profile 删除、元数据不改会话 revision、known_hosts 自然键复活、端口复用、过期父墓碑与较新子实体、v8 元数据回填。现有身份/元数据仓储测试也断言墓碑和 revision。
- 定向测试：第一组 95 通过 / 1 条 sshd 集成跳过；新增边界测试后的第二组 37 通过 / 1 条 sshd 集成跳过，0 失败。
- `dotnet build Kei.Term.slnx --artifacts-path /tmp/opencode/keiterm-entity-stamps`：0 警告 / 0 错误。
- 初次全量（独立 artifacts 目录）：1500 通过 / 36 跳过 / 0 失败；不能据此代替默认目录复核。
- 编排者复核后的修正：默认目录确实复现 Profile 重启 revision 从 1 变 2。加入临时诊断并重新编译后，原源码的 `baseline: true` 分支已正确执行且原断言通过，提示默认输出存在源码/二进制不一致；没有证据将它解释为随机测试失败。临时诊断已移除。
- 生产代码进一步将初始化与保存的记账入口明确分离：`SeedTrackingBaselineAsync` 只为 SQLite 中缺失的 Profile/偏好建立记账，已有项不进行内容比较、不刷新时间或 revision；`TrackBundleAsync` 仅用于成功保存后的变更比较。共用 `SnapshotTrackingItems`，不再通过可选布尔参数切换两种语义。没有放宽或修改测试断言。
- 最新默认目录构建：`dotnet build Kei.Term.slnx`：0 错误 / 1 条已有警告（`TerminalProfileApplicationTests.cs:948` CS8602）。
- 最新默认目录定向验证：`dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --no-build --filter "FullyQualifiedName~LocalEntityStampTests"` 连续 5 次，每次 9 通过 / 0 跳过 / 0 失败。
- 最新默认目录全量：`dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --no-build`：1500 通过 / 36 跳过 / 0 失败。跳过项是平台/外部依赖条件测试。
- 没有 commit。
