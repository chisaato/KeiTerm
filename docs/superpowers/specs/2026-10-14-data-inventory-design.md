# 数据清单与旧密文清理

日期：2026-10-14　状态：已实现

## 1. 背景与目标

材料库迁移完成后，旧表 `identity_secrets` 仍保留每个身份的整包密文。完成标记 `vault_metadata[secret_library_migrated] = 1` 之后，读取不再回退这张表，但行不会自动删除。部分完成时，已迁走的身份行同样留下，只有未迁移或解密失败的行还要供下次解锁重试。

用户要在设置里看到「库里有哪些数据、各自存什么、现在有多少、哪些旧数据可以删」。这不是 SQLite 管理器：不展示 SQL，不能按行改活数据，也不能对任意表执行删除。

目标：

- 在设置的「安全」页增加一节「数据清单」。
- 每一项用用途名说明存的是什么，并给出条数。说明里可以写出对应表名，作为对照，不作为操作对象。
- 目前唯一可清理的是已迁入材料库的旧身份密文。
- 清理前必须经过确认。取消、交互服务缺失、当时没有可删行，都不删除。
- 不解密、不记录密文。日志只记删除条数和模式。

非目标：

- 通用增删改、导出、执行 SQL、`VACUUM`。
- `DROP TABLE identity_secrets` 或新增 schema 版本。重加密、明文导出和遗留读写仍引用该表；拆掉这些路径之后才能另做 v9。
- 材料库里无引用条目的删除。仍在材料库窗口做。
- 身份编辑器 `Func<>` 弹窗收口、`MainViewModel` 拆分。
- 启动时静默清理。

## 2. 清单项

固定七项，顺序如下。不读取 `sqlite_master` 动态发现表。`vault_metadata`、已在 v1 删除的 `credentials` / `folder_configs` 不出现。

| 项 | 主标题 | 条数 | 说明里点出的表 | 清理 |
|---|---|---|---|---|
| sessions | 会话 | `tree_nodes.node_type = 1`（`NodeType.Session`）。文件夹不算 | `tree_nodes`、`session_details`、`port_forwards`。后两张表不单列条数 | 无 |
| identities | 身份 | `identities` 行数 | `identities`。密文不在这张表 | 无 |
| secrets | 材料库 | `vault_secrets` 行数，不过滤 `deleted_at`（与材料库列表一致） | `vault_secrets` | 无。说明写明：无引用的条目在材料库窗口删除。设置页不打开材料库 |
| legacy-secrets | 旧身份密文 | 见第 3 节 | `identity_secrets` | 仅此项有按钮 |
| proxies | 代理 | `proxies` 行数。说明另给 `proxy_secrets` 行数 | 两张表 | 无 |
| known-hosts | 已知主机 | `known_hosts` 行数 | `known_hosts` | 无。说明指向现有已知主机窗口，设置页不打开它 |
| editors | 编辑器与关联 | `external_editors` 行数。说明另给路径表和关联表行数 | `external_editors`、`external_editor_paths`、`file_associations` | 无 |

条数都是整数。表不存在时该项条数为 0，不报错。

## 3. 旧身份密文

状态只看两份已有元数据，不新写标记：

- 完成标记：`vault_metadata[secret_library_migrated] == "1"`。
- 已迁移身份集合：`vault_metadata[secret_library_migrated_identities]`，JSON 字符串数组。键缺失、空、或 JSON 损坏时当作空集。损坏不能当成「全部可删」。

三种状态：

| 状态 | 条件 | 可删行 | 按钮 |
|---|---|---|---|
| 未开始 | 完成标记不是 `"1"`，且已迁移集合为空 | 无 | 禁用。说明：先解锁一次，让迁移跑完 |
| 部分完成 | 完成标记不是 `"1"`，且已迁移集合非空 | `identity_id` 能解析成 Guid 且属于已迁移集合的行 | 可删行大于 0 时可用。说明写明留下的是尚未迁走或解密失败的行，下次解锁还要重试 |
| 已完成 | 完成标记是 `"1"` | 该表全部行，含无法解析的 `identity_id` | 行数大于 0 时可用。说明：这些行已是材料库中的旧副本 |

可删行数为 0 时按钮禁用。状态说明仍按上表显示，不把「没有可清理的旧副本」当成另一种状态。部分完成但可删行数为 0 时，说明里仍写出留下的行数。不提供「丢掉这张表」。

清理规则在删除当时重判，不使用打开页面时缓存的行列表：

