# 文件传输进阶：完整性校验、目录归档传输与连接元数据

Status: resolved

## 最终验证

- `dotnet build Kei.Term.slnx`：0 警告、0 错误。
- `KEITERM_SSHD_TESTS=1 dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`：1145 通过、13 跳过、0 失败（总计 1158，含真实 sshd 的探测、校验、篡改重传、三种格式归档往返）。
- 界面经用户桌面验收；排序列表按反馈收紧为紧凑密度。
- 与 spec 的差异：
  - 会话级校验/归档覆盖在连接建立时取快照（`ResolvedSessionConfig.VerifyHashOverride` / `ArchiveDirectoriesOverride`），改会话后需重连生效。
  - 归档命令依赖 GNU tar / mv，`RemoteToolsets.ForArchives` 只对 Linux 返回工具集；Darwin 仅哈希可用。

术语以 `GLOSSARY.md` 为准；决策见 `docs/adr/0003-connection-metadata-and-remote-toolset.md`。前序能力见 `.scratch/file-transfers/spec.md`。

## 背景

现状（2026-10-18，`main@d8c608c`）：

- 单文件由 `FileTransferExecutor` 写 `<Name>.keipart`，只确认长度后改名，没有内容校验。
- 目录由 `SourceTreeReader` + `DirectoryTransferPlanner` 展开为逐文件子任务，小文件多时很慢。
- SFTP 专用传输连接只有 `SftpClient`，没有 exec 通道；SCP 端点有 `ExecuteCommandAsync`。
- `session_details.options_json` 存 `SessionOverrides`（用户意图），没有存放自动发现信息的地方。

## 目标行为

### 1. 完整性校验

- 时机：部分文件长度确认之后、改名之前。上传比对「本地源」与「远端部分文件」；下载比对「远端源」与「本地部分文件」。本地用 .NET 计算，远端执行探测到的命令。
- 一致：照常改名。不一致：**无论续传开关，删除部分文件**，抛 `TransferHashMismatchException`，归为暂时性失败，按重试设置从头重传。重试耗尽后为失败。
- 算法按配置顺序取探测结果中的第一个可用项。没有可用算法（平台未知/未实现/无 shell/全部缺失）时跳过校验，任务仍成功，任务详情注明「未校验」。
- 远端校验命令本身执行失败（非不一致）：按暂时性失败处理，部分文件保留与否遵循续传开关（它不是坏数据）。
- 执行时状态 `Verifying`，进度可不细化。

### 2. 目录归档传输

- 开启且探测到可用归档格式时，目录任务不再展开为逐文件子任务，而是整包作为一个任务：
  - 上传：本地按 `SourceTreeReader` 同样的取舍（跳过指向目录的软链等）用 `System.Formats.Tar` 生成归档到本地临时目录（状态 `Packing`），作为普通文件经 `FileTransferExecutor` 传到远端临时位置（可续传、可校验），远端解包（状态 `Unpacking`），删除远端与本地临时归档。
  - 下载：远端在 `mktemp` 临时位置打包（`Packing`），经执行器下载到本地临时目录（可续传、可校验），本地解包（`Unpacking`），清理两端临时文件。
  - 进度按归档字节计。保留修改时间与权限位（tar 自带）。
- 冲突整体判定（目标父目录下已有同名目录或文件）：
  - 覆盖：解包合并进目标（同名文件覆盖，多余文件保留）。
  - 跳过：不传。
  - 自动改名：解包到暂存目录后改名为 `AutoRename` 给出的新名。
  - 每次询问：只问一次（覆盖/跳过/改名）。
  - 仅当较旧时覆盖：对目录无意义，**退回逐文件模式**。
