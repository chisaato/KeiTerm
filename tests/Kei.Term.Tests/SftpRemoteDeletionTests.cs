using Kei.Term.Core.Abstractions;
using Kei.Term.Ssh.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Core.Services;
using Kei.Term.Core.Transfers;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Ssh.Services;
using Microsoft.Data.Sqlite;

namespace Kei.Term.Tests;

public sealed class SftpRemoteDeletionTests : IClassFixture<SshdFixture>, IDisposable
{
    private readonly SshdFixture _sshd;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"keiterm_sftp_delete_{Guid.NewGuid():N}.db");

    public SftpRemoteDeletionTests(SshdFixture sshd) => _sshd = sshd;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch
            {
                // 清理失败不影响测试结论
            }
        }
    }

    [SshdFact]
    public async Task DeleteAsync_RejectsFileLink_AndDoesNotRemoveNonEmptyDirectory()
    {
        string root = CreateRoot();
        string target = Path.Combine(root, "target.txt");
        string link = Path.Combine(root, "link");
        string directory = Path.Combine(root, "filled");
        await File.WriteAllTextAsync(target, "target");
        File.CreateSymbolicLink(link, "target.txt");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "inside.txt"), "inside");
        await using IRemoteFileSystem fs = await ConnectAsync();

        await Assert.ThrowsAsync<TransferPathRejectedException>(() => fs.DeleteAsync(link, false));
        await Assert.ThrowsAnyAsync<Exception>(() => fs.DeleteAsync(directory, true));

        Assert.Equal("target", await File.ReadAllTextAsync(target));
        Assert.True(File.Exists(link));
        Assert.Equal("inside", await File.ReadAllTextAsync(Path.Combine(directory, "inside.txt")));
    }

    [SshdFact]
    public async Task DeleteEntryAsync_RemovesLinkItselfAndEmptyDirectoryOnly()
    {
        string root = CreateRoot();
        string target = Path.Combine(root, "target.txt");
        string fileLink = Path.Combine(root, "file-link");
        string broken = Path.Combine(root, "broken");
        string filled = Path.Combine(root, "filled");
        string directoryLink = Path.Combine(root, "dir-link");
        string empty = Path.Combine(root, "empty");
        await File.WriteAllTextAsync(target, "target");
        File.CreateSymbolicLink(fileLink, "target.txt");
        File.CreateSymbolicLink(broken, "missing");
        Directory.CreateDirectory(filled);
        await File.WriteAllTextAsync(Path.Combine(filled, "inside.txt"), "inside");
        Directory.CreateSymbolicLink(directoryLink, "filled");
        Directory.CreateDirectory(empty);
        await using IRemoteFileSystem fs = await ConnectAsync();

        await fs.DeleteEntryAsync(fileLink, false);
        await fs.DeleteEntryAsync(broken, false);
        await fs.DeleteEntryAsync(directoryLink, false);
        await Assert.ThrowsAnyAsync<Exception>(() => fs.DeleteEntryAsync(filled, true));
        await fs.DeleteEntryAsync(empty, true);

        Assert.Equal("target", await File.ReadAllTextAsync(target));
        Assert.Equal("inside", await File.ReadAllTextAsync(Path.Combine(filled, "inside.txt")));
        Assert.False(File.Exists(fileLink));
        Assert.False(File.Exists(broken));
        Assert.False(Directory.Exists(directoryLink));
        Assert.True(Directory.Exists(filled));
        Assert.False(Directory.Exists(empty));
    }

    [SshdFact]
    public async Task DeletionService_RemovesNestedTreeWithoutLinkTargets()
    {
        string root = CreateRoot();
        string tree = Path.Combine(root, "tree");
        string nested = Path.Combine(tree, "nested");
        string hiddenDirectory = Path.Combine(tree, ".hidden-dir");
        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(hiddenDirectory);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(nested, "leaf.txt"), "leaf");
        await File.WriteAllTextAsync(Path.Combine(tree, ".hidden"), "hidden");
        await File.WriteAllTextAsync(Path.Combine(hiddenDirectory, "inside.txt"), "inside");
        await File.WriteAllTextAsync(Path.Combine(outside, "kept.txt"), "kept");
        File.CreateSymbolicLink(Path.Combine(tree, "file-link"), "../outside/kept.txt");
        Directory.CreateSymbolicLink(Path.Combine(tree, "dir-link"), "../outside");
        File.CreateSymbolicLink(Path.Combine(tree, "broken"), "missing");
        await using IRemoteFileSystem fs = await ConnectAsync();
        RemoteFileItem? rootItem = await fs.GetAttributesAsync(tree);
        var service = new RemoteFileDeletionService(fs);

        await service.DeleteAsync([rootItem!]);

        Assert.False(Directory.Exists(tree));
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(outside, "kept.txt")));
        Assert.True(Directory.Exists(outside));
    }

    [SshdFact]
    public async Task BackslashName_IsDeletedWithoutRemovingSimilarChildPath()
    {
        string root = CreateRoot();
        string slashFile = Path.Combine(root, "a\\b");
        string similarDirectory = Path.Combine(root, "a");
        string similarFile = Path.Combine(similarDirectory, "b");
        await File.WriteAllTextAsync(slashFile, "slash");
        Directory.CreateDirectory(similarDirectory);
        await File.WriteAllTextAsync(similarFile, "nested");
        await using IRemoteFileSystem fs = await ConnectAsync();
        RemoteFileItem? item = await fs.GetAttributesAsync(slashFile);
        var service = new RemoteFileDeletionService(fs);

        await service.DeleteAsync([item!]);

        Assert.Equal("a\\b", item!.Name);
        Assert.False(File.Exists(slashFile));
        Assert.Equal("nested", await File.ReadAllTextAsync(similarFile));
        Assert.True(Directory.Exists(similarDirectory));
    }

    private string CreateRoot()
    {
        string root = Path.Combine(_sshd.Directory, $"delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private async Task<IRemoteFileSystem> ConnectAsync()
    {
        var repository = new SqliteKnownHostRepository($"Data Source={_dbPath}");
        await repository.InitializeAsync();
        var trust = new HostKeyTrustService(repository, () => HostKeyPolicy.AcceptNew);
        IRemoteFileSystem fs = await new SshSessionFactory().CreateFileSystemAsync(
            _sshd.Config(),
            _sshd.KeyAuth(),
            options: new SshConnectOptions { HostKeyVerifier = trust });
        await fs.ConnectAsync();
        return fs;
    }
}
