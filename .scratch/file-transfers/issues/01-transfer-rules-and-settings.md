# 01 传输规则与设置模型（Core 纯逻辑）

Status: resolved
Blocked by: (none)

见 `../spec.md`。只放 `Kei.Term.Core`，不引用 UI，也不引用 SSH.NET。

## 范围

- `FileTransferSettings` 新增 spec 中"设置项"一节列出的字段及默认值。旧设置文件缺这些字段时，读出默认值。
- 冲突策略枚举，以及冲突判定：输入源和目标的大小、修改时间，加上所选策略，输出覆盖 / 跳过 / 改名 / 需要询问。"目标较旧"允许 2 秒误差。
- 自动改名的命名函数：`name (1).ext`、`name (2).ext`……要处理无扩展名和点开头的文件；由调用方提供"该名字是否已存在"的判断。
- 续传判定：输入部分文件的长度、源文件大小、源文件修改时间是否变化、续传开关、协议是否支持续传，输出从哪个偏移开始传（0 即从头）。
- 失败分类：把异常映射成暂时性 / 永久性 / 无法判断。SSH.NET 异常类型不能进 Core，所以映射接口定义在 Core，SFTP 状态码到类别的具体映射在 ticket 02 实现；Core 这里只处理 .NET 自身的 IO、权限、套接字异常。
- 退避间隔计算：固定间隔 / 指数退避，带最大间隔。
- 目录递归的传输计划：输入一棵源树（抽象的条目：目录、文件、指向文件的链接、指向目录的链接），输出有序的操作列表（建目录、传文件、跳过并记录原因）。

## 验收

- 以上每个函数都有单元测试，测试要覆盖边界：退避第 1 次和超过最大间隔、2 秒误差的两侧、改名的多次碰撞、续传开关开/关和源文件变化、空目录、指向目录的符号链接。

## Comments

命名空间 `Kei.Term.Core.Transfers`（设置字段在 `Kei.Term.Core.Settings.FileTransferSettings`）。JSON 仍由 `JsonSettingsService` 的 System.Text.Json 反序列化：缺字段不覆盖属性初始值，没有额外的规范化步骤。枚举按数字读写，已发布取值不能重排。

### 设置

```csharp
public const int DefaultConcurrency = 3;
public const int MinConcurrency = 1;
public const int DefaultRetryCount = 3;
public const int DefaultInitialRetryIntervalSeconds = 2;
public const int DefaultMaxRetryIntervalSeconds = 60;

public int Concurrency { get; set; } = 3;                              // 不在 setter 夹紧
public string DefaultDownloadDirectory { get; set; } = "";             // 空 = 系统下载目录
public TransferConflictPolicy DefaultConflictPolicy { get; set; } = TransferConflictPolicy.Ask;
public bool ResumeEnabled { get; set; } = false;
public bool AutoRetryEnabled { get; set; } = true;
public int RetryCount { get; set; } = 3;                               // 不含首次传输
public RetryBackoffMode BackoffMode { get; set; } = RetryBackoffMode.Exponential;
public int InitialRetryIntervalSeconds { get; set; } = 2;
public int MaxRetryIntervalSeconds { get; set; } = 60;

public static int ClampConcurrency(int value);   // < 1 → 1，无上限
public int EffectiveConcurrency { get; }         // 读取点夹紧，不改字段
public string ResolveDownloadDirectory();
```

`TransferConflictPolicy.Ask = 4`，`RetryBackoffMode.Exponential = 1`。缺字段因此不会落到枚举 0（覆盖 / 固定间隔）。

### 冲突

```csharp
public enum TransferConflictPolicy { Overwrite = 0, Skip = 1, OverwriteIfOlder = 2, AutoRename = 3, Ask = 4 }
public enum ConflictResolution { Overwrite, Skip, Rename, NeedsPrompt }
public readonly record struct TransferFileFacts(long Size, DateTimeOffset ModifiedUtc);

public static class ConflictRules
{
    public static readonly TimeSpan OlderTolerance; // 2 秒
    public static ConflictResolution Resolve(TransferConflictPolicy policy, TransferFileFacts source, TransferFileFacts target);
}
```

`OverwriteIfOlder`：仅当 `target.ModifiedUtc < source.ModifiedUtc - 2s` 时覆盖；正好 2 秒或 ±2 秒内跳过。比较绝对时间。大小不参与。未定义枚举 → `NeedsPrompt`。

### 自动改名

```csharp
public static class AutoRename
{
    public static string NextAvailable(string fileName, Func<string, bool> exists);
    public static (string Stem, string Extension) SplitStemAndExtension(string fileName);
}
```

从 `(1)` 起，不返回原名。扩展名取最后一个点：`a.tar.gz` → `a.tar (1).gz`；`.bashrc` → `.bashrc (1)`；`.config.json` → `.config (1).json`。拒绝空名和路径。

### 续传与部分文件

```csharp
public readonly record struct ResumeDecision(long StartOffset, bool DeleteExistingPartial);

public static class ResumeRules
{
    public static ResumeDecision Decide(
        long? partialLength, long sourceSize, bool sourceModified, bool resumeEnabled, bool protocolSupportsResume);
}

public static class PartialFileNames
{
    public const string Suffix = ".keipart";
    public static string ToPartial(string fileName);                  // 可含路径：dir/a.txt → dir/a.txt.keipart
    public static bool IsPartial(string? fileName);
    public static bool TryGetOriginal(string? fileName, out string original);
}
```

