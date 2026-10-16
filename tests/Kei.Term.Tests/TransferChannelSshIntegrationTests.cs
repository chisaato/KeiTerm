using System.Text;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public sealed class TransferChannelSshIntegrationTests(SshdFixture sshd) : IClassFixture<SshdFixture>
{
    [SshdFact]
    public async Task OffsetWrite_OverwriteRename_AndMtime_RoundTripOnTransferChannel()
    {
        string directory = Path.Combine(sshd.Directory, "transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using IRemoteTransferChannel channel = await new SshSessionFactory()
            .CreateTransferChannelAsync(sshd.Config(), sshd.KeyAuth());
        await using IRemoteTransferLease lease = await channel.AcquireAsync();
        IRemoteFileSystem files = lease.FileSystem;
        Assert.True(files.SupportsResume);

        string path = Path.Combine(directory, "offset.bin");
        await using (Stream write = await files.OpenWriteAsync(path, 0))
        {
            await write.WriteAsync("0123456789"u8.ToArray());
            await files.CommitWriteAsync(write);
        }

        await using (Stream patch = await files.OpenWriteAsync(path, 4))
        {
            await patch.WriteAsync("ABC"u8.ToArray());
            await files.CommitWriteAsync(patch);
        }

        Assert.Equal("0123ABC789", await File.ReadAllTextAsync(path));
        RemoteFileItem? patched = await files.GetItemAsync(path);
        Assert.NotNull(patched);
        Assert.Equal(10, patched.Size);
        await using Stream read = await files.OpenReadAsync(path, 4);
        byte[] slice = new byte[3];
        Assert.Equal(3, await read.ReadAsync(slice));
        Assert.Equal("ABC", Encoding.ASCII.GetString(slice));

        string source = Path.Combine(directory, "incoming.txt");
        string target = Path.Combine(directory, "kept.txt");
        await File.WriteAllTextAsync(source, "from-part");
        await File.WriteAllTextAsync(target, "previous");
        await files.RenameOverwriteAsync(source, target);
        Assert.False(File.Exists(source));
        Assert.Equal("from-part", await File.ReadAllTextAsync(target));

        DateTime expected = new(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        await files.SetLastWriteTimeUtcAsync(target, expected);
        RemoteFileItem? renamed = await files.GetItemAsync(target);
        Assert.NotNull(renamed);
        Assert.Equal(expected, renamed.LastModified.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, renamed.LastModified.Offset);
    }

    [SshdFact]
    public async Task ThreeConcurrentStreams_ShareOneConnection_AndKeepTheirBytes()
    {
        string directory = Path.Combine(sshd.Directory, "streams-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using IRemoteTransferChannel channel = await new SshSessionFactory()
            .CreateTransferChannelAsync(sshd.Config(), sshd.KeyAuth());
        IRemoteTransferLease[] leases = await Task.WhenAll(
            channel.AcquireAsync(),
            channel.AcquireAsync(),
            channel.AcquireAsync());
        try
        {
            SftpTransferConnection concrete = Assert.IsType<SftpTransferConnection>(channel);
            Assert.Equal(1, concrete.ConnectCount);

            byte[][] payloads = leases.Select((_, index) =>
            {
                byte[] payload = new byte[96 * 1024];
                Random.Shared.NextBytes(payload);
                payload[0] = (byte)index;
                return payload;
            }).ToArray();
            string[] paths = payloads.Select((_, index) => Path.Combine(directory, $"part-{index}.bin")).ToArray();

            await Task.WhenAll(leases.Select((lease, index) => WriteAllAsync(lease.FileSystem, paths[index], payloads[index])));

            for (int index = 0; index < payloads.Length; index++)
            {
                Assert.Equal(payloads[index], await File.ReadAllBytesAsync(paths[index]));
                RemoteFileItem? item = await leases[index].FileSystem.GetItemAsync(paths[index]);
                Assert.NotNull(item);
                Assert.Equal(payloads[index].Length, item.Size);
            }
        }
        finally
        {
            foreach (IRemoteTransferLease lease in leases)
            {
                await lease.DisposeAsync();
            }
        }
    }

    [SshdFact]
    public async Task ScpSetsMtime_AndStillRejectsResume()
    {
        string path = Path.Combine(sshd.Directory, "scp-mtime-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "scp");
        ResolvedSessionConfig config = sshd.Config() with { FileTransferProtocol = FileTransferProtocol.Scp };
        await using IRemoteFileSystem files = await new SshSessionFactory()
            .CreateFileSystemAsync(config, sshd.KeyAuth());
        await files.ConnectAsync();

        Assert.False(files.SupportsResume);
        await Assert.ThrowsAsync<NotSupportedException>(() => files.OpenReadAsync(path, 1));

        DateTime expected = new(2018, 4, 3, 2, 1, 0, DateTimeKind.Utc);
        await files.SetLastWriteTimeUtcAsync(path, expected);
        RemoteFileItem? item = await files.GetItemAsync(path);
        Assert.NotNull(item);
        Assert.Equal(3, item.Size);
        Assert.Equal(expected, item.LastModified.UtcDateTime);
    }

    private static async Task WriteAllAsync(IRemoteFileSystem files, string path, byte[] payload)
    {
        await using Stream stream = await files.OpenWriteAsync(path, 0);
        await stream.WriteAsync(payload);
        await files.CommitWriteAsync(stream);
    }
}
