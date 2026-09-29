# 08 - 架构审查与后端优化记录（2026-09）

> 范围：`Kei.Term.Core` / `Kei.Term.Infrastructure` / `Kei.Term.Ssh` 以及 App 层中与后端强耦合的连接编排。
> 云端无桌面环境，本轮不涉及视觉/交互改动（仅补了两个菜单入口）。

---

## 1. 结论速览

| # | 问题 | 严重度 | 状态 |
|---|---|---|---|
| 1 | SSH 连接**不校验主机密钥**（SSH.NET 默认接受任意密钥），存在中间人风险 | 高（安全） | ✅ 已修复：信任库 + 策略 + 变更阻断（见 [07](./07-host-keys-and-connectivity.md)） |
| 2 | SCP 文件系统用双引号包裹远端路径，`$(...)` / 反引号仍会展开：恶意文件名可在服务器上执行命令 | 高（安全） | ✅ 已修复：POSIX 单引号转义 + `--`，真实 sshd 集成测试覆盖 |
| 3 | 设置页保存时整对象重建，未在页面上编辑的字段被重置为默认值（文件侧栏位置、轮询参数、UI 字号、外部编辑器路径） | 中（数据丢失） | ✅ 已修复 |
| 4 | 认证失败重试成功后，文件通道仍使用**首轮**（失败的）认证材料 → SFTP 侧栏必然连不上 | 中 | ✅ 已修复 |
| 5 | `KeepAliveIntervalSeconds` / `CustomAgentSocketPath` / `StartupScript` / `JumpHostSessionId` 只存不用 | 中 | ✅ 已接通（跳板链为完整实现） |
| 6 | 设置文件非原子写入；解析失败静默回退默认值，下次保存即永久覆盖用户配置 | 中 | ✅ 原子写 + 损坏文件备份 |
| 7 | 每个仓储各自 `CREATE TABLE` + 手工补列，Schema 分散在 4 个类；无版本号 | 中（可维护性） | ✅ 版本化迁移器（`PRAGMA user_version`） |
| 8 | 连接级 PRAGMA 在 4 处复制；未设 `busy_timeout`，后台写入与 UI 写入并发时可能 `SQLITE_BUSY` | 中 | ✅ `SqliteConnectionFactory` 统一 + WAL |
| 9 | `DateTime.Parse` 无文化/往返参数：UTC 时间被转换为本地时间（Kind=Local） | 低 | ✅ 统一 `RoundtripKind`，读出恒为 UTC |
| 10 | Vault 未记录 KDF 标识，未来切换 Argon2id 会导致旧库无法解锁 | 低（前瞻） | ✅ `vault_metadata.kdf` |
| 11 | `GetNodeByIdAsync` 全量加载后内存过滤 | 低 | ✅ 单行查询 |
| 12 | `MainViewModel` 约 2050 行，混合树管理、认证编排、连接重试、文件侧栏装配 | 中（可维护性） | ⏳ 拆分规划见 [10](./10-refactor-plan.md) |
| 13 | `EnableAgentForwarding` 设置项存在但 SSH.NET 不支持 agent 转发 | 低（误导） | ✅ 设置页已标注「暂不支持」并说明免密登录的真实来源，见 §6 |
| 16 | 测试集中混有空测试、只测 Mock、按源码文本断言、断言常量等无效测试 | 低（维护成本） | ✅ 已清理 24 个，见 §5 |
| 17 | 主机密钥协商未优先已记录的算法；`HostKeyName` 为签名算法（`rsa-sha2-512`）与记录的密钥类型（`ssh-rsa`）不一致 | 中（误报 / 漏判变更） | ✅ 已修复，sshd 三密钥集成测试覆盖 |
| 14 | `SftpChannelMode.Auto/Subsystem` 无法实现（SSH.NET 公共 API 不能在已有会话上开 sftp 子系统） | 低（误导） | ⏳ 已在代码注释说明；建议 UI 合并为单一选项 |
| 15 | README 将 OS Keyring、E2EE 同步、插件化凭据源写为既有特性 | 低（预期管理） | ✅ README 已标注「规划中」 |

