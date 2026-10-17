# 03 目录归档传输（Core / Ssh）

Status: resolved
Blocked by: 01, 02

见 `../spec.md` §2。

## 范围
- Core 引入 `ZstdSharp.Port`（纯托管）用于本地 zstd；gzip 用 `System.IO.Compression`；tar 用 `System.Formats.Tar`。
- 新组件（不要继续撑大 `TransferQueue.cs`，它已 1000+ 行）：如 `DirectoryArchiveTransfer`，负责：
  - 本地打包（按 `SourceTreeReader` 的取舍，保留 mtime/mode）、本地安全解包（拒绝绝对路径、`..`、硬链接、设备文件、软链穿透）。
  - 远端打包/解包经 01 的工具集与 `IRemoteShell`，临时文件用 `mktemp`，任一环节失败可检测。
  - 归档文件本身经 `FileTransferExecutor` 传输（续传 + 02 的校验）。
  - 冲突整体判定：覆盖=合并解包；跳过；自动改名=暂存后改名；询问只问一次；「仅较旧时覆盖」→ 返回「退回逐文件」。
  - 状态 `Packing` / `Unpacking`；进度按归档字节。
  - 所有路径的清理（成功、失败、取消）。
- `TransferQueue`：目录根任务在「会话覆盖 ?? 全局」开启且挑得到格式时走归档组件，否则走现有逐文件路径。归档失败整包重试，不退回逐文件。

## 验收
- 单元：本地打包/解包往返（含空目录、软链、mtime）；恶意归档（`../x`、绝对路径、软链后接同名文件、硬链接）被拒；冲突四种分支；「较旧覆盖」退回逐文件；失败/取消后临时文件清理。
- sshd 集成：上传与下载目录往返内容与 mtime 一致；zstd 不可用（如 PATH 屏蔽或注入探测结果）时用 gzip；目标已存在时覆盖/跳过/改名。

## Comments

### 公开 API / 落地

- `LocalDirectoryArchive.PackAsync(source, archive, format, ct)` / `UnpackAsync(archive, destination, format, ct)`：PAX tar + `GZipStream` / 纯托管 `ZstdSharp.Port 0.8.7`；归档条目相对根为 `.`，不依赖源目录名与目标名一致。文件软链读取目标内容，目录软链/断链不打包，与 `SourceTreeReader` 一致。
- 安全解包拒绝绝对路径、盘符路径、`..`、硬链接、设备/FIFO、重复条目与软链祖先穿透；软链目标必须留在目标树内，并且最后创建。现有目标路径也用 `TransferPathGuard` 拒绝链接。mtime/mode 保留，mode 仅保留普通权限位。
- `DirectoryArchiveTransfer(FileTransferExecutor).ExecuteAsync(...)`：单次完整尝试的本地打包/安全解包、远端清单打包/解包、执行器传输与校验、暂存发布、两端清理。进度/总字节都是归档字节；回调发布 `Packing` / `Transferring` / `Verifying` / `Unpacking`。
- `TransferQueue` 新增末尾可选参数 `Func<bool?>? archiveDirectoriesOverride = null`；归档路由与重试放在独立的 `TransferQueue.Archives.cs`，未继续往原文件堆归档实现。整体处理覆盖（合并保留多余文件）、跳过、改名、单次询问；较旧覆盖直接退回逐文件；暂时失败重新打包整包重试，失败后不展开逐文件。等待冲突/声明/重试时释放并发槽。
- `IRemoteToolset` / `PosixToolset` 新增 `PackTreeCommand(format, sourceDirectory, archivePath, manifestPath)`（NUL 清单、禁止递归、文件软链解引用、硬链接转内容、PAX 时间精度）、`CreateTemporaryDirectoryCommand`、`DeleteTemporaryDirectoryCommand`、`PublishDirectoryCommand`。压缩与 tar 各自检查退出码、不用管道；中间 tar 在归档工作目录内，并用 trap 清理。远端解包到目标父目录内 `mktemp -d` 生成的私有目录，再检查目标路径和发布；自动改名用目录重命名。
- `RemoteToolsets.ForArchives(platform)`：本次归档依赖 GNU tar / mv，仅 Linux 可用；Darwin 仍可用原有 POSIX 哈希，但不会误报归档可用，旧缓存也被队列的格式工具集选择挡住。
- `SftpRemoteFileSystem.GetAttributesAsync`：父目录尚不存在时也返回 null，符合不跟随属性查询契约；否则首次归档发布的深层目标检查会误报失败。
- `ConnectionTransferBoard.Create(..., SessionOverrides? overrides = null)` 在 `TransferQueue` 构造处同时接入 `verifyHashOverride` 与 `archiveDirectoriesOverride`；保留连接创建时的覆盖对象，任务开始时读取，注释说明快照语义。

### 偏离 / 未做 / 主会话接线提醒

- **App 外层调用方仍需传 `overrides:`**：`TerminalTabViewModel.FileManager.cs` 的现有 `Create` 调用只有 `sessionId` / `metadata`，其初始化链也没有覆盖参数。按本任务写入边界，未改该文件或其他 App ViewModel；主会话 / ticket 04 必须把连接配置的 `SessionOverrides` 传到上述新参数，否则线上两个会话覆盖仍会继承全局。构造处已接好，不能仅添加参数后宣称线上覆盖已生效。
- 清理不沿用取消 token，远端清理最多等待 10 秒；连接已被销毁时只能尽力清理，不让其阻止本地清理或遮蔽原始失败。归档暂存目录的只读权限会在清理时恢复为当前用户可写。
- 归档仍复用支持续传的执行器及其校验路径，但整包重试创建新的工作目录、重新打包，不把旧包的部分文件当作新包的可信历史。
- 未修改其他 App XAML / ViewModel；未提交 git commit。

### 验证

- `dotnet build Kei.Term.slnx`：0 警告 / 0 错误。
- `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`：1113 通过 / 36 跳过 / 0 失败，总计 1149。
- `KEITERM_SSHD_TESTS=1 dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`：1136 通过 / 13 跳过 / 0 失败，总计 1149。
- 新测试覆盖三格式本地往返（空目录、文件/目录软链、mtime/mode）、恶意归档、整体冲突四分支、较旧覆盖回退、开关覆盖、上传/下载失败与取消清理、整包重试和状态、真实 sshd 三格式往返（含亚秒 mtime）与 gzip 退选。
- 中途真实 sshd 验证曾暴露并修复上述 SFTP 缺失父目录问题；另一次既有 `TransferQueueTests.TransientFailure_RetriesAfterDelay_PermanentDoesNot` 超时，未改该测试或生产逐文件行为，后续完整回归均通过。
