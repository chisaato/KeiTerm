# 01 能力探测、连接元数据与远端工具集（Core / Infrastructure / Ssh）

Status: resolved
Blocked by: —

见 `../spec.md` §3 §4、`docs/adr/0003-connection-metadata-and-remote-toolset.md`。

## 范围

### Core
- 枚举：`RemotePlatform { Unknown, Linux, Darwin, Windows }`、`TransferHashAlgorithm { Sha256, Sha1, Md5 }`、`TransferArchiveFormat { Zstd, Gzip, Tar }`。放 `Kei.Term.Core/Transfers/`（或新子目录 `Transfers/Capabilities/`）。
- `FileTransferSettings` 新增 `VerifyHashEnabled`(true)、`HashAlgorithmOrder`([Sha256,Sha1,Md5])、`ArchiveDirectoriesEnabled`(false)、`ArchiveFormatOrder`([Zstd,Gzip,Tar])，以及读取时去重/丢未知值的有效值访问器（参照 `EffectiveConcurrency`）。确认 `JsonSettingsService` 对缺字段旧文件回落到默认值、枚举序列化方式与现有一致。
- `SessionOverrides` 新增 `bool? VerifyHashEnabled`、`bool? ArchiveDirectoriesEnabled`。
- 连接元数据：`TransferProbeResult`（`Platform`、`IReadOnlyList<TransferHashAlgorithm> Hashes`、`IReadOnlyList<TransferArchiveFormat> Archives`、`DateTime ProbedAtUtc`），以及挑选函数：给定当前配置顺序，返回第一个同时在结果中的项（无则 null）。
- 抽象 `ISessionMetadataRepository`：`GetTransferProbeAsync(Guid sessionId)`、`SetTransferProbeAsync(Guid, TransferProbeResult)`、`ClearTransferProbeAsync(Guid)`。内部可泛化为按键读写，但对外强类型。
- 远端命令抽象 `IRemoteShell`：`Task<RemoteCommandResult> RunAsync(string command, CancellationToken)`（复用现有 `RemoteCommandResult`，若在 Ssh 层则上移到 Core）。
- `IRemoteToolset`（按平台选）+ `PosixToolset`：
  - 探测命令（`uname -s`；`command -v sha256sum || command -v shasum` 等；归档需要 `tar`，`Zstd` 另需 `zstd`，`Gzip` 另需 `gzip`）。
  - 哈希命令生成与输出解析（`sha256sum -- <path>` / `shasum -a 256 <path>`；解析首段十六进制，大小写不敏感）。
  - 打包/解包命令生成（供 ticket 03 使用；须能检测管线中任一环节失败，`/bin/sh` 无 `pipefail`）。
  - 路径一律用 POSIX 单引号转义；把 `ScpRemoteFileSystem.ShellQuote` 的实现上移到 Core（如 `PosixShell.Quote`），Ssh 侧改为调用它，行为不变。
  - Darwin：至少不能误报可用；能复用 POSIX 就复用（`shasum`）。Windows：返回不支持（工具集为 null），探测结果 `Unknown` + 空列表。
- `TransferCapabilityProbe`：用 `IRemoteShell` + 工具集，按**当前配置顺序**检测并返回 `TransferProbeResult`（列表按配置顺序）。exec 被拒/失败 → `Unknown` + 空列表（不抛）。
- `ITransferCapabilityProvider`（每个连接一个）：`GetAsync(ct)`：仓储有结果直接用；否则探测并写入。并发调用共享同一次在途探测。每次调用都读仓储（不跨任务缓存，保证 UI 清除立即生效）。无会话节点的临时连接仅内存保存。

### Infrastructure
- `SchemaMigrations.cs` 追加 v8：`session_details` 加 `metadata_json TEXT`（`AddColumnIfMissingAsync`）。
- `SqliteSessionMetadataRepository`：经 `SqliteConnectionFactory.OpenAsync`；写用 `UPDATE session_details SET metadata_json = json_set(COALESCE(metadata_json,'{}'), '$.transferProbe', json(@v)) WHERE node_id = @id`，清除用 `json_remove`；时间按 `SqliteValue.FormatUtc/ParseUtc` 语义（JSON 内字符串）。损坏 JSON 读取时当作无结果。不改 `revision`。
- 确认 `SqliteTreeRepository` 的会话 upsert 不触碰 `metadata_json`（当前是列出列的 `ON CONFLICT DO UPDATE`，保持如此）。同步导出（如有）不包含该列。
- DI 注册。

