# 02 传输后完整性校验（Core / Ssh）

Status: resolved
Blocked by: 01

见 `../spec.md` §1。

## 范围
- `FileTransferRequest` 增加可选校验器（如 `ITransferHashVerifier? Verifier`，或 `TransferHashAlgorithm?` + `IRemoteShell?`），null = 不校验。校验器：本地哈希（`System.Security.Cryptography` 流式计算，支持取消）+ 远端哈希（`IRemoteShell` + 工具集）。
- `FileTransferExecutor`：长度确认后、`copyComplete = true` 之前执行校验：
  - 一致 → 继续改名。
  - 不一致 → 删除部分文件（不看续传开关），抛 `TransferHashMismatchException`。
  - 远端哈希命令失败/输出无法解析 → 抛可归为暂时性失败的异常，部分文件按续传开关处理。
- `TransferFailureClassification`：`TransferHashMismatchException` 为暂时性失败；重试时不得把旧源快照当续传历史（部分文件已删，确保从 0 开始）。
- `QueuedTransferState.Verifying`；`QueuedTransfer` 增加「未校验」标记（启用校验但无可用算法时为 true）。
- `TransferQueue`：根据「会话覆盖 ?? 全局」决定是否校验；需要时经 01 的 `ITransferCapabilityProvider` 取探测结果，按当前顺序挑算法，构造校验器传入请求。只改请求构造与状态发布，不动目录展开逻辑（03 会改）。

## 验收
- 单元（假文件系统 + 假 shell）：一致改名；不一致删部分文件并抛（续传开/关两种）；远端命令失败时部分文件保留规则；无可用算法时跳过且标记未校验；`Verifying` 状态发布顺序；不一致后重试从 0 重传。
- sshd 集成：上传/下载 sha256 校验通过；在执行器复制完成后篡改远端部分文件（测试钩子）→ 不一致 → 重试后成功。

## Comments

### 公开 API（`Kei.Term.Core.Transfers`）

- `interface ITransferHashVerifier`
  - `Task<string> HashLocalAsync(string path, CancellationToken cancellationToken = default)`
  - `Task<string> HashRemoteAsync(string path, CancellationToken cancellationToken = default)`
- `sealed class TransferHashVerifier(TransferHashAlgorithm algorithm, IRemoteShell shell, IRemoteToolset toolset)`：本地 `SHA256` / `SHA1` / `MD5` 流式计算；远端用工具集命令。退出码非 0 或输出解析失败抛 `TransferHashCommandException`。
- `sealed class TransferHashMismatchException(string localHex, string remoteHex)`：暂时性失败。
- `sealed class TransferHashCommandException(string message)`：暂时性失败。部分文件按续传开关保留。
- `FileTransferRequest` 末尾新增可选参数：`ITransferHashVerifier? Verifier = null`、`Action? OnVerifying = null`、`Func<CancellationToken, Task>? AfterCopy = null`。`Verifier == null` 不校验。`AfterCopy` 在长度确认之后、校验之前，供测试篡改部分文件。
- `QueuedTransferState` 末尾追加 `Packing`、`Verifying`、`Unpacking`（`Packing` / `Unpacking` 只占位，03 使用）。三者都算 `IsActive`，目录子任务聚合时也会冒泡到父任务。
- `QueuedTransfer.IsUnverified`：启用校验但没有可用算法 / 没有 shell / 没有探测提供者时为 true。`NoteUnverified` 会触发 `Updated`。`PrepareRetry` 清回 false。校验关闭或已经比对过为 false。

### 行为

- 执行器在长度确认后、`copyComplete = true` 之前校验。上传比对本地源与远端部分文件，下载比对远端源与本地部分文件。
- 不一致：不论续传开关，删除部分文件，抛 `TransferHashMismatchException`。
- 自动重试看到这个异常会丢掉 `FileWork.Attempted`，下一轮不能把旧源快照当续传历史。
- `TransferQueue` 用 `verifyHashOverride?.Invoke() ?? settings.VerifyHashEnabled`，再要求 `EffectiveHashAlgorithmOrder` 非空。然后 `Capabilities.GetAsync` → `SelectHash` → `AcquireShellAsync` → `new TransferHashVerifier`。目录展开没改。
- 构造参数：`Func<bool?>? verifyHashOverride = null`（null 继承全局），`Func<CancellationToken, Task>? afterCopy = null`（转给每个文件请求）。

### 未做 / 偏离

- 没改 App。`ConnectionTransferBoard.Create` 仍不传 `verifyHashOverride`，所以线上连接目前只看全局 `VerifyHashEnabled`（默认开）。会话覆盖要等调用方把 `() => overrides.VerifyHashEnabled` 传进 `TransferQueue`；04 若改看板，请加上这个参数，不要另起一套。
- `Packing` / `Unpacking` 没有任何生产路径会进入。
- 没有探测提供者（多数旧测试队列）且开关为开时，任务成功并标 `IsUnverified`，不探测。

验证：`dotnet build Kei.Term.slnx` 0 警告 0 错误。`dotnet test` 1087 通过 / 33 跳过 / 0 失败。`KEITERM_SSHD_TESTS=1 dotnet test` 1107 通过 / 13 跳过 / 0 失败（含上传篡改部分文件后重试成功，以及下载 sha256）。