- 远端打包/解包必须能检测管线中任一环节失败（`/bin/sh` 不保证 `pipefail`；可两步落盘或另取退出码）。
- 本地解包安全：拒绝绝对路径、`..`、硬链接、设备文件；软链不得被后续条目穿透写入（先写普通文件再建软链，或拒绝指向目标树外的软链）。远端解包的是我们自己生成的归档，但解包目录仍须限定在目标父目录内。
- 任一归档环节失败：清理临时文件；暂时性失败按重试设置整包重试，不自动退回逐文件模式（避免半解包后再逐文件叠加）。
- 不可用时（关闭、无 shell、平台未实现、无可用格式、上面那条退回规则）：沿用现有逐文件模式。

### 3. 能力探测与连接元数据

- 每个连接惰性探测一次：第一个需要校验或归档的任务开始前。并发任务共享同一次在途探测。
- 探测内容：
  - 平台：POSIX 用 `uname -s` 识别为 `Linux` / `Darwin`，否则 `Unknown`。Windows 识别留扩展点，本次不实现（得到 `Unknown`）。
  - 按**配置的顺序**逐个检测哈希算法与归档格式是否可用，结果保存为按配置顺序排列的可用列表；首项即最佳方案。
- 保存：写入会话的 `session_details.metadata_json` 的 `transferProbe` 键（见 ADR-0003）。下次连接直接复用，不再探测。
- 用户改了全局顺序不自动失效（列表是可用集合；使用时仍按当前配置顺序挑第一个同时出现在列表里的项）。新加入顺序、但不在探测结果里的项视为不可用，直到清除后重新探测。
- 清除：会话编辑窗口「文件传输」分组显示当前探测结果（平台、可用算法、可用格式、探测时间），提供「清除并重新探测」按钮；删除 `transferProbe` 键，下次需要时重新探测。在线连接也立即生效（提供者每个任务从仓储读取，不做跨任务内存缓存）。
- 无 shell（exec 被拒、只开放 SFTP 子系统）：保存 `Platform = Unknown`、空列表，下次不再反复尝试，清除后才重试。
- 未保存的临时连接（无会话节点）：只在内存中保存，不落库。

### 4. 设置

`FileTransferSettings` 新增：

- `VerifyHashEnabled`：默认 `true`。
- `HashAlgorithmOrder`：`List<TransferHashAlgorithm>`，默认 `[Sha256, Sha1, Md5]`。不在列表中的算法视为禁用。
- `ArchiveDirectoriesEnabled`：默认 `false`（会改变目录冲突语义，需用户主动开启）。
- `ArchiveFormatOrder`：`List<TransferArchiveFormat>`，默认 `[Zstd, Gzip, Tar]`。

读取时去重、丢弃未知值；列表为空等价于该功能禁用。

`SessionOverrides` 新增 `bool? VerifyHashEnabled`、`bool? ArchiveDirectoriesEnabled`（null 继承全局）。顺序不做会话级覆盖。

### 5. 界面

- 设置 → 文件传输：校验开关 + 算法顺序列表（可勾选启用、上移/下移）；归档开关 + 格式顺序列表（同上），说明归档时目录冲突的整体语义。
- 会话编辑 → 文件传输：两个三态覆盖（继承/开/关）；探测结果只读展示 + 清除按钮。
- 传输看板：显示 `Packing` / `Verifying` / `Unpacking` 状态文字；已完成但未校验的任务有可见提示。

## 不做

- Windows / macOS 工具集实现（只留扩展点；Darwin 识别出来但工具集按 POSIX 复用与否由 ticket 01 决定，至少不能误报可用）。
- 校验结果持久化、跨重启续传。
- 会话级顺序覆盖。
- 同步：`metadata_json` 是本机缓存，不参与同步、不改 `revision`。

## 验收总览

- `dotnet build Kei.Term.slnx` 0 警告 0 错误。
- `dotnet test` 全绿；`KEITERM_SSHD_TESTS=1` 下真实 sshd 覆盖：探测、上传/下载校验成功、篡改远端部分文件导致不一致并重传、目录归档往返（zstd 不可用时退到 gzip）、归档冲突（覆盖/跳过/改名）。
