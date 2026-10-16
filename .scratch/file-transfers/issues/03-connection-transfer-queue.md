# 03 每个连接的传输队列，接入文件视图

Status: resolved
Blocked by: 01, 02

见 `../spec.md`。

## 范围

- 传输队列服务：每个连接一个，在连接挂上文件通道时创建，与连接同生同灭。
  - 并发数取自设置，超出的排队，按 FIFO 顺序开始。
  - 自动重试按 01 的分类和退避规则执行。
  - 支持取消单个任务、重试单个任务、重试全部失败项、清除已完成项。
  - 连接断开或重连时整体取消，并关闭专用传输连接。
- 冲突处理：遇到需要询问的冲突，只暂停当前任务，经 `IInteractionService` 新增的冲突询问方法问用户（参照 `docs/design/10-refactor-plan.md`，不往 ViewModel 上加 `Func<>` 属性）。"对本批次剩余的冲突都这样处理"的选择存在批次上，只对这一批生效。
- 文件视图接入：
  - 删掉 `RemoteFileManagerViewModel.Transfers.cs` 里直接执行传输的路径，改为把任务交给连接的队列。
  - 侧栏、文件标签、目录标签都显示同一个队列；目录标签不再各持一份 `TransferTasks`。
  - "下载"存到默认下载目录；"下载到…"经交互服务调用系统文件夹选择框。
  - 下载和拖入上传都支持目录、支持多选。
- SCP 大文件：收到 02 的警告信号后，经交互服务确认是否继续。
- `RemoteFileManagerViewModel` 已超过约 600 行，接入前先把传输相关职责移出（AGENTS.md 规定）。

## 验收

- 单元测试：
  - 并发上限：设为 3、提交 5 个任务时，同时只跑 3 个，其余按序开始。
  - 侧栏与目录标签看到同一个队列。
  - 连接关闭时队列取消，且不触发重试。
  - "对本批次剩余的冲突都这样处理"只在本批次内生效。
  - 暂时性失败会按次数重试，永久性失败不重试。
- 现有的 `RemoteFileEditingTests`、`TerminalFileManagerLifecycleTests`、`FileManagerWorkspaceTests` 全部通过。

## Comments

队列核心在 `Kei.Term.Core.Transfers.TransferQueue`。每个连接由 `TerminalTabViewModel` 持有一个 `ConnectionTransferBoard`，侧栏和目录标签共用它的 `Tasks`。连接关闭、断开、重连复位都走 `CloseFileManagerAsync`，会取消队列且不重试，并释放专用传输端点。

```csharp
public sealed class TransferQueue : IAsyncDisposable
{
    public int MaxConcurrency { get; } // SCP 固定 1，否则 FileTransferSettings.EffectiveConcurrency
    public IReadOnlyList<QueuedTransfer> Tasks { get; }
    public event Action? Changed;
    public QueuedTransfer Enqueue(TransferSubmission submission);
    public IReadOnlyList<QueuedTransfer> EnqueueMany(IReadOnlyList<TransferSubmission> submissions); // 同一次调用共享一个批次
    public void Cancel(Guid taskId);
    public void Retry(Guid taskId);
    public void RetryAllFailed();
    public void ClearFinished();
    public Task CloseAsync();
}

public sealed record TransferSubmission(TransferDirection Direction, string Name, string LocalPath, string RemotePath, bool IsDirectory, long Size = 0, DateTimeOffset ModifiedUtc = default);
public enum QueuedTransferState { Queued, Transferring, AwaitingConflict, RetryWait, Completed, Skipped, Failed, Cancelled }
```

等待用 `TimeProvider.CreateTimer`（.NET 10 的 `TimeProvider` 没有 `Delay`）。失败分类由调用方注入；生产用 `SshTransferFailureClassifier`。取消和跳过不是失败。冲突询问和 SCP 大文件确认是构造时传入的回调，只挂起当前文件并放开并发槽。`ApplyToRemaining` 记在批次上。

端点：`ITransferEndpoint`。SFTP 是 `ChannelTransferEndpoint`（`CreateTransferChannelAsync`，首次 Acquire 才连接）。SCP 是另一个 `FileSystemTransferEndpoint`，不和浏览共用，并发夹到 1。工厂失败时只记日志，浏览仍可用。

界面：`DownloadCommand` 存到 `ResolveDownloadDirectory()`；`DownloadToCommand` 经 `IInteractionService.PickFolderAsync`。拖入上传接受文件和目录。活动抽屉仍绑定 `TransferTasks`。重试命令在任务和 ViewModel 上（`Retry` / `RetryAllFailedTransfers`），抽屉模板还没放按钮，留给票据 04。冲突窗口是四个按钮加一个勾选，同样留给 04 重做。

