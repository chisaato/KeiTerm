# 08 自动触发与状态

Status: resolved
Blocked by: 03

见 `../spec.md`「触发与界面」。

**要做什么：** 用户启用同步后不需要再手动点击：启动时、修改后、定时都会自动同步，同步在后台进行、不阻塞界面，状态始终可见。

- [x] 应用启动且保险库解锁后同步一次。
- [x] 任何同步实体发生本地修改后，防抖 30 秒再同步。
- [x] 运行期间每 5 分钟同步一次。
- [x] 同一时刻只运行一轮；运行中再次触发时合并为下一轮。
- [x] 保险库锁定时暂停并显示「保险库已锁定，同步暂停」，解锁后自动补跑一轮。
- [x] 同步失败时状态显示可读原因（网络、凭据、口令、同步包损坏、需要确认），本机数据不变；下一个触发点照常重试。
- [x] 同步状态在同步页可见；是否在主窗口加一个轻量状态指示，由 @designer 决定。
- [x] 测试用可控时钟与测试同步位置覆盖：防抖、定时、单轮串行与合并触发、锁定暂停与解锁补跑。

## Comments

### 公开 API

- `ISyncClock` / `SystemSyncClock`：`UtcNow` 与可取消的 `DelayAsync`。调度器只通过它等待，测试注入手动时钟。
- `ISyncChangeSignal` / `SyncChangeSignal`：`Publish()`、`Changed`、`Suppress()`。抑制标志走 `AsyncLocal`，包住整轮 `SyncAsync`，导入和墓碑清理都不会再预约同步。
- `ISyncLocationSource`：`HasConfiguration`（不读保险库，可在锁内调用）与 `OpenAsync`。App 的 `FileSyncLocationSource` 读取同步页已经保存的位置配置，并用现有 `SyncLocationFactory` 打开；WebDAV / S3 密钥仍从保险库取。
- `SyncScheduler`：`Start()`、`NotifyVault(bool)`、`SyncNowAsync()`、`Status`、`StatusChanged`、`Synchronized`。常量 `Debounce`（30 秒）、`Interval`（5 分钟）、`VaultLockedMessage`（「保险库已锁定，同步暂停」）。
- 本地修改信号接在 `SqliteConnectionFactory.OpenAsync`：`sqlite3_trace` 只把含 `updated_at` 或 `local_entity_stamps` 的 INSERT/UPDATE/DELETE 当成内容修改。展开/折叠只改 `is_expanded`，不触发。`local_vault_secrets` 不触发。

### 行为

- 启动且保险库已解锁、位置和口令都已配置时，立即同步一轮，之后每 5 分钟一轮。未配置（无位置或无口令）时自动触发直接跳过，不改状态、不调用协调器。
- 空闲时的本地修改把截止时间重置为 30 秒后。正在同步时，本地修改、手动点击和到期的定时都并成「本轮结束后再跑恰好一轮」，不再并行，也不把同一次修改排成多轮。
- 手动「立即同步」仍先由同步页保存位置配置，再进入调度器。空闲时马上跑；运行中则等待那一轮补跑的结果。
- 保险库锁定且已经配置了位置时，状态固定为「保险库已锁定，同步暂停」，定时和防抖都不跑。解锁后只补一轮，并把下一次定时从当前时间重新计算，不把锁定期间错过的间隔连跑多次。接入的是现有 `VaultLockStateChangedMessage`（`VaultSessionService` 锁定、密码解锁、快速解锁都会发）。启动时先 `TryAutoUnlockAsync`，再 `Start()`，所以自动解锁不必再发消息。
- 失败状态沿用协调器的 `SyncResult.Message`（口令、同步包、网络等可读原因），格式仍是「本地时间：结果」加最后写入设备。下一触发点照常重试。真正执行过的一轮，无论成败，都走原来的 `Synchronized` → `ReloadTreeAsync`。跳过的未配置触发不刷新树。
- 同步页继续用原来的 `Status` 文本块，并写回 `sync-status.txt`。后台线程的状态和树刷新会切回 UI 线程。自动同步不置 `IsBusy`，不挡住设置页。

### 未做 / 偏离

- 主窗口状态指示没有做，留给 @designer。同步页位置表单也没有改布局；06/07 的工厂已经落地，所以位置来源直接用它，而不是再读旧的 `sync-location.txt`。
- 运行中再次触发时，补跑在当前轮结束后立即开始，不再额外等 30 秒。30 秒防抖只用于空闲时的连续修改。
- 状态行的时间用注入时钟的本地时间，便于测试；以前手动同步用的是 `DateTime.Now`。
- 协调器自己的信号量还在。调度器保证同一时刻只发出一轮，信号量只是兜底。

### 验证

- `dotnet build Kei.Term.slnx`：0 错误 / 0 新警告。仅既有 `TerminalProfileApplicationTests.cs:948` CS8602。
- `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter FullyQualifiedName~SyncSchedulerTests`：9 通过 / 0 失败 / 0 跳过。覆盖防抖及重置、每 5 分钟、运行中合并为恰好一轮、锁定暂停与解锁补跑、未配置不跑、缺口令不自动跑、失败后下次重试、导入写回不循环、展开折叠不触发、其后的用户保存会触发。
- 全量 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`：1547 通过 / 40 跳过 / 1 失败。失败是 `S3ConditionalHeaderTests.IfMatchIsTheReadEtagAnd412Or409Conflicts`（`SyncConditionalHeaderTests.cs:73`，读回内容不是刚写入的字节）。该测试和 `S3SyncLocation` 属于并行的 06/07，本工单没有改这些文件。跳过项仍是平台或外部依赖条件测试。没有 commit。