### Ssh
- SFTP 专用传输端点提供 `IRemoteShell`：经同一 `SshDialer` 懒建一个 `SshClient`（与 SFTP 连接同生命周期/空闲策略，或独立懒连接，二选一并注释理由）。SCP 端点复用现有 `ExecuteCommandAsync`。
- `ITransferEndpoint` 增加获取 shell 的方式（如 `Task<IRemoteShell?> AcquireShellAsync(ct)`，不可用返回 null）。为每个连接的传输队列构造 `ITransferCapabilityProvider`（需要会话 Id 与设置）。

## 验收
- 单元：设置有效值（去重、未知值、空列表）；挑选函数；`PosixToolset` 命令生成（含带 `'`、空格、`-` 开头的路径）与哈希输出解析；探测在假 shell 上按配置顺序产出列表、exec 失败得到空结果；提供者并发只探测一次、清除后重新探测。
- 仓储：迁移 v8 测试（`SqliteMigrationTests` 风格）；set/get/clear 往返；保存会话（改 overrides）后 `metadata_json` 不变；删除会话级联。
- sshd 集成（`[SshdFact]`）：真实主机探测出 `Linux` 与 sha256 / 至少 gzip+tar。
- `ShellQuote` 现有测试仍通过。

## Comments

### 公开 API

命名空间 `Kei.Term.Core.Transfers`：

- `enum RemotePlatform { Unknown, Linux, Darwin, Windows }`
- `enum TransferHashAlgorithm { Sha256, Sha1, Md5 }`
- `enum TransferArchiveFormat { Zstd, Gzip, Tar }`
- `sealed record RemoteCommandResult(string Output, string Error, int? ExitStatus, string? ExitSignal = null)`（从 Ssh 上移）
- `interface IRemoteShell { Task<RemoteCommandResult> RunAsync(string command, CancellationToken cancellationToken = default); }`
- `static class PosixShell { static string Quote(string value); }`
- `sealed class TransferProbeResult`：`Platform`、`Hashes`、`Archives`、`ProbedAtUtc`；`SelectHash(IEnumerable<TransferHashAlgorithm>)` / `SelectArchive(...)` 按当前顺序挑第一项，没有则 null；`Unavailable(TimeProvider)`
- `interface IRemoteToolset`：`PlatformProbeCommand`、`ParsePlatform`、`HashProbeCommand`、`HashCommand`、`TryParseHash`、`ArchiveProbeCommand`、`PackCommand`、`UnpackCommand`
- `sealed class PosixToolset`（`Instance`）+ `static class RemoteToolsets { static IRemoteToolset? For(RemotePlatform); }`
- `static class TransferCapabilityProbe { static Task<TransferProbeResult> ProbeAsync(IRemoteShell, FileTransferSettings, TimeProvider? = null, CancellationToken = default); }`
- `interface ITransferCapabilityProvider { Task<TransferProbeResult> GetAsync(CancellationToken cancellationToken = default); }`
- `sealed class TransferCapabilityProvider(Guid? sessionId, ISessionMetadataRepository? repository, Func<FileTransferSettings> settings, Func<CancellationToken, Task<IRemoteShell?>> acquireShell, TimeProvider? time = null)`

`Kei.Term.Core.Settings.FileTransferSettings` 新增 `VerifyHashEnabled`、`HashAlgorithmOrder`、`ArchiveDirectoriesEnabled`、`ArchiveFormatOrder`，以及 `EffectiveHashAlgorithmOrder`、`EffectiveArchiveFormatOrder`、`EffectiveVerifyHashEnabled`、`EffectiveArchiveDirectoriesEnabled`（空列表视为该功能关闭）。枚举仍按数字写入设置 JSON。

`Kei.Term.Core.Models.SessionOverrides` 新增 `bool? VerifyHashEnabled`、`bool? ArchiveDirectoriesEnabled`。