---

## 2. 持久化：要不要上 ORM？

### 2.1 结论：引入 **Dapper**（micro-ORM），不引入 EF Core

本轮已落地：`Microsoft.Data.Sqlite` + `Dapper` + 自研版本化迁移器。

### 2.2 取舍依据

| 维度 | EF Core (Sqlite) | Dapper | 手写 ADO.NET（原状） |
|---|---|---|---|
| 样板代码 | 最少 | 少（参数/映射自动） | 多（逐列 `GetString(i)`，列序号易错） |
| 多态 JSON 列（`methods_json` 以 `$kind` 判别） | `ToJson()` 不支持多态，需自写 ValueConverter | 直接 `JsonSerializer` | 同左 |
| 启动开销 | 模型构建冷启动百毫秒级 | 可忽略 | 可忽略 |
| NativeAOT / 裁剪 | 支持有限（需预编译查询/模型，仍有实验性限制） | 反射映射；可升级 `Dapper.AOT` 源生成 | 完全兼容 |
| 迁移 | 自带，但需要设计时工具链；与现存的"旧 credentials 表重建"等手工迁移并存较别扭 | 需自研（已实现，约 100 行） | 同左 |
| 变更跟踪 | 有，但本项目树为"启动一次加载到内存 + 按节点保存"，基本用不上 | 无（不需要） | 无 |
| 对未来 E2EE 同步的友好度 | 需绕开跟踪器做增量导出/合并 | 显式 SQL，易写 `WHERE updated_at > @since`、软删除 | 同左 |

数据模型只有约 8 张表，访问模式是"全量加载 + 单行写"，EF Core 的主要收益（变更跟踪、LINQ 翻译、导航属性）在这里用不到，
反而要为多态 JSON、启动时间、AOT 付出成本。Dapper 解决了原代码真正的痛点——手写映射样板与列序号错位风险——且不改变"SQL 显式可见"的特性。

### 2.3 新的持久化结构

```
Kei.Term.Infrastructure/Storage/
├── SqliteConnectionFactory.cs     # 唯一开连接入口：foreign_keys=ON、busy_timeout=5000；SqliteValue 时间/Guid 转换
├── Schema/
│   ├── SchemaMigrator.cs          # PRAGMA user_version；每条迁移与版本号同事务提交；BEGIN IMMEDIATE 防并发双迁移
│   └── SchemaMigrations.cs        # 全库 Schema 唯一定义处：v1 基线（兼容存量库）、v2 known_hosts
├── SqliteTreeRepository.cs        # Dapper 行对象（snake_case 自动映射）→ 领域对象
├── SqliteIdentityRepository.cs
├── SqliteExternalEditorRepository.cs
└── SqliteKnownHostRepository.cs
```

约定：

- **新增表/列 = 追加一条版本号 +1 的迁移**；已发布的迁移永不修改。
- 仓储保留 `InitializeAsync()`（内部调用迁移器）以兼容既有调用；App 启动时统一 `SchemaMigrator.MigrateAsync(db)` 一次。
- 行对象（`private sealed class XxxRow`）与表列一一对应，领域对象不带任何持久化特性，Core 仍零 ORM 依赖。
- 时间列统一 ISO-8601 往返格式 TEXT；读出恒为 `DateTimeKind.Utc`。
- 日志 `journal_mode=WAL`：库文件位于 AppData，不建议放入网盘同步目录（WAL 的 `-wal/-shm` 伴生文件不适合被同步工具单独搬运）。

### 2.4 后续可选项

