using System.Security.Cryptography;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public sealed class TransferCapabilitySshIntegrationTests(SshdFixture sshd) : IClassFixture<SshdFixture>
{
    [SshdFact]
    public async Task Probe_RealHost_ReportsLinuxSha256AndArchiveTools()
    {
        await using IRemoteTransferChannel channel = await new SshSessionFactory()
            .CreateTransferChannelAsync(sshd.Config(), sshd.KeyAuth());
        await using ChannelTransferEndpoint endpoint = new(channel);
        IRemoteShell? shell = await endpoint.AcquireShellAsync();
        Assert.NotNull(shell);

        FileTransferSettings settings = new();
        TransferProbeResult result = await TransferCapabilityProbe.ProbeAsync(shell, settings);
        Assert.Equal(RemotePlatform.Linux, result.Platform);
        Assert.Contains(TransferHashAlgorithm.Sha256, result.Hashes);
        Assert.Contains(TransferArchiveFormat.Gzip, result.Archives);
        Assert.Contains(TransferArchiveFormat.Tar, result.Archives);
        Assert.Equal(settings.EffectiveHashAlgorithmOrder.Where(result.Hashes.Contains).ToArray(), result.Hashes);
        Assert.Equal(settings.EffectiveArchiveFormatOrder.Where(result.Archives.Contains).ToArray(), result.Archives);

        string name = "a b'c-" + Guid.NewGuid().ToString("N") + ".txt";
        string path = Path.Combine(sshd.Directory, name);
        await File.WriteAllTextAsync(path, "hello-hash");
        RemoteCommandResult hashed = await shell.RunAsync(PosixToolset.Instance.HashCommand(TransferHashAlgorithm.Sha256, path));
        Assert.Equal(0, hashed.ExitStatus);
        Assert.True(PosixToolset.Instance.TryParseHash(TransferHashAlgorithm.Sha256, hashed.Output, out string hex));
        string expected = Convert.ToHexString(SHA256.HashData("hello-hash"u8.ToArray())).ToLowerInvariant();
        Assert.Equal(expected, hex);
    }
}
