# 02 传输执行器与专用传输连接（Ssh / Infrastructure）

Status: resolved
Blocked by: 01

见 `../spec.md` 与 `docs/adr/0002-transfer-channel-and-queue.md`。

## 范围

- 扩展文件系统抽象，SFTP 要支持：
  - 从指定偏移打开写入流（续传用）。
  - 从指定偏移打开读取流。
  - 取文件大小和修改时间（可复用 `GetItemAsync`）。
  - 设置修改时间。
  - 覆盖式改名：优先 `posix-rename@openssh.com`，不支持时先删除再改名。
  - SCP 只需声明"不支持续传 / 不支持偏移"，调用方据此降级。
- 专用传输连接：每个连接一条，经 `SshDialer` 建立，复用这个连接的跳板链和认证材料（参照 `SshNetSession.ConnectUploadSftpAsync` 和 `SshSessionFactory.CreateFileSystemAsync`）。首次使用时才建立，空闲 60 秒后关闭，可被显式关闭。多个文件流可以在这条连接上同时进行，但一个流只给一个任务用。
- 单个文件的执行器：
  - 写部分文件 `.keipart`，按 01 的续传判定决定起始偏移，完成后确认大小、改成正式文件名、设置修改时间。
  - 报告进度和速度，支持取消。
  - 失败或取消时，按续传开关决定是否清理部分文件。
  - 本地和远程两个方向都要实现。
- 失败分类：把 SSH.NET 的异常和 SFTP 状态码映射到 01 定义的三类。
- 目录遍历：列出远程或本地的目录树，交给 01 的传输计划。
- SCP 路径：不走续传，单个文件超过 512 MiB 时给出警告信号，由上层负责提示。

## 验收

- 单元测试：用假的文件系统测部分文件的生命周期（成功后改名、开/关两种续传设置下取消和失败的清理）。
- sshd 集成测试（`KEITERM_SSHD_TESTS=1`）：
  - 目录往返后内容一致、修改时间被保留。
  - 写到一半中断，再续传，最终内容一致。
  - 覆盖式改名。
  - 一条传输连接上同时传 3 个流，内容都正确。
- 不改 `TerminalFileUploader`。

## Comments

前半已落地（文件系统原语 + 专用传输连接）。后半未做：单文件执行器、失败分类、目录遍历、SCP 512 MiB 警告。未改 `TerminalFileUploader`，也未动 `AppSettings` / `Core/Transfers`。

### 已完成的 API

`IRemoteFileSystem` 新增成员都有默认实现，现有测试替身不用改就能编译：

- `bool SupportsResume { get; }`：默认 `false`。SFTP 为 `true`，SCP 为 `false`。
- `Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct = default)`
- `Task<Stream> OpenWriteAsync(string path, long offset, CancellationToken ct = default)`
  - `offset < 0` 抛 `ArgumentOutOfRangeException`。
  - `offset == 0`：读 = 打开现有文件；写 = `FileMode.Create`（创建并截断）。
  - `offset > 0`：SFTP 用 `SftpClient.Open(path, FileMode.Open, access)` 再 `Seek(offset, SeekOrigin.Begin)`，不截断。SCP 抛 `NotSupportedException`。
- `Task SetLastWriteTimeUtcAsync(string path, DateTime lastWriteTimeUtc, CancellationToken ct = default)`
  - SFTP：`SftpClient.SetLastWriteTimeUtc`。`Kind` 为 Local 时先转 UTC，Unspecified 按 UTC 解释。
  - SCP：exec `/bin/sh -c`，路径经 `ShellQuote`。先 `touch -d @epoch`，失败再 `TZ=UTC touch -t yyyyMMddHHmm.ss`。
- `Task RenameOverwriteAsync(string oldPath, string newPath, CancellationToken ct = default)`
  - SFTP：`RenameFile(old, new, isPosix: true)`（SSH.NET 2026 → `posix-rename@openssh.com`）。扩展缺失时 `RequestPosixRename` 在发包前抛 `NotSupportedException`；服务器拒绝时为 `SftpException.StatusCode.OperationUnsupported`。这两种才退回「`DeleteFile` 目标（忽略 `SftpPathNotFoundException`）再 `RenameFile`」。其它失败不退回。
  - SCP：`mv -f --`。
