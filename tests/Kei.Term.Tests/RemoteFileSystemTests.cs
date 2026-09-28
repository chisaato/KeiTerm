using System.IO;
using System.Text;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Xunit;

namespace Kei.Term.Tests;

public class MockRemoteFileSystem : IRemoteFileSystem
{
    private readonly Dictionary<string, byte[]> _storage = new(StringComparer.Ordinal);
    public bool IsConnected { get; private set; } = true;
    public string WorkingDirectory { get; set; } = "/home/user";

    public Task ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        var list = new List<RemoteFileItem>();
        foreach (var kvp in _storage)
        {
            if (kvp.Key.StartsWith(path.TrimEnd('/') + "/"))
            {
                string name = Path.GetFileName(kvp.Key);
                list.Add(new RemoteFileItem(name, kvp.Key, false, kvp.Value.Length, DateTimeOffset.UtcNow, "-rw-r--r--"));
            }
        }
        return Task.FromResult<IReadOnlyList<RemoteFileItem>>(list);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        if (_storage.TryGetValue(path, out var bytes))
        {
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }
        throw new FileNotFoundException(path);
    }

    public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
    {
        var ms = new TrackingMemoryStream((bytes) => _storage[path] = bytes);
        return Task.FromResult<Stream>(ms);
    }

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
    {
        _storage.Remove(path);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        if (_storage.Remove(oldPath, out var bytes))
        {
            _storage[newPath] = bytes;
        }
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;

    public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
    {
        if (_storage.TryGetValue(path, out var bytes))
        {
            return Task.FromResult<RemoteFileItem?>(new RemoteFileItem(Path.GetFileName(path), path, false, bytes.Length, DateTimeOffset.UtcNow, "-rw-r--r--"));
        }
        return Task.FromResult<RemoteFileItem?>(null);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    private class TrackingMemoryStream : MemoryStream
    {
        private readonly Action<byte[]> _onClose;
        public TrackingMemoryStream(Action<byte[]> onClose) => _onClose = onClose;
        protected override void Dispose(bool disposing)
        {
            if (disposing) _onClose(ToArray());
            base.Dispose(disposing);
        }
    }
}

public class RemoteFileSystemTests
{
    [Fact]
    public async Task MockFileSystem_UploadAndDownload_Success()
    {
        var fs = new MockRemoteFileSystem();
        string testPath = "/home/user/test.txt";
        byte[] data = Encoding.UTF8.GetBytes("hello sftp");

        // Write
        await using (var writeStream = await fs.OpenWriteAsync(testPath))
        {
            await writeStream.WriteAsync(data);
        }

        // Read
        await using (var readStream = await fs.OpenReadAsync(testPath))
        {
            using var reader = new StreamReader(readStream);
            string content = await reader.ReadToEndAsync();
            Assert.Equal("hello sftp", content);
        }

        // List
        var items = await fs.ListDirectoryAsync("/home/user");
        Assert.Single(items);
        Assert.Equal("test.txt", items[0].Name);

        // Delete
        await fs.DeleteAsync(testPath, false);
        var itemAfterDelete = await fs.GetItemAsync(testPath);
        Assert.Null(itemAfterDelete);
    }
}
