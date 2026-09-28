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
    Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default);
    Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default);
    Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default);
    Task CreateDirectoryAsync(string path, CancellationToken ct = default);
    Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default);
    Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default);
}
