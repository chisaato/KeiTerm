using System.Text;
using Kei.Term.Core.Abstractions;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public sealed class SftpOverwriteTests(SshdFixture sshd) : IClassFixture<SshdFixture>
{
    [SshdFact]
    public async Task UploadingShorterAndEmptyFiles_TruncatesExistingRemoteContents()
    {
        string path = Path.Combine(sshd.Directory, "overwrite.txt");
        await using IRemoteFileSystem fileSystem = await new SshSessionFactory()
            .CreateFileSystemAsync(sshd.Config(), sshd.KeyAuth());
        await fileSystem.ConnectAsync();

        foreach (string replacement in new[] { "xy", string.Empty })
        {
            await File.WriteAllTextAsync(path, "abcdef");
            await using (Stream remote = await fileSystem.OpenWriteAsync(path))
            {
                await remote.WriteAsync(Encoding.UTF8.GetBytes(replacement));
                await fileSystem.CommitWriteAsync(remote);
            }

            Assert.Equal(replacement, await File.ReadAllTextAsync(path));
        }
    }
}
