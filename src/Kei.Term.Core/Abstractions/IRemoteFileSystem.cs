namespace Kei.Term.Core.Abstractions;

using Kei.Term.Core.Models;

// 远程文件系统统一抽象接口
public interface IRemoteFileSystem : IAsyncDisposable
{
    bool IsConnected { get; }
    string WorkingDirectory { get; }

    // 是否支持从非零偏移读写（续传）。SCP 为 false，调用方据此从头传输。
    bool SupportsResume => false;

    Task ConnectAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);

    // 从指定偏移打开只读流。offset 为 0 时等同 OpenReadAsync(path)。
    // 不支持续传的实现在 offset > 0 时抛出 NotSupportedException。
    Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > 0)
        {
            throw new NotSupportedException("此文件系统不支持从偏移读取");
        }

        return OpenReadAsync(path, ct);
    }

    // 覆盖写入；调用方完成全部写入后必须提交，再报告成功。释放未提交的缓冲写流不应触发上传。
    Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default);

    // 从指定偏移打开写入流。offset 为 0 时创建并截断；大于 0 时打开已有文件并定位，不截断。
    // 不支持续传的实现在 offset > 0 时抛出 NotSupportedException。
    Task<Stream> OpenWriteAsync(string path, long offset, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > 0)
        {
            throw new NotSupportedException("此文件系统不支持从偏移写入");
        }

        return OpenWriteAsync(path, ct);
    }

    Task CommitWriteAsync(Stream stream, CancellationToken ct = default) => stream.FlushAsync(ct);
    Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default);

    // 与 DeleteAsync 不同：只删除一个文件、符号链接或空目录，不递归，也不跟随链接。
    // 传输清理必须继续调用 DeleteAsync，以保留其对符号链接的拒绝。
    // 默认转调 DeleteAsync；会递归或会拒绝链接的实现必须覆盖，不能回退成递归删除。
    Task DeleteEntryAsync(string path, bool isDirectory, CancellationToken ct = default)
        => DeleteAsync(path, isDirectory, ct);
    Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default);

    // 覆盖式改名。SFTP 优先 posix-rename@openssh.com。服务器没有该扩展且目标已存在时拒绝非原子覆盖，不删除任何一方。
    // 默认抛出，避免测试替身被静默当成支持覆盖。
    Task RenameOverwriteAsync(string oldPath, string newPath, CancellationToken ct = default)
        => throw new NotSupportedException("此文件系统不支持覆盖式改名");

    Task CreateDirectoryAsync(string path, CancellationToken ct = default);
    Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default);

    // 返回大小与 UTC 修改时间；路径不存在时返回 null。实现可以跟随符号链接以取得目标内容的事实。
    Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default);

    // 不跟随的属性。部分文件和目标目录的安全检查必须用这个，不能用会跟随的 GetItemAsync。
    // 默认退回 GetItemAsync，SFTP 必须覆盖成 lstat。
    Task<RemoteFileItem?> GetAttributesAsync(string path, CancellationToken ct = default)
        => GetItemAsync(path, ct);

    // 设置最后写入时间（UTC）。SFTP 写属性，SCP 经 exec 调用 touch。默认不支持。
    Task SetLastWriteTimeUtcAsync(string path, DateTime lastWriteTimeUtc, CancellationToken ct = default)
        => throw new NotSupportedException("此文件系统不支持设置修改时间");
}
