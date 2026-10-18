using Kei.Term.Ssh.Services;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Kei.Term.Tests;

public sealed class SftpLinkSafetyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CyclicOrExcessiveLinks_IsIndeterminateNotBroken(bool cycle)
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "keiterm-link-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int count = cycle ? 2 : 80;
            for (int i = 0; i < count; i++)
                File.CreateSymbolicLink(Path.Combine(root, $"link{i}"), $"link{(cycle ? (i + 1) % count : i + 1)}");
            if (!cycle) await File.WriteAllBytesAsync(Path.Combine(root, $"link{count}"), new byte[7]);
            TrackingDirectory directory = new(new SshException("服务器 opendir 失败"));
            SftpLinkFollower.Probe probe = await SftpLinkFollower.ProbeAsync(directory,
                () => File.OpenRead(Path.Combine(root, "link0")), CancellationToken.None);
            Assert.Equal(SftpLinkTargetKind.Indeterminate, probe.Kind);
            Assert.Null(probe.Size);
            Assert.True(directory.Disposed);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TargetOutsideServerBoundary_PermissionErrorMustNotBeReportedBroken()
    {
        SftpLinkFollower.Probe probe = await SftpLinkFollower.ProbeAsync(
            new TrackingDirectory(new SftpPathNotFoundException("目录不可见")),
            () => throw new SftpPermissionDeniedException("目标越过服务端边界"), CancellationToken.None);
        Assert.Equal(SftpLinkTargetKind.Indeterminate, probe.Kind);
        Assert.Null(probe.Size);
    }

    [Fact]
    public async Task MissingBothProbes_IsBroken_ButPermissionFailureCannotReadFile()
    {
        TrackingDirectory missing = new(new SftpPathNotFoundException("missing"));
        SftpLinkFollower.Probe broken = await SftpLinkFollower.ProbeAsync(missing,
            () => throw new SftpPathNotFoundException("missing"), CancellationToken.None);
        Assert.Equal(SftpLinkTargetKind.Broken, broken.Kind);
        Assert.Null(broken.Size);
        Assert.True(missing.Disposed);

        TrackingDirectory denied = new(new SftpPermissionDeniedException("denied"));
        SftpLinkFollower.Probe unknown = await SftpLinkFollower.ProbeAsync(denied,
            () => throw new InvalidOperationException("权限失败时禁止打开文件"), CancellationToken.None);
        Assert.Equal(SftpLinkTargetKind.Indeterminate, unknown.Kind);
        Assert.True(denied.Disposed);
    }

    [Fact]
    public async Task EmptyDirectoryIsRecognizedWithoutOpeningFile_EvenIfClosingHandleFails()
    {
        TrackingDirectory directory = new(null) { DisposeFailure = new IOException("close failed") };
        SftpLinkFollower.Probe probe = await SftpLinkFollower.ProbeAsync(directory,
            () => throw new InvalidOperationException("目录不能当文件打开"), CancellationToken.None);
        Assert.Equal(SftpLinkTargetKind.Directory, probe.Kind);
        Assert.Null(probe.Size);
        Assert.True(directory.Disposed);
    }

    [Fact]
    public async Task FileTargetReturnsLengthAndClosesReadHandle()
    {
        TrackingDirectory directory = new(new SshException("not a directory"));
        MemoryStream stream = new(new byte[137]);
        SftpLinkFollower.Probe probe = await SftpLinkFollower.ProbeAsync(directory, () => stream, CancellationToken.None);
        Assert.Equal(SftpLinkTargetKind.File, probe.Kind);
        Assert.Equal(137, probe.Size);
        Assert.False(stream.CanRead);
        Assert.True(directory.Disposed);
    }

    [Fact]
    public async Task CancellationIsNotConvertedIntoLinkClassification()
    {
        TrackingDirectory directory = new(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SftpLinkFollower.ProbeAsync(directory,
            () => throw new InvalidOperationException("取消后不可读"), CancellationToken.None));
        Assert.True(directory.Disposed);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        TrackingDirectory unopened = new(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SftpLinkFollower.ProbeAsync(unopened,
            () => throw new InvalidOperationException("取消后不可读"), cancellation.Token));
        Assert.False(unopened.Enumerated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelativeAndAbsoluteLinks_AreResolvedByTransport(bool absolute)
    {
        if (OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "target");
            string link = Path.Combine(directory, "link");
            await File.WriteAllBytesAsync(file, new byte[29]);
            File.CreateSymbolicLink(link, absolute ? file : "target");
            SftpLinkFollower.Probe probe = await SftpLinkFollower.ProbeAsync(
                new TrackingDirectory(new SshException("not a directory")), () => File.OpenRead(link), CancellationToken.None);
            Assert.Equal(SftpLinkTargetKind.File, probe.Kind);
            Assert.Equal(29, probe.Size);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class TrackingDirectory(Exception? failure) : IAsyncEnumerable<ISftpFile>, IAsyncEnumerator<ISftpFile>
    {
        public bool Disposed { get; private set; }
        public bool Enumerated { get; private set; }
        public Exception? DisposeFailure { get; init; }
        public ISftpFile Current => throw new InvalidOperationException("空目录无条目");
        public IAsyncEnumerator<ISftpFile> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Enumerated = true;
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
            => failure == null ? ValueTask.FromResult(false) : ValueTask.FromException<bool>(failure);
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return DisposeFailure == null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
        }
    }
}
