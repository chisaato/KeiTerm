using System.Formats.Tar;
using Kei.Term.Core.Transfers;

namespace Kei.Term.Tests;

public sealed class LocalDirectoryArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("keiterm-archive-test-").FullName;

    [Theory]
    [InlineData(TransferArchiveFormat.Tar)]
    [InlineData(TransferArchiveFormat.Gzip)]
    [InlineData(TransferArchiveFormat.Zstd)]
    public async Task RoundTrip_PreservesContentEmptyDirectoriesTimesAndModes_AndUsesSourceTreeLinkRules(TransferArchiveFormat format)
    {
        string source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        string file = Path.Combine(source, "sub", "a ' -file");
        await File.WriteAllTextAsync(file, "真实内容\n");
        DateTime modified = new(2023, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, modified);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            File.CreateSymbolicLink(Path.Combine(source, "link"), "sub/a ' -file");
            Directory.CreateSymbolicLink(Path.Combine(source, "directory-link"), "sub");
            File.CreateSymbolicLink(Path.Combine(source, "broken"), "missing");
        }
        Directory.SetLastWriteTimeUtc(source, modified);
        Directory.SetLastWriteTimeUtc(Path.Combine(source, "empty"), modified);
        string archive = Path.Combine(_root, "archive");
        string destination = Path.Combine(_root, "destination");

        await LocalDirectoryArchive.PackAsync(source, archive, format);
        await LocalDirectoryArchive.UnpackAsync(archive, destination, format);

        Assert.Equal("真实内容\n", await File.ReadAllTextAsync(Path.Combine(destination, "sub", "a ' -file")));
        Assert.True(Directory.Exists(Path.Combine(destination, "empty")));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(destination, "sub", "a ' -file")));
        Assert.Equal(modified, Directory.GetLastWriteTimeUtc(destination));
        Assert.Equal(modified, Directory.GetLastWriteTimeUtc(Path.Combine(destination, "empty")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(File.GetUnixFileMode(file), File.GetUnixFileMode(Path.Combine(destination, "sub", "a ' -file")));
            Assert.Equal("真实内容\n", await File.ReadAllTextAsync(Path.Combine(destination, "link")));
            Assert.Null(new FileInfo(Path.Combine(destination, "link")).LinkTarget);
            Assert.False(Directory.Exists(Path.Combine(destination, "directory-link")));
            Assert.False(File.Exists(Path.Combine(destination, "broken")));
        }
    }

    [Theory]
    [InlineData("../escape", TarEntryType.RegularFile)]
    [InlineData("/absolute", TarEntryType.RegularFile)]
    [InlineData("C:/absolute", TarEntryType.RegularFile)]
    [InlineData("hard", TarEntryType.HardLink)]
    [InlineData("device", TarEntryType.BlockDevice)]
    public async Task MaliciousArchive_IsRejected(string name, TarEntryType type)
    {
        string archive = Path.Combine(_root, "malicious.tar");
        PaxTarEntry entry = new(type, name);
        if (type == TarEntryType.HardLink) entry.LinkName = "safe";
        await WriteArchiveAsync(archive, entry);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalDirectoryArchive.UnpackAsync(archive, Path.Combine(_root, "out"), TransferArchiveFormat.Tar));
        Assert.False(File.Exists(Path.Combine(_root, "escape")));
    }

    [Theory]
    [InlineData("link", false)]
    [InlineData("link/file", false)]
    [InlineData("link/file", true)]
    public async Task SymlinkFollowedBySameNameOrChild_AndReverseOrder_AreRejected(string fileName, bool reverse)
    {
        string archive = Path.Combine(_root, "links.tar");
        PaxTarEntry link = new(TarEntryType.SymbolicLink, "link") { LinkName = "safe" };
        PaxTarEntry file = new(TarEntryType.RegularFile, fileName);
        await WriteArchiveAsync(archive, reverse ? [file, link] : [link, file]);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalDirectoryArchive.UnpackAsync(archive, Path.Combine(_root, "out"), TransferArchiveFormat.Tar));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/outside")]
    public async Task EscapingSymlink_IsRejected(string target)
    {
        string archive = Path.Combine(_root, "links.tar");
        await WriteArchiveAsync(archive, new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = target });
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalDirectoryArchive.UnpackAsync(archive, Path.Combine(_root, "out"), TransferArchiveFormat.Tar));
    }

    [Fact]
    public async Task ExistingDestinationSymlink_IsNotFollowed()
    {
        if (OperatingSystem.IsWindows()) return;
        string outside = Path.Combine(_root, "outside");
        string destination = Path.Combine(_root, "out");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(destination);
        Directory.CreateSymbolicLink(Path.Combine(destination, "link"), outside);
        string archive = Path.Combine(_root, "existing.tar");
        await WriteArchiveAsync(archive, new PaxTarEntry(TarEntryType.RegularFile, "link/escape"));
        await Assert.ThrowsAsync<TransferPathRejectedException>(() => LocalDirectoryArchive.UnpackAsync(archive, destination, TransferArchiveFormat.Tar));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    private static async Task WriteArchiveAsync(string path, params TarEntry[] entries)
    {
        await using FileStream file = File.Create(path);
        await using TarWriter writer = new(file);
        foreach (TarEntry entry in entries) await writer.WriteEntryAsync(entry);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
