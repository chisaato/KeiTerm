using System.Diagnostics;
using System.Text;
using Kei.Term.Core.Models;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public sealed class UnixShellFactAttribute : FactAttribute
{
    public UnixShellFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "需要 POSIX shell 与 stat（Linux/macOS）";
        }
    }
}

public sealed class ZshFactAttribute : FactAttribute
{
    public ZshFactAttribute()
    {
        if (!File.Exists("/bin/zsh")) Skip = "需要 /bin/zsh 验证默认 shell 兼容性";
    }
}

public sealed class ScpFileSystemTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"keiterm scp ' {Guid.NewGuid():N}");
    private readonly ScpShellFileSystem _shell = new(ExecuteLocallyAsync);

    public ScpFileSystemTests() => Directory.CreateDirectory(_directory);

    [ZshFact]
    public async Task ListingFromZshLoginShell_DoesNotFailForUnmatchedHiddenFileGlobs()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "visible.txt"), "data");
        ScpShellFileSystem zsh = new((command, ct) => ExecuteLocallyAsync("/bin/zsh", command, ct));

        IReadOnlyList<RemoteFileItem> entries = await zsh.ListDirectoryAsync(_directory, CancellationToken.None);

        Assert.Equal("visible.txt", Assert.Single(entries).Name);
    }

    [UnixShellFact]
    public async Task ListAndDelete_PreserveExactNamesIncludingWhitespaceAndSymlinks()
    {
        string[] names = ["a  b.txt", "a b.txt", " leading and trailing ", "line\nbreak", ".hidden", "..also", "-option", "it's $(touch INJECTED) `touch INJECTED`"];
        foreach (string name in names)
        {
            await File.WriteAllTextAsync(Path.Combine(_directory, name), "data");
        }

        File.CreateSymbolicLink(Path.Combine(_directory, "current"), "a  b.txt");
        Directory.CreateDirectory(Path.Combine(_directory, "child"));

        IReadOnlyList<RemoteFileItem> entries = await _shell.ListDirectoryAsync(_directory, CancellationToken.None);
        Assert.Equal(names.Length + 2, entries.Count);
        foreach (string name in names)
        {
            RemoteFileItem item = Assert.Single(entries, item => item.Name == name);
            Assert.Equal(Path.Combine(_directory, name), item.FullPath);
            Assert.Equal(4, item.Size);
        }

        RemoteFileItem link = Assert.Single(entries, item => item.Name == "current");
        Assert.StartsWith("l", link.Permissions);
        Assert.Equal(Path.Combine(_directory, "current"), link.FullPath);
        Assert.True(entries[0].IsDirectory);
        Assert.Equal("child", entries[0].Name);
        Assert.False(File.Exists(Path.Combine(_directory, "INJECTED")));

        await _shell.DeleteAsync(entries.Single(item => item.Name == "a  b.txt").FullPath, false, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_directory, "a  b.txt")));
        Assert.Equal("data", await File.ReadAllTextAsync(Path.Combine(_directory, "a b.txt")));
    }

    [UnixShellFact]
    public async Task ShellOperations_ReportRemoteFailuresAndPreserveQuotedPaths()
    {
        string original = Path.Combine(_directory, "it's $(touch INJECTED)");
        string renamed = Path.Combine(_directory, "renamed ' `touch INJECTED`");
        await _shell.CreateDirectoryAsync(original, CancellationToken.None);
        await _shell.RenameAsync(original, renamed, CancellationToken.None);
        Assert.True(Directory.Exists(renamed));
        Assert.False(File.Exists(Path.Combine(_directory, "INJECTED")));

        IOException delete = await Assert.ThrowsAsync<IOException>(() =>
            _shell.DeleteAsync(renamed, false, CancellationToken.None));
        Assert.Contains("远端操作失败", delete.Message);
        Assert.True(Directory.Exists(renamed));

        string missing = Path.Combine(_directory, "missing");
        await Assert.ThrowsAsync<IOException>(() => _shell.RenameAsync(missing, renamed, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => _shell.ChangePermissionsAsync(missing, 420, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => _shell.ListDirectoryAsync(missing, CancellationToken.None));
        string file = Path.Combine(_directory, "file");
        await File.WriteAllTextAsync(file, "data");
        await Assert.ThrowsAsync<IOException>(() => _shell.CreateDirectoryAsync(file + "/child", CancellationToken.None));

        await _shell.DeleteAsync(renamed, true, CancellationToken.None);
        Assert.False(Directory.Exists(renamed));
    }

    [Fact]
    public async Task DisposingUncommittedOrCancelledUpload_LeavesRemoteContentsUntouched()
    {
        string destination = Path.Combine(_directory, "remote.txt");
        await File.WriteAllTextAsync(destination, "original remote contents");

        await using (ScpUploadBuffer abandoned = CreateUpload(destination))
        {
            await abandoned.WriteAsync("partial"u8.ToArray());
        }

        Assert.Equal("original remote contents", await File.ReadAllTextAsync(destination));

        using CancellationTokenSource cancellation = new();
        await using (ScpUploadBuffer cancelled = CreateUpload(destination))
        {
            await cancelled.WriteAsync("partial"u8.ToArray());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.CommitAsync(cancellation.Token));
        }

        Assert.Equal("original remote contents", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task CommitUploadsWholeBufferBeforeReturning_AndPropagatesFailure()
    {
        string destination = Path.Combine(_directory, "remote.txt");
        await File.WriteAllTextAsync(destination, "original remote contents");
        await using (ScpUploadBuffer complete = CreateUpload(destination))
        {
            await complete.WriteAsync(Encoding.UTF8.GetBytes("whole file"));
            await complete.CommitAsync(CancellationToken.None);
            Assert.Equal("whole file", await File.ReadAllTextAsync(destination));
        }

        await using ScpUploadBuffer failed = CreateUpload(Path.Combine(_directory, "missing", "file"));
        await failed.WriteAsync("data"u8.ToArray());
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => failed.CommitAsync(CancellationToken.None));
    }

    private static ScpUploadBuffer CreateUpload(string destination)
        => new(async (source, ct) =>
        {
            await using FileStream file = new(destination, FileMode.Create, FileAccess.Write);
            await source.CopyToAsync(file, ct);
        });

    private static Task<RemoteCommandResult> ExecuteLocallyAsync(string command, CancellationToken ct)
        => ExecuteLocallyAsync("/bin/sh", command, ct);

    private static async Task<RemoteCommandResult> ExecuteLocallyAsync(string shell, string command, CancellationToken ct)
    {
        ProcessStartInfo start = new(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new RemoteCommandResult(await output, await error, process.ExitCode);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