不确定的地方：目录列举本身失败时直接标失败，不走自动退避；手动重试会重新展开。单个文件的暂时性失败会按次数退避。外部编辑回写和 `TerminalFileUploader` 没进队列。

### 审查修复（2026-10-17）

- 目标最终路径和 `.keipart` 一起原子预留。重叠任务等待，拿到排他后再查一次冲突。自动改名也走同一套预留。本地下载按平台区分大小写（Linux 区分，其余不区分，测试可强制）；远端路径区分大小写。`a` 与真实文件 `a.keipart` 互斥。
- 每次尝试重新读取源 size/mtime。`PreviouslyRecordedSource` 只放上一次尝试读到的事实；第一次为 null（无可信历史，交给执行器从头传）。源变化由执行器按 mtime（以及执行器侧即将加上的 size）决定重启。没有改 `FileTransferExecutor`。
- `FileSystemTransferEndpoint.AcquireAsync` 才连接，连接和关闭串行，重复释放无害。编排器用真实工厂结果包成这个端点，工厂本身不 Connect。
- 上传建目录先 `GetItemAsync`：已是目录则合并；是文件则失败，不吞掉 `CreateDirectory` 错误。
- 文件管理器初始化按代持有传输板，只在挂上时写入字段。关闭在 await 前摘下字段。迟到会话会释放传入的传输端点。
- `CloseAsync` 先取消并释放端点打断 IO，再排空。重复关闭等待同一次关闭。
- 生产路径不再把浏览通道当传输端点，也不再把协议硬编码成 SFTP。端点创建失败时 `ReportWarning`，浏览仍可用，上传/下载只报告原因。测试必须显式注入端点。
- 目录手动重试会取消并清掉上一代子项和进度，再重新展开。
- 退避等待按 24 小时分段创建计时器，不改用户配置的秒数。`TimeProvider.System` 配 `int.MaxValue` 秒可以创建并立即取消。

### 预留等待与续传世代（P1）

- `DestinationClaimGate.TryEnter` 在同一把锁里要么返回租约，要么返回当时的等待 `Task`。释放只会完成这次捕获到的任务；调用方 await 的是失败当下拿到的凭据，不会在释放之后再去取新的 pulse。
- 每条最终路径和 `.keipart` 有内存所有者世代。别的文件任务成功取得任一重叠路径时世代作废。重试只有世代仍匹配才把上次源事实放进 `PreviouslyRecordedSource`，否则传 null，执行器从头传。普通单任务自动重试世代不变，仍从部分文件偏移续。目录手动重试会换新的 `FileWork`，因此不会沿用上一代部分文件。外部改文件仍是已知 TOCTOU。

### 历史提交协议（P1）

- `file.Attempted` 表示已经打开过写流的部分文件属于哪个源，不是“这次准备传哪个源”。队列不得在调用执行器之前写入它。世代失效时先把 `Attempted` 清成 null。
- `FileTransferRequest.OnPartialWriteStarted` 只在旧部分文件已按续传决定删除、并且对应写流成功打开之后触发。参数 `PartialWriteStarted.Source` 是执行器 `ReadLiveSource` 刚读到的事实，不是队列早先的快照，也不是 `request.Source`。
- 没有这次回调，历史保持原样：路径检查或重置前失败不会把新源记进去。下一次只有世代仍匹配且 `Attempted` 非空，才把该事实放进 `PreviouslyRecordedSource`。
- 因此：源已变成 `BBBBBB` 但父路径检查在删除旧 `AA` 之前超时，再试必须从偏移 0 得到 `BBBBBB`，不能续成 `AABBBB`。首次路径检查失败没有确认历史，中途塞进的部分文件也不会被续上。普通部分写入失败仍从已写偏移续。队列读到旧快照、执行器随后读到新源时，提交的是执行器的现场事实。

### 与 spec / ADR 对齐（2026-10-17）

上文「审查修复」里「源变化只看 mtime、size 即将加上」和「SCP 用浏览文件系统」已被本节及后文取代：执行器比较现场大小和修改时间；SCP 是独立文件系统，懒连接，并发 1，创建失败不回退浏览。跨应用队列清空，旧部分文件无可信来源，不续传，不新增元数据。代码已落地，最终回归尚未通过，Status 保持 `ready-for-agent`，不标 resolved。