- `GetItemAsync` 仍返回 `Size` 与 `LastModified`。SFTP 侧把 `LastWriteTimeUtc` 明确收成 UTC `DateTimeOffset`（offset 0）。

专用传输连接（ADR-0002，始终是独立 SFTP，与浏览协议无关）：

- `IRemoteTransferChannel.AcquireAsync(CancellationToken) -> IRemoteTransferLease`
- `IRemoteTransferLease.FileSystem`：与同通道其他租约共享一条 `SftpClient`，各自 `Open` 独立流。`Dispose` 只归还租约。不要释放 `FileSystem`（借用视图的 `DisposeAsync` 是空操作，不会归还租约，也不会关掉连接）。
- `ISshSessionFactory.CreateTransferChannelAsync(config, methods, activeSession, options, ct)`：默认实现抛 `NotSupportedException`，测试替身不用改。`SshSessionFactory` 用与 `CreateFileSystemAsync` 相同的 `CreateDialer`，`activeSession` 为 `SshNetSession` 时借用其跳板链。返回的通道尚未连接，首次 `Acquire` 才拨号。
- 实现类 `SftpTransferConnection`（internal）：拨号串行；空闲 = 无未归还租约；`IdleTimeout` = 60 秒，经 `TimeProvider.CreateTimer`，可注入。掉线后下次 `Acquire` 懒重连。`Dispose` 先取消正在进行的拨号，再关掉客户端；未归还租约的后续操作抛 `IOException`（「传输连接已关闭」/「传输连接已断开」）。

### 仍缺

下面四项已在「后半已落地」完成，留给 03 的接线见该节末尾。

- 单文件执行器：`.keipart`、按 01 的 `ResumeRules` 决定偏移、确认大小、`RenameOverwriteAsync`、`SetLastWriteTimeUtcAsync`、进度/速度/取消、两种续传设置下的清理。上传和下载都要。
- 失败分类：SSH.NET 异常与 SFTP 状态码映射到 01 的三类。租约在通道关闭后抛的是 `IOException`，可归入暂时性。
- 目录遍历，交给 01 的传输计划。
- SCP 路径不走续传；单文件超过 512 MiB 的警告信号。SCP 传输不要用这条 SFTP 通道，继续用 `ScpRemoteFileSystem`（`SupportsResume == false`）。
- 本轮不改 `TerminalFileUploader`。

### 后半已落地（执行器、失败分类、目录遍历）

执行器在 Core，只依赖 `IRemoteFileSystem` 和本地文件。协议由上层选：SFTP 用专用传输连接的租约，SCP 用浏览用的 `ScpRemoteFileSystem`。未改 `TerminalFileUploader`。

```csharp
public sealed record FileTransferRequest(
    string SourcePath,
    string DestinationDirectory,
    string DestinationFileName,
    FileTransferSettings Settings,          // 快照。执行器只用 ResumeEnabled
    TransferFileFacts Source,
    TransferFileFacts? PreviouslyRecordedSource = null, // null = 当作修改时间未变化
    IProgress<long>? Progress = null);      // 累计已落盘字节，含续传起点。速度由调用方按时间换算

public sealed class FileTransferExecutor
{
    public const int BufferSize = 64 * 1024;
    public FileTransferExecutor(ILogger? logger = null);
    public Task UploadAsync(FileTransferRequest request, IRemoteFileSystem remote, CancellationToken ct = default);
    public Task DownloadAsync(FileTransferRequest request, IRemoteFileSystem remote, CancellationToken ct = default);
    public static bool IsSourceModified(TransferFileFacts current, TransferFileFacts? previouslyRecorded);
}

public static class SourceTreeReader
{
    // 相对路径相对于传入的根，不包含根本身（空相对路径会被计划拒绝）。根目录由上层创建。
    public static Task<IReadOnlyList<SourceTreeEntry>> ReadRemoteAsync(IRemoteFileSystem fileSystem, string directory, CancellationToken ct = default);
    public static Task<IReadOnlyList<SourceTreeEntry>> ReadLocalAsync(string directory, CancellationToken ct = default);
}

public static class ScpTransferLimits
{
    public const long LargeFileBytes = 512L * 1024 * 1024; // 超过才警告，等于不警告
    public static bool NeedsLargeFileWarning(FileTransferProtocol protocol, long size);
}

public sealed class TransferLengthMismatchException : Exception
{
    public long Expected { get; }
    public long Actual { get; }
}

public sealed class SshTransferFailureClassifier : ITransferFailureClassifier
{
    public static SshTransferFailureClassifier Instance { get; }
}
```

