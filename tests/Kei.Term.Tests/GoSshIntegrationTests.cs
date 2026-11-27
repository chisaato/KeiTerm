using System.Text;
using System.Threading.Channels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Ssh.Abstractions;
using Kei.Term.Ssh.Services;
using Kei.Term.Transport.Go;
using Xunit;

namespace Kei.Term.Tests;

// 本机完整链路冒烟：公开 C# 工厂 -> 独立 Go daemon -> 真实 sshd。
// 不代表用户原始 EOF 已复现，也不替代跨平台或硬件验收。
public sealed class GoSshIntegrationTests(GoProtocolDaemonBinary binary, SshdFixture sshd)
    : IClassFixture<GoProtocolDaemonBinary>, IClassFixture<SshdFixture>
{
    [SshdFact]
    public async Task Terminal_VerifiesHostKey_ReceivesExecutedOutput_AndKeepsSharedDaemonAfterDisconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (repo, verifier) = await CreateTrustAsync();
        await using var manager = new GoSidecarProcessManager(binary.Path);
        var factory = new GoSshSessionFactory(manager, verifier);
        await using var session = await factory.CreateSessionAsync(sshd.Config("go-terminal"), sshd.KeyAuth(), ct: timeout.Token);
        var output = Channel.CreateUnbounded<byte>();
        session.OutputReceived += data => { foreach (byte value in data) output.Writer.TryWrite(value); };
        await session.ConnectAsync(timeout.Token);
        Assert.True(session.IsConnected);
        await session.ResizeTerminalAsync(101, 33, 808, 528, timeout.Token);

        string left = "GO_EXEC_" + Guid.NewGuid().ToString("N");
        string right = "_COMPLETE";
        string marker = left + right;
        string command = $"printf '%s%s\\n' '{left}' '{right}'\n";
        // PTY 可能回显输入；输入中不存在连续 marker，只有命令被执行才会匹配。
        Assert.DoesNotContain(marker, command);
        await session.SendInputAsync(Encoding.UTF8.GetBytes(command), timeout.Token);
        var received = new StringBuilder();
        while (!received.ToString().Contains(marker, StringComparison.Ordinal))
            received.Append((char)await output.Reader.ReadAsync(timeout.Token));

        var host = Assert.Single(await repo.GetAllAsync());
        Assert.Equal(sshd.HostKeys[host.KeyType], host.PublicKeyBase64);
        Assert.Equal(sshd.Port, host.Port);
        await session.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        Assert.False(session.IsConnected);
        var client = await manager.GetClientAsync(timeout.Token);
        Assert.True((await client.CallAsync("daemon.ping", ct: timeout.Token)).GetProperty("pong").GetBoolean());
    }

    [SshdFact]
    public async Task Sftp_CreatesUploadsReadsRenamesAndDeletesThroughRealDaemon()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (_, verifier) = await CreateTrustAsync();
        await using var manager = new GoSidecarProcessManager(binary.Path);
        var factory = new GoSshSessionFactory(manager, verifier);
        await using var fs = await factory.CreateFileSystemAsync(sshd.Config("go-sftp"), sshd.KeyAuth(), ct: timeout.Token);
        await fs.ConnectAsync(timeout.Token);
        Assert.True(fs.IsConnected);
        await FileTransferExecutorSshIntegrationTests.VerifyEditorSaveAsync(fs, sshd.Directory);
        string directory = Path.Combine(sshd.Directory, "go-sftp-" + Guid.NewGuid().ToString("N"));
        string original = directory + "/payload.bin";
        string renamed = directory + "/renamed.bin";
        byte[] payload = [0, 1, 127, 128, 255, 13, 10, 65];
        await fs.CreateDirectoryAsync(directory, timeout.Token);
        await using (var upload = await fs.OpenWriteAsync(original, timeout.Token))
        {
            await upload.WriteAsync(payload, timeout.Token);
            await fs.CommitWriteAsync(upload, timeout.Token);
        }
        var item = Assert.Single(await fs.ListDirectoryAsync(directory, timeout.Token), entry => entry.Name == "payload.bin");
        Assert.Equal(payload.Length, item.Size);
        Assert.False(item.IsDirectory);
        Assert.Equal(payload, await File.ReadAllBytesAsync(original, timeout.Token));
        await fs.RenameAsync(original, renamed, timeout.Token);
        Assert.False(File.Exists(original));
        await using (var download = await fs.OpenReadAsync(renamed, timeout.Token))
        {
            using var bytes = new MemoryStream();
            await download.CopyToAsync(bytes, timeout.Token);
            Assert.Equal(payload, bytes.ToArray());
        }
        await fs.DeleteEntryAsync(renamed, isDirectory: false, timeout.Token);
        await fs.DeleteEntryAsync(directory, isDirectory: true, timeout.Token);
        Assert.False(Directory.Exists(directory));
        await fs.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        Assert.True((await (await manager.GetClientAsync(timeout.Token)).CallAsync("daemon.ping", ct: timeout.Token)).GetProperty("pong").GetBoolean());
    }

    private async Task<(SqliteKnownHostRepository Repo, HostKeyTrustService Verifier)> CreateTrustAsync()
    {
        var repo = new SqliteKnownHostRepository($"Data Source={Path.Combine(sshd.Directory, "known-hosts-" + Guid.NewGuid().ToString("N") + ".db")};Pooling=False");
        await repo.InitializeAsync();
        return (repo, new HostKeyTrustService(repo, () => HostKeyPolicy.AcceptNew));
    }
}
