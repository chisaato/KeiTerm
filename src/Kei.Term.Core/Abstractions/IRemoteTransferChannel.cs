namespace Kei.Term.Core.Abstractions;

using Kei.Term.Core.Transfers;

// 某个连接的专用传输通道（ADR-0002）。
// 与浏览用的文件通道分离：首次 Acquire 时才建立，无未归还租约且空闲满 60 秒后关闭，
// 连接掉线后下次 Acquire 懒重连。多个租约共享这一条连接，各自打开独立文件流；单个流只给一个任务用。
// 释放通道会关掉连接，并使未归还租约上的后续操作失败。Core 不暴露 SSH.NET 类型。
// App 层每个连接经 ISshSessionFactory.CreateTransferChannelAsync 创建一个，并传入活动会话以借用跳板链。
public interface IRemoteTransferChannel : IAsyncDisposable
{
    Task<IRemoteTransferLease> AcquireAsync(CancellationToken ct = default);

    // 没有 exec 的通道返回 null。默认实现让既有测试替身不用补方法。
    Task<IRemoteShell?> AcquireShellAsync(CancellationToken ct = default)
        => Task.FromResult<IRemoteShell?>(null);
}

// 传输通道上的一次租约。Dispose 只归还租约，不关闭连接。
// FileSystem 与同通道其他租约共享底层连接；不要释放 FileSystem（释放它不会归还租约，也不会关掉连接）。
public interface IRemoteTransferLease : IAsyncDisposable
{
    IRemoteFileSystem FileSystem { get; }
}