执行流程：部分文件 `<目录>/<最终名>.keipart`（`PartialFileNames`）。用 `GetItemAsync` 或本地 `FileInfo` 取已有长度，交给 `ResumeRules.Decide`。协议是否支持续传看远程的 `SupportsResume`；本地一侧总是支持。`PreviouslyRecordedSource == null` 时 `sourceModified` 为 false；非空时只比较 `ModifiedUtc`，不用 2 秒容差。需要时删除旧部分文件，从偏移打开流，64 KiB 复制。远程写入结束后 `CommitWriteAsync`。长度不一致抛 `TransferLengthMismatchException`（`DotNetTransferFailureClassifier` 和 SSH 分类器都视为暂时性）。然后 `RenameOverwriteAsync` 或 `File.Move(overwrite: true)`，再设置 UTC 修改时间。失败或取消：`ResumeEnabled == false` 删除部分文件，`true` 保留；删除失败只记日志，文件不存在不记。取消原样抛 `OperationCanceledException`。下载前会 `Directory.CreateDirectory`。

`RemoteFileItem` 追加了可选字段，不填时与原来的构造一致。`IsDirectory` 仍是 lstat，不会因为链接目标是目录而变成 true：

- `IsSymbolicLink`
- `LinkTargetIsDirectory`
- `LinkTargetSize` / `LinkTargetModified`（指向文件时是目标内容的事实）

SFTP 跟随不用反射，也不用 `GetAttributes`（那是 LSTAT）。只对符号链接探测：`ListDirectoryAsync` 的 opendir 能开始枚举就是指向目录（读到一条就停）；否则 `Open(FileMode.Open, FileAccess.Read)` 能打开就是指向文件，长度取流的 `Length`。两种都失败是断开的链接（`IsBrokenSymbolicLink` / `SourceEntryKind.BrokenSymlink`），`DirectoryTransferPlanner` 以「符号链接已断开」跳过。探测异常只用于分支，取消仍向外抛。

失败分类（`SshTransferFailureClassifier`，其余交给 `DotNetTransferFailureClassifier.Instance`）：

- 暂时性：`SshConnectionException`（含握手失败、连接丢失）、`SshOperationTimeoutException`、`ProxyException`、`SftpException` 的 `ConnectionLost` / `NoConnection`、消息含 closed/rejected 的普通 `SshException`（通道被关、SCP exec 被拒）、租约抛出的「传输连接已关闭/已断开」。
- 永久性：`SftpPermissionDeniedException`、`SftpPathNotFoundException`、`SshAuthenticationException`。
- 无法判断：`StatusCode.Failure` 以及其他 SFTP 状态码。取消仍抛 `ArgumentException`。

### SCP 并发（给 03）

`ScpRemoteFileSystem` **不能被多个任务同时使用**。03 对 SCP 主机的传输并发数应固定为 1，不要复用 SFTP 那条「一条连接多个流」的模型。

- 一个实例只有一个 `ScpClient` 和一个 exec 用的 `SshClient`。
- 每次 Upload/Download 自建 exec 通道，但 `ScpClient` / `Session` 没有并发安全保证。
- `SshClient.CreateCommand` 会写共享的 `ConnectionInfo.Encoding`。列目录、改名、`touch`、删除并发会互相踩。
- 读写把整个文件放进内存，并发还会叠加内存。

