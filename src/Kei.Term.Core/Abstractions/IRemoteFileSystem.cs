namespace Kei.Term.Core.Abstractions;

using Kei.Term.Core.Models;

// 远程文件系统统一抽象接口
public interface IRemoteFileSystem : IAsyncDisposable
{
    bool IsConnected { get; }
    string WorkingDirectory { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);
    // 覆盖写入；调用方完成全部写入后必须提交，再报告成功。释放未提交的缓冲写流不应触发上传。
    Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default);
    Task CommitWriteAsync(Stream stream, CancellationToken ct = default) => stream.FlushAsync(ct);
    Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default);
    Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default);
    Task CreateDirectoryAsync(string path, CancellationToken ct = default);
    Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default);
    Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default);
}