- 若将来启用 NativeAOT 发布，把 `Dapper` 换为 `Dapper.AOT`（同 API，源生成映射）。
- 启动 E2EE 同步前追加迁移：各表 `revision INTEGER` + `deleted_at TEXT`（软删除墓碑），见 [05](./05-cloud-sync-and-plugins.md) §4.1。
  注意：本轮已让"展开/折叠"不再刷新 `updated_at`，避免把视图状态当成内容变更参与同步合并。

---

## 3. SSH 层：统一拨号器

原先 `CreateSessionAsync` 与 `CreateFileSystemAsync` 各自复制了一套认证方法构建，且都直接 `new ConnectionInfo`，
导致超时、保活、Agent、主机密钥等横切关注点需要在多处重复（且实际上都没做）。本轮重构为：

```
SshConnectOptions ──► SshSessionFactory
                           │  SshAuthMethodBuilder（唯一的材料 → AuthenticationMethod 规则）
                           ▼
                       SshDialer ─── 直连 / 自有跳板链 / 借用终端会话的跳板链
                           │  HostKeyGate（逻辑地址校验）· KeepAlive · Timeout
             ┌─────────────┼──────────────┐
             ▼             ▼              ▼
       SshNetSession  SftpRemoteFS   ScpRemoteFS（scp + exec 两条连接共用一个转发口）
```

`ISshSessionFactory` 的参数由散列的 `connectTimeout / interactivePrompt` 收敛为 `SshConnectOptions`，
后续新增端口转发、代理、证书等选项不再改签名。

### 3.1 SSH.NET 的已知边界（决定了若干设计）

| 需求 | SSH.NET 2026.0 公共 API | 处理 |
|---|---|---|
| 在已有会话上开 sftp 子系统 | 不支持（`SftpClient` 只能自建会话） | 文件通道独立登录；经跳板时借用终端的跳板链，避免重复登录跳板 |
| 在已有通道上承载下一跳 SSH（真 ProxyJump） | 不支持 stream 连接 | 以 `ForwardedPortLocal(127.0.0.1:0)` 逐跳转发；转发口仅绑定回环并随会话释放 |
| Agent 转发（`-A`） | 不支持 | 设置项应标注暂不支持 |
| 会话环境变量（`env` 请求） | 未公开 | `EnvironmentVariables` 仍只存不用；可用登录脚本 `export` 过渡 |
| Local / Remote / Dynamic 端口转发 | 支持 | 端口转发管理器可直接基于 `SshNetSession` 的底层客户端实现（建议下一步） |

> 回环转发口的威胁模型：会话存续期间，本机其它进程可连接该临时端口到达目标主机的 SSH 端口（仍需目标认证）。
> 对单用户桌面可接受；如需更严，可向 SSH.NET 上游提交"基于 Stream 建连"的能力后替换。

---

## 4. 仍建议处理的结构问题

### 4.1 拆分 `MainViewModel`（优先）

完整的职责地图、目标类型、接口草案与执行顺序见 [10 - 长文件扫描与拆分规划](./10-refactor-plan.md)。

### 4.2 组合根

`App.axaml.cs` 手工 new 约 10 个对象，尚可维护；当服务数继续增长（端口转发、同步、录制）时，
可引入 `Microsoft.Extensions.DependencyInjection`（轻量、AOT 友好），无需更重的容器。

### 4.3 机密材料的内存形态

`SecretPayload` 使用 `string`，无法在用后清零。完全消除需要 SSH.NET 接受 `ReadOnlySpan<byte>` 私钥输入，
短期内可接受；已保证不落日志、不落明文文件。

### 4.4 KDF

PBKDF2-HMAC-SHA512 600k 迭代高于 OWASP 密码存储建议（SHA-512 为 21 万次）；若要回到规格中的 Argon2id，
可引入 `Konscious.Security.Cryptography.Argon2` 或 libsodium 绑定，写入新的 `kdf` 标识即可无损并存（本轮已预留）。

---

## 5. 测试集清理（2026-09-29）

原则：测试必须能因**生产行为出错**而失败。删除（265 → 241，其后新增至 249）：

