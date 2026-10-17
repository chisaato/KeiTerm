using System.Security.Cryptography;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public sealed class TransferHashSshIntegrationTests(SshdFixture sshd) : IClassFixture<SshdFixture>
{
    [SshdFact]
    public async Task UploadTamper_RetriesFromScratch_AndDownloadMatchesSha256()
    {
        string remoteDir = Path.Combine(sshd.Directory, "hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(remoteDir);
        string downloadDir = Path.Combine(sshd.Directory, "hash-down-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadDir);
        byte[] content = new byte[32 * 1024];
        Random.Shared.NextBytes(content);
        string source = Path.Combine(sshd.Directory, "hash-src-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(source, content);
        string uploadPartial = Path.Combine(remoteDir, "payload.bin.keipart");
        int tampered = 0;

        await using IRemoteTransferChannel channel = await new SshSessionFactory().CreateTransferChannelAsync(sshd.Config(), sshd.KeyAuth());
        await using ChannelTransferEndpoint endpoint = new(channel);
        FileTransferSettings settings = new()
        {
            VerifyHashEnabled = true,
            HashAlgorithmOrder = [TransferHashAlgorithm.Sha256],
            ArchiveFormatOrder = [],
            ResumeEnabled = true,
            AutoRetryEnabled = true,
            RetryCount = 1,
            BackoffMode = RetryBackoffMode.Fixed,
            InitialRetryIntervalSeconds = 0,
            MaxRetryIntervalSeconds = 0,
            DefaultConflictPolicy = TransferConflictPolicy.Overwrite,
        };
        await using TransferQueue queue = new(
            endpoint,
            () => settings,
            capabilities: new TransferCapabilityProvider(null, null, () => settings, endpoint.AcquireShellAsync),
            afterCopy: async cancellationToken =>
            {
                // 第二次复制是重试，不能再改，否则永远对不上。
                if (!File.Exists(uploadPartial) || Interlocked.Exchange(ref tampered, 1) != 0)
                {
                    return;
                }

                byte[] stored = await File.ReadAllBytesAsync(uploadPartial, cancellationToken);
                stored[0] ^= 0xFF;
                await File.WriteAllBytesAsync(uploadPartial, stored, cancellationToken);
            });

        QueuedTransfer upload = queue.Enqueue(new TransferSubmission(
            TransferDirection.Upload,
            "payload.bin",
            source,
            remoteDir,
            false,
            content.Length,
            new DateTimeOffset(File.GetLastWriteTimeUtc(source))));
        await WaitUntilAsync(() => upload.State is QueuedTransferState.Completed or QueuedTransferState.Failed);

        Assert.Equal(QueuedTransferState.Completed, upload.State);
        Assert.False(upload.IsUnverified);
        Assert.Equal(1, Volatile.Read(ref tampered));
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(remoteDir, "payload.bin")));
        Assert.False(File.Exists(uploadPartial));

        string remoteFile = Path.Combine(remoteDir, "payload.bin");
        QueuedTransfer download = queue.Enqueue(new TransferSubmission(
            TransferDirection.Download,
            "payload.bin",
            downloadDir,
            remoteFile,
            false,
            content.Length,
            new DateTimeOffset(File.GetLastWriteTimeUtc(remoteFile))));
        await WaitUntilAsync(() => download.State is QueuedTransferState.Completed or QueuedTransferState.Failed);

        Assert.Equal(QueuedTransferState.Completed, download.State);
        Assert.False(download.IsUnverified);
        byte[] downloaded = await File.ReadAllBytesAsync(Path.Combine(downloadDir, "payload.bin"));
        Assert.Equal(content, downloaded);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(content)),
            Convert.ToHexString(SHA256.HashData(downloaded)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