续传条件：开关开、协议支持、源修改时间未变、且 `0 < partialLength < sourceSize`。否则偏移 0；`partialLength != null`（含 0）时 `DeleteExistingPartial = true`。负长度抛 `ArgumentOutOfRangeException`。后缀大小写敏感；单独的 `.keipart` 不是部分文件。

### 失败分类

```csharp
public enum TransferFailureKind { Transient, Permanent, Unknown }

public interface ITransferFailureClassifier
{
    // 取消不是失败：应抛 ArgumentException，不要返回 Unknown。
    TransferFailureKind Classify(Exception exception);
}

public sealed class DotNetTransferFailureClassifier : ITransferFailureClassifier
{
    public static DotNetTransferFailureClassifier Instance { get; }
    public TransferFailureKind Classify(Exception exception);
}
```

- 暂时性：`SocketException`、`TimeoutException`、包着二者的 `IOException`。
- 永久性：`UnauthorizedAccessException`、`FileNotFoundException`、`DirectoryNotFoundException`、`PathTooLongException`；磁盘满 `IOException`（HResult `28` / `0x80070070` / `0x80070027`）；占用（`0x80070020`、`0x80070021`，以及 Win32 码 `32`、`33`）。
- 其余（含普通 `IOException`）为 `Unknown`。`ShouldRetry` 只重试 `Transient`。
- `OperationCanceledException` / `TaskCanceledException`、包在 `IOException` 里的取消、只含取消的 `AggregateException` 抛 `ArgumentException`。SFTP 状态码映射留给 ticket 02，实现同一接口。

### 退避

```csharp
public enum RetryBackoffMode { Fixed = 0, Exponential = 1 }

public static class RetryBackoff
{
    public static TimeSpan Delay(int attempt, FileTransferSettings settings); // attempt 从 1 计，表示第几次重试
    public static bool ShouldRetry(TransferFailureKind classification, int attemptsSoFar, FileTransferSettings settings);
}
```

固定间隔 → 初始秒数（不看最大间隔）。指数 → `min(初始 × 2^(attempt-1), 最大)`，大 attempt 饱和不溢出。负的间隔秒数只在读取时当 0，不改字段。`attemptsSoFar` 含首次：首次失败为 1；重试次数 3 时 1、2、3 为 true，4 为 false。

### 目录计划

```csharp
public enum SourceEntryKind { Directory, File, SymlinkToFile, SymlinkToDirectory }
public sealed record SourceTreeEntry(string RelativePath, SourceEntryKind Kind, long Size = 0, DateTimeOffset ModifiedUtc = default);

public abstract record TransferPlanOp(string RelativePath)
{
    public sealed record CreateDirectory(string RelativePath, DateTimeOffset? ModifiedUtc) : TransferPlanOp(RelativePath);
    public sealed record TransferFile(string RelativePath, long Size, DateTimeOffset ModifiedUtc, bool FromSymlink) : TransferPlanOp(RelativePath);
    public sealed record Skip(string RelativePath, string Reason) : TransferPlanOp(RelativePath);
}

public static class DirectoryTransferPlanner
{
    public const string DirectorySymlinkSkipReason = "指向目录的符号链接，不进入";
    public const string UnderDirectorySymlinkSkipReason = "位于被跳过的目录符号链接之下";
    public static IReadOnlyList<TransferPlanOp> Plan(IEnumerable<SourceTreeEntry> entries);
    public static string NormalizeRelativePath(string relativePath);
}
```

相对路径规范成 `/`。缺的父目录会补 `CreateDirectory`，`ModifiedUtc == null` 表示没有源时间。空目录照样建。指向文件的链接 → `TransferFile(FromSymlink: true)`，大小和时间用条目上的目标内容事实。指向目录的链接跳过，其下条目也跳过，不建该目录。按路径分段排序，父目录在子项前。调用方必须放入目录本身；空列表得到空计划。重复路径、绝对路径、`..`、把文件当父目录会抛 `ArgumentException`。

### 下载目录

```csharp
public static class DownloadDirectoryResolver
{
    public const string FolderName = "Downloads";
    public static string Resolve(string? configured);
    public static string Resolve(string? configured, string? userProfile, string? userDirsText);
    public static string? TryParseXdgDownloadDir(string? userDirsText, string? userProfile);
}
```

空白配置：Linux 读 `~/.config/user-dirs.dirs` 的 `XDG_DOWNLOAD_DIR`（`$HOME` / `${HOME}`），否则 `UserProfile/Downloads`。Core 不做 P/Invoke。非空配置原样返回（去掉首尾空白），忽略 XDG。

### 与现行行为对齐（2026-10-17）

`ResumeRules` 仍只接收 `sourceModified` 布尔值。调用方必须把「没有可信历史」当成已变化：`PreviouslyRecordedSource == null` 不是未变化。大小相同不是校验和。`SourceEntryKind` 另有 `BrokenSymlink` 与 `IndeterminateSymlink`，计划分别以「符号链接已断开」和「无法确认符号链接目标，已跳过」跳过。代码已落地，最终回归尚未通过，Status 保持 `ready-for-agent`，不标 resolved。
