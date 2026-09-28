namespace Kei.Term.Core.Models;

// 文件传输协议类型
public enum FileTransferProtocol
{
    Sftp = 0,
    Scp = 1
}

// SFTP 通道连接模式
public enum SftpChannelMode
{
    // 智能模式：优先复用终端物理 SSH 会话开启 subsystem，若被拒或无会话则回退到独立连接
    Auto = 0,
    // 子系统模式：严格复用当前终端 SSH 会话物理连接，不开启新 TCP
    Subsystem = 1,
    // 独立模式：始终建立全新独立的 TCP + SSH 连接
    Dedicated = 2
}

// 本地文件监视模式（应对某些 Linux inotify 配额受限环境）
public enum FileWatcherMode
{
    // 智能模式：优先 OS 原生事件通知，若环境不支持或异常则自动降级为轻量轮询
    Auto = 0,
    // 原生模式：严格依赖操作系统原生事件（Linux inotify / Windows ReadDirectoryChangesW / macOS kqueue）
    OSNative = 1,
    // 纯轮询模式：低开销定时读取元数据与时间戳
    Polling = 2
}

// 远程文件元数据条目
public record RemoteFileItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastModified,
    string Permissions,
    int UserId = 0,
    int GroupId = 0);

// 传输任务状态
public enum FileTransferStatus
{
    Queued,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

// 传输方向
public enum TransferDirection
{
    Upload,
    Download
}

// 传输进度报告数据
public record FileTransferProgress(
    string TaskId,
    string FileName,
    TransferDirection Direction,
    long TransferredBytes,
    long TotalBytes,
    double SpeedBytesPerSecond,
    FileTransferStatus Status,
    string? ErrorMessage = null);