- 已完成：`DELETE FROM identity_secrets`。
- 否则：只删除身份 Id 落在已迁移集合内的行。集合外的行、无法解析的 Id 留下。
- 不修改 `vault_secrets`、`identities`、`vault_metadata`，不改完成标记，也不改已迁移集合。
- 不解密。因此不必为了清理而解锁。清单在保管库锁定时也能看、也能删已判定可删的行。
- 与材料库迁移共用 `InternalVaultManager` 的 `_secretWriteLock`。确认框期间不持有该锁，也不持有数据库事务。
- 单事务。失败则整批回滚，旧行仍在。
- 部分完成后再次解锁不会把已删行插回：迁移会跳过已迁移集合里的身份。完成后迁移直接返回，不再读旧表。

确认文案说明删的是已迁移的旧副本，不是材料库里正在用的密钥，并带上本次重判得到的可删行数。用户取消则不删除。确认之后、加锁之前如果又有新的可删行（例如期间迁移刚好完成），按加锁后的规则删除；完成标记一旦变成 `"1"`，剩余行都已是旧副本，全部删除是安全的。

## 4. 接口与界面

Core 新增只读报告和清理入口，不引入 UI 或 SQL：

- `DataInventoryItem`：稳定 Id（上表第一列）。本地化标题与说明由 App 层按 Id 取字符串。Core 给出 Id、主计数、`ExtraCounts`、`LegacyState`、`Cleanup`、可删行数。
- `ExtraCounts` 顺序与该项说明中的附加表一致：代理只有 `proxy_secrets`；编辑器先 `external_editor_paths` 再 `file_associations`；其余为空。
- `LegacySecretState`：`NotStarted` / `Partial` / `Complete`。只有 `legacy-secrets` 非空；其他项为 null，界面不显示迁移状态。
- `LegacySecretCleanup`：`None` / `MigratedRowsOnly` / `AllRows`。只有 `legacy-secrets` 不是 `None`。`NotStarted` 对应 `None`；`Partial` 对应 `MigratedRowsOnly`；`Complete` 对应 `AllRows`。
- `IDataInventory.LoadAsync()`：返回上述七项。
- `IDataInventory.PurgeLegacyIdentitySecretsAsync()`：按第 3 节删除，返回实际删除行数。调用方仍须先确认；接口本身不再弹窗。

实现放在 Infrastructure，经 `InternalVaultManager` 取写锁。仓储不新建连接类型；连接走 `SqliteConnectionFactory.OpenAsync`。

App：

- `SecuritySettingsPage` 增加子 ViewModel，不把清单逻辑堆进页面本体。安全页变为当前分类时加载；清理结束后无论成败都重新加载。
- 不新增 `Func<>` 弹窗属性。
- 确认不复用 `ConfirmDeleteAsync`：设置窗口那份实现把标题写死成移除主密码，主窗口那份直接返回 true。`IInteractionService` 新增 `ConfirmAsync(string title, string message)`。设置窗口弹出真确认框。主窗口同样弹出真确认框，避免以后从主窗口调用时静默放行。`NullInteractionService` 和测试空实现默认返回 `false`（与 `ConfirmDeleteAsync` 默认放行相反）。交互服务为 null，或确认返回 false：不调用清理。
- 文案进 `Strings.resx`。界面沿用安全页现有的 `settingsSection`，放在本机解锁那一节下面。清单是列表而不是第七张设置卡片堆叠：名称、说明、条数；只有旧身份密文一行有按钮。

## 5. 测试

测试必须能因生产删除范围出错而失败。至少：

- 已完成且旧表有行：清理删光 `identity_secrets`，`vault_secrets` 与完成标记不变。
- 部分完成：只删已迁移身份的行；被跳过身份的行还在，回退读取仍能读到它。
- 未开始：即使直接调用清理，删除行数也是 0，旧行还在。
- 已迁移集合 JSON 损坏且完成标记不是 `"1"`：删除行数是 0。
- 清单条数与插入的会话、文件夹（文件夹不计入会话）、身份、材料、代理、已知主机、编辑器一致。
- ViewModel：确认返回 false，或不提供交互服务时，不调用清理。确认返回 true 时调用一次，并刷新条数。

## 6. 文档

实现时把 `docs/design/06-identity-and-auth.md` 里「v9 清理旧表」改成：旧行由本清单在用户确认后删除；不自动 `DROP TABLE`。本规格不单独改那份设计文档。
