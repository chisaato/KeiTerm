using System.Text;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Transfers;
using Kei.Term.Ssh.Services;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Kei.Term.Tests;

public sealed class SftpTransferPrimitiveTests
{
    [Fact]
    public async Task SftpDeclaresResume_ScpRejectsNonZeroOffset()
    {
        ConnectionInfo info = new("127.0.0.1", "user", new PasswordAuthenticationMethod("user", "x"));
        await using SftpRemoteFileSystem sftp = new(info);
        await using ScpRemoteFileSystem scp = new(info);

        Assert.True(sftp.SupportsResume);
        Assert.False(scp.SupportsResume);
        await Assert.ThrowsAsync<NotSupportedException>(() => scp.OpenReadAsync("/tmp/a", 1));
        await Assert.ThrowsAsync<NotSupportedException>(() => scp.OpenWriteAsync("/tmp/a", 4));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scp.OpenReadAsync("/tmp/a", -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sftp.OpenWriteAsync("/tmp/a", -2));
    }

    [Fact]
    public async Task ClosedTransferLease_FailsBeforeTouchingTheClient()
    {
        ConnectionInfo info = new("127.0.0.1", "user", new PasswordAuthenticationMethod("user", "x"));
        using SftpClient client = new(info);
        await using SftpRemoteFileSystem fileSystem = new(client, () => new IOException("传输连接已关闭"), null);

        IOException error = await Assert.ThrowsAsync<IOException>(() => fileSystem.OpenReadAsync("/tmp/x", 3));
        Assert.Equal("传输连接已关闭", error.Message);
    }

    [Fact]
    public void PosixRename_RefusesNonAtomicOverwriteWhenExtensionIsMissing()
    {
        int plain = 0;
        SftpOverwriteRename.Execute(() => { }, () => throw new InvalidOperationException("成功时不应查目标"), () => plain++);
        Assert.Equal(0, plain);

        TransferAtomicOverwriteUnsupportedException blocked = Assert.Throws<TransferAtomicOverwriteUnsupportedException>(() =>
            SftpOverwriteRename.Execute(
                () => throw new NotSupportedException("Extension method posix-rename@openssh.com currently not supported by the server."),
                () => true,
                () => throw new InvalidOperationException("目标存在时不应普通改名")));
        Assert.Contains("未删除任何一方", blocked.Message);

        SftpOverwriteRename.Execute(
            () => throw new SftpException(StatusCode.OperationUnsupported, "posix-rename"),
            () => false,
            () => plain++);
        Assert.Equal(1, plain);

        Assert.Throws<SftpPermissionDeniedException>(() => SftpOverwriteRename.Execute(
            () => throw new SftpPermissionDeniedException("denied"),
            () => throw new InvalidOperationException("不应查目标"),
            () => throw new InvalidOperationException("不应普通改名")));

        Assert.Throws<IOException>(() => SftpOverwriteRename.Execute(
            () => throw new NotSupportedException("missing"),
            () => false,
            () => throw new IOException("普通改名失败")));
    }

    [Fact]
    public void LinkProbe_DoesNotCallPermissionFailureBroken()
    {
        Assert.Equal(SftpLinkTargetKind.Broken, SftpLinkFollower.Classify(ProbeOutcome.NotFound, ProbeOutcome.NotFound));
        Assert.Equal(SftpLinkTargetKind.File, SftpLinkFollower.Classify(ProbeOutcome.Ambiguous, ProbeOutcome.Opened));
        Assert.Equal(SftpLinkTargetKind.Indeterminate, SftpLinkFollower.Classify(ProbeOutcome.PermissionDenied, ProbeOutcome.NotFound));
        Assert.Equal(SftpLinkTargetKind.Indeterminate, SftpLinkFollower.Classify(ProbeOutcome.Ambiguous, ProbeOutcome.Ambiguous));
        Assert.Equal(SftpLinkTargetKind.Indeterminate, SftpLinkFollower.Classify(ProbeOutcome.NotFound, ProbeOutcome.PermissionDenied));
        Assert.Equal(SftpLinkTargetKind.Indeterminate, SftpLinkFollower.Classify(ProbeOutcome.Ambiguous, ProbeOutcome.NotFound));
    }
}