| 类别 | 例子 |
|---|---|
| 不测任何生产代码 | 空的 `UnitTest1`；打印 API 的 `ApiInspectionTests`；只测自身 Mock 的 `RemoteFileSystemTests` |
| 锁死实现细节 | 用 `IndexOf` 断言 `.axaml.cs` 源码里方法调用顺序、`.axaml` 里 `ColumnSpacing="20"` 等字面量；反射私有字段 `_cursorBlinkTimer` |
| 永不失败 | `ConnectionStateBrushConverter` 测试（测试进程无 `Application`，恒返回灰色）；同一输入算两次比较相等 |
| 回显常量 | 默认前景色必须是 `#FFFFFF`、枚举整数值、预设名称清单 |
| 与 Theory 数据行重复 | `ToArgb` 六位/八位单例测试、空白字体名回退 |

保留：`Contracts/*` 下的 XAML 契约测试（裸色号、图标、令牌前缀）——这是 `.agents/skills/avalonia-contract-tests` 约定的防腐手段，断言的是规则而非字面布局。
合并：内置预设改为校验 **Id 唯一 + 颜色合法**；`TerminalProfileId` 透传并入 `SessionConfigBuilderTests`。

---

## 6. Agent 转发与 SSH 库选型

### 6.1 现状核实

- SSH.NET 2026.0 的程序集里不存在 `auth-agent-req@openssh.com`（agent 转发请求名）；对照 `pty-req`、`x11-req`、`keepalive@openssh.com` 均能找到，检索方法有效。
- 代码中 `EnableAgentForwarding` 只被设置页读写，连接路径从未使用。
- 勾选后能"免密登录常用服务器"来自 **本机 agent 认证**（`PreferSystemAgent`，默认开启），与转发无关。
  区分方法：登录后在远端执行 `ssh-add -l`——真正的转发会列出本机密钥，当前会提示 `Could not open a connection to your authentication agent`。

### 6.2 候选：Tmds.Ssh

| 能力 | SSH.NET 2026.0 | Tmds.Ssh 0.24.0（2026-08，MIT） |
|---|---|---|
| Agent 转发 | ❌ | 主干已合并（2026-09-17，PR #510），**尚未发版** |
| SFTP 复用同一连接 | ❌ | ✅ `SshClient.OpenSftpClientAsync` |
| 原生 ProxyJump（通道内承载，无需回环转发口） | ❌ | ✅ `SshProxy` |
| 会话环境变量 | ❌ | ✅ `ExecuteOptions.EnvironmentVariables` |
| 本地 / 远程 / SOCKS 转发 | ✅ | ✅ |
| 异步主机密钥回调 / known_hosts | 同步事件 | ✅ `HostAuthentication`（异步）+ 内建 known_hosts |
| keyboard-interactive（2FA） | ✅ | ✅ |
| 自动重连 | ❌ | ✅ `AutoReconnect` |
| SK 硬件密钥经 agent | ✅ 已在用 | ⚠️ 未验证（程序集中未见 `sk-*` 算法名，需实机测试） |
| API 稳定性 | 稳定 | 0.x，可能有破坏性变更 |

建议：**不整体迁移**，改为双引擎——默认 SSH.NET，会话开启 Agent 转发时用 Tmds.Ssh。已用主干构建实测可行，
方案、能力映射与 keyboard-interactive 缺口的处理见 [11](./11-dual-ssh-backend.md)。以下为最初的迁移评估，保留备查：本轮已把 SSH 相关代码收敛到 `Kei.Term.Ssh` 的 `SshDialer` / `SshAuthMethodBuilder` 与
`ISshSession` / `IRemoteFileSystem` 抽象之后，迁移范围限定在该项目内。spike 的通过条件：
YubiKey `ed25519-sk` 经 agent 登录成功、2FA 交互正常、跳板 + SFTP 复用正常、现有 sshd 集成测试全部通过。