### 仍留给 03

- 队列接线：SFTP 用 `CreateTransferChannelAsync` 的租约，SCP 用浏览文件系统，并按上面把 SCP 并发夹到 1。
- 512 MiB 警告由上层弹提示（`ScpTransferLimits.NeedsLargeFileWarning`）。
- 速度由字节进度和时间换算，执行器不算速度。
- 目录任务套用 `SourceTreeReader` + `DirectoryTransferPlanner`。根目录名由上层创建，遍历结果不含根本身。
- 不改 `TerminalFileUploader`。

### 安全修正（2026-10-17，覆盖原 spec 的两处）

审查确认的缺陷都在源码里成立，已按失败即保留数据修掉。原 spec「没有记录就当源没变」和「不支持 posix-rename 就先删目标再改名」会丢数据，主会话应改 spec。

1. 续传：`PreviouslyRecordedSource == null` 不再表示未变化。没有可信历史就不能续传，从 0 重传。开始前重新读取源的真实大小和修改时间；与快照的大小或修改时间任一不一致也从 0。大小相同不是校验，没有持久化元数据或校验和。跨重启无法验证的残留部分文件重传。
   - **给队列**：`PreviouslyRecordedSource` 必须是上一轮尝试开始时记下的源事实，不是本轮刚读到的当前源。首次传 null。重试传上一轮快照；执行器会再读现场，一致才续传。`request.Source` 只是本轮提示，不作为续传依据。当前队列已是这个约定（`history = file.Attempted`，然后 `Attempted = source`）。
2. `.keipart` 或最终名若是符号链接、重解析点或目录，拒绝跟随、删除和打开。本地还会拒绝目标目录路径上的符号链接。远程安全检查用列父目录的 lstat（`GetAttributesAsync` / `FindChild`），不用 `Get()`（它会先 realpath）。检查和打开之间仍有 TOCTOU，远程不能消掉，不能当作绝对安全。
3. 服务器没有 `posix-rename` 且目标已存在时，抛 `TransferAtomicOverwriteUnsupportedException`，不删除旧文件，也不改名。目标不存在才普通改名。长度已确认之后的发布失败不再按续传开关删除部分文件，异常原样抛出。
4. 符号链接两种探测都失败时，只有两边都明确不存在才记为断开。权限不足或笼统失败记为 `IndeterminateSymlink`，计划跳过，不读写目标。

新增异常都是永久性失败：`TransferPathRejectedException`、`TransferAtomicOverwriteUnsupportedException`。

### 覆盖上文过时句（2026-10-17）

以下以当前生产代码为准，覆盖上文「null = 修改时间未变化」「不支持 posix-rename 就先删目标再改名」「SCP 用浏览用的 `ScpRemoteFileSystem`」以及「队列先写 `Attempted = source`」。

- `PreviouslyRecordedSource == null` 是无可信历史，从 0 重传。非 null 才与开始前重读的源大小、修改时间比较；任一不一致从 0。大小相同不是校验和。不新增持久化元数据。
- `OnPartialWriteStarted` 在旧部分文件已按决定删除、并且对应写流成功打开之后触发，携带的是 `ReadLiveSource` 的现场事实。没有这次回调就不得更新历史。路径检查或重置前失败不能把本次源记进去。
- SFTP：`posix-rename` 缺失且目标已存在时抛 `TransferAtomicOverwriteUnsupportedException`，旧文件和已完成部分文件都保留。目标不存在才普通改名。长度确认后的发布失败不再按续传开关删除部分文件。
- 生产 SCP 端点是另一个 `FileSystemTransferEndpoint` 包住的 `ScpRemoteFileSystem`，Acquire 才连接，与浏览隔离，并发固定为 1。创建失败则传输不可用，不回退浏览通道。
- 链接和父路径保守拒绝。两种探测都明确不存在才是断开；权限或笼统失败是 `IndeterminateSymlink`，跳过。远程检查消不掉 TOCTOU。

代码已落地，最终回归尚未通过，Status 保持 `ready-for-agent`，不标 resolved。