`Kei.Term.Core.Storage.ISessionMetadataRepository`：

- `Task<TransferProbeResult?> GetTransferProbeAsync(Guid sessionId, CancellationToken cancellationToken = default)`
- `Task<bool> SetTransferProbeAsync(Guid sessionId, TransferProbeResult result, CancellationToken cancellationToken = default)` — 没有 `session_details` 行时返回 false
- `Task ClearTransferProbeAsync(Guid sessionId, CancellationToken cancellationToken = default)`

实现：`Kei.Term.Infrastructure.Storage.SqliteSessionMetadataRepository`。迁移 v8 `session_metadata` 给 `session_details` 加 `metadata_json`。

`ITransferEndpoint` / `IRemoteTransferChannel` 新增 `Task<IRemoteShell?> AcquireShellAsync(CancellationToken ct = default)`，默认返回 null。

### TransferQueue 如何拿到这三样

`TransferQueue` 构造函数末尾增加可选参数 `ITransferCapabilityProvider? capabilities`，暴露为 `Capabilities`。调度循环还没调用它（校验是 02，目录展开是 03）。

生产路径：`App.axaml.cs` 注册 `SqliteSessionMetadataRepository`，经 `MainViewModel` → `TerminalTabConnectionTarget` → `TerminalTabViewModel.InitializeFileManagerAsync` 传到 `ConnectionTransferBoard.Create`。看板用 `session.SessionId`、当前 `FileTransferSettings` 和 `endpoint.AcquireShellAsync` 构造 `TransferCapabilityProvider` 并交给队列。

- SFTP：`ChannelTransferEndpoint.AcquireShellAsync` 转到 `SftpTransferConnection`。后者用同一 `SshDialer` 懒建独立 `SshClient`（`DialerCommandShell`），不跟 SFTP 60 秒空闲计时走，也不挂端口转发。
- SCP：`FileSystemTransferEndpoint.AcquireShellAsync` 先连接，再把实现了 `IRemoteShell` 的 `ScpRemoteFileSystem` 交出去（`RunAsync` 即原 `ExecuteCommandAsync`）。浏览用的 SFTP 文件系统返回 null。
- 无 shell、exec 失败：探测结果为 `Unknown` + 空列表并保存，不再反复试。`Guid.Empty` 或 `Set` 返回 false（未保存的临时连接）只留在该提供者的内存里。有会话行时每次 `GetAsync` 都先读仓储。

### 未做 / 偏离

- 没有改 `FileTransferExecutor`，也没有改 `TransferQueue` 的目录展开。设置页 / 会话编辑 UI 未做（ticket 04）。队列只是持有提供者。
- `SetTransferProbeAsync` 返回 `bool`，用来区分「写进了会话行」和「没有行、改走内存」。票据只列了方法名。
- Darwin 复用 `PosixToolset`（仍 `command -v`，不假装工具存在）。Windows 的 `RemoteToolsets.For` 为 null；`uname` 认不出时探测结果是 `Unknown` + 空列表，不会把 `Platform` 记成 `Windows`。
- 哈希命令是 `sha256sum` / `shasum`（以及 md5 对应工具）的 `if/elif`，探测结果不记二进制名。打包/解包用 `mktemp` 两步落盘和显式退出码，不用管道。
- 仓库里没有同步导出实现；`metadata_json` 不在任何导出查询里。`SqliteTreeRepository` 的 upsert 仍然不列出这一列，保存会话不改它，也不改 `revision`。
- 清除与正在进行的探测赛跑时，探测结束仍会把结果写回去；下一次清除仍然有效。
- 为让 `dotnet build` 0 警告，把 `MainWindowLayoutModeTests` 里过时的 `Bitmap.Save(path)` 换成 `PngBitmapEncoderOptions.Default`。与本票据无关。

验证：`dotnet build Kei.Term.slnx` 0 警告 0 错误。`dotnet test` 1075 通过 / 32 跳过 / 0 失败。`KEITERM_SSHD_TESTS=1 dotnet test` 1094 通过 / 13 跳过 / 0 失败（含真实 sshd 探测）。
