using System.Globalization;
using Kei.Term.Core.Sync;

namespace Kei.Term.Infrastructure.Sync;

public sealed class LocalFolderSyncLocation(string directory) : ISyncLocation
{
    public bool SupportsConditionalWrites => false;
    private string MainPath => Path.Combine(directory, SyncObjectNames.PackageFileName);
    private string HistoryPath(string name)
    {
        SyncObjectNames.ValidateHistoryName(name);
        return Path.Combine(directory, "history", name);
    }

    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        string probe = Path.Combine(directory, $".probe-{Guid.NewGuid():N}");
        try { await File.WriteAllBytesAsync(probe, [], ct); }
        finally { File.Delete(probe); }
    }

    public async Task<SyncObject?> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(MainPath, ct);
            var info = new FileInfo(MainPath);
            return new(bytes, info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + bytes.Length);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public Task WriteAsync(byte[] content, string? expectedVersionTag = null, CancellationToken ct = default)
        => AtomicWriteAsync(MainPath, content, ct);

    public Task<IReadOnlyList<SyncHistoryEntry>> ListHistoryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string path = Path.Combine(directory, "history");
        IReadOnlyList<SyncHistoryEntry> result = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.age").Select(p => new SyncHistoryEntry(Path.GetFileName(p), File.GetLastWriteTimeUtc(p)))
                .OrderByDescending(e => e.WrittenAtUtc).ToArray() : [];
        return Task.FromResult(result);
    }

    public async Task<byte[]?> ReadHistoryAsync(string name, CancellationToken ct = default)
    {
        try { return await File.ReadAllBytesAsync(HistoryPath(name), ct); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public Task WriteHistoryAsync(string name, byte[] content, CancellationToken ct = default)
        => AtomicWriteAsync(HistoryPath(name), content, ct);

    public Task DeleteHistoryAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        File.Delete(HistoryPath(name));
        return Task.CompletedTask;
    }

    private static async Task AtomicWriteAsync(string path, byte[] content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
