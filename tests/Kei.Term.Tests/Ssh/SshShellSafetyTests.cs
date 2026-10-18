using System.Diagnostics;
using Kei.Term.Core.Transfers;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public sealed class PosixShellTheoryAttribute : TheoryAttribute
{
    public PosixShellTheoryAttribute()
    {
        if (!File.Exists("/bin/sh")) Skip = "需要 /bin/sh";
    }
}

public sealed class SshShellSafetyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keiterm-shell-" + Guid.NewGuid().ToString("N"));

    public SshShellSafetyTests() => Directory.CreateDirectory(_directory);

    private static readonly string[] Names =
    [
        "single'quote", "double\"quote", "$(printf INJECTED)", "`printf INJECTED`", "line\nbreak",
        " space and trailing ", "-rf", "", "中文日本語🔐", "back\\slash", "'; printf INJECTED; #"
    ];

    public static IEnumerable<object[]> Paths => Names.Select(name => new object[] { name });
    public static IEnumerable<object[]> FilePaths => Names.Where(name => name.Length > 0).Select(name => new object[] { name });

    [PosixShellTheory]
    [MemberData(nameof(Paths))]
    public async Task Quote_RoundTripsThroughPosixShellAsExactlyOneArgument(string path)
    {
        foreach (string quoted in new[] { PosixShell.Quote(path), ScpRemoteFileSystem.ShellQuote(path) })
        {
            RemoteCommandResult result = await RunAsync($"set -- {quoted}; printf '%s\\000%s' \"$#\" \"$1\"", CancellationToken.None);
            Assert.Equal(0, result.ExitStatus);
            Assert.Equal("1\0" + path, result.Output);
            Assert.Empty(result.Error);
        }
    }

    [PosixShellTheory]
    [MemberData(nameof(FilePaths))]
    public async Task ScpCommands_PreserveMaliciousNamesWithoutExecutingThem(string name)
    {
        ScpShellFileSystem shell = new(RunAsync);
        string source = Path.Combine(_directory, name);
        string destinationName = name + " renamed";
        string destination = Path.Combine(_directory, destinationName);
        await shell.CreateDirectoryAsync(name, CancellationToken.None);
        Assert.True(Directory.Exists(source));
        await shell.RenameAsync(name, destinationName, CancellationToken.None);
        Assert.False(Directory.Exists(source));
        Assert.True(Directory.Exists(destination));
        await shell.DeleteAsync(destinationName, true, CancellationToken.None);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));

        await File.WriteAllTextAsync(source, "payload");
        await File.WriteAllTextAsync(destination, "old");
        await shell.RenameOverwriteAsync(name, destinationName, CancellationToken.None);
        Assert.Equal("payload", await File.ReadAllTextAsync(destination));
        await shell.ChangePermissionsAsync(destinationName, 384, CancellationToken.None);
        DateTime timestamp = new(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        await shell.SetLastWriteTimeUtcAsync(destinationName, timestamp, CancellationToken.None);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(destination));
        await shell.DeleteAsync(destinationName, false, CancellationToken.None);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    private async Task<RemoteCommandResult> RunAsync(string command, CancellationToken ct)
    {
        ProcessStartInfo start = new("/bin/sh")
        {
            WorkingDirectory = _directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new RemoteCommandResult(await output, await error, process.ExitCode);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
