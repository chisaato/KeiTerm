namespace Kei.Term.Core.Transfers;

// Linux 与 Darwin 共用这一套。探测仍逐项 command -v，不会因为平台是 Darwin 就假装工具存在。
// Windows 不走这里：RemoteToolsets.For 返回 null，探测结果记 Unknown 和空列表。
public sealed class PosixToolset : IRemoteToolset
{
    public static PosixToolset Instance { get; } = new();

    public string CreateTemporaryDirectoryCommand(string? parentDirectory = null)
        => parentDirectory == null ? "mktemp -d" : $"mktemp -d {PosixShell.Quote(parentDirectory.TrimEnd('/') + "/.keiterm-archive.XXXXXXXXXX")}";

    public string DeleteTemporaryDirectoryCommand(string directory)
    {
        string path = PosixShell.Quote(directory);
        // tar 保留了只读目录权限。仅修改我们拥有的私有工作目录，GNU chmod -R 不跟随目录内软链。
        return $"if test -d {path} && test ! -L {path}; then chmod -R u+rwX -- {path} || exit $?; fi; rm -rf -- {path}";
    }

    public string PublishDirectoryCommand(string stagingDirectory, string destinationDirectory, bool rename)
    {
        string source = PosixShell.Quote(stagingDirectory);
        string target = PosixShell.Quote(destinationDirectory);
        // 调用方持有目标声明且已 lstat 检查所有目标路径；不允许 mv 在目标出现后变成移入目录。
        if (rename) return $"test ! -e {target} && test ! -L {target} && mv -T -- {source} {target}";
        return $"mkdir -p -- {target} && cp -a -- {PosixShell.Quote(stagingDirectory.TrimEnd('/') + "/.")} {target}";
    }

    public string PlatformProbeCommand => "uname -s";

    public RemotePlatform ParsePlatform(string output)
    {
        string token = FirstLine(output);
        if (token == "Linux")
        {
            return RemotePlatform.Linux;
        }

        if (token == "Darwin")
        {
            return RemotePlatform.Darwin;
        }

        return RemotePlatform.Unknown;
    }

    public string HashProbeCommand(TransferHashAlgorithm algorithm) => algorithm switch
    {
        TransferHashAlgorithm.Sha256 => "command -v sha256sum >/dev/null 2>&1 || command -v shasum >/dev/null 2>&1",
        TransferHashAlgorithm.Sha1 => "command -v sha1sum >/dev/null 2>&1 || command -v shasum >/dev/null 2>&1",
        TransferHashAlgorithm.Md5 => "command -v md5sum >/dev/null 2>&1 || command -v md5 >/dev/null 2>&1",
        _ => "exit 127",
    };

    public string HashCommand(TransferHashAlgorithm algorithm, string path)
    {
        string quoted = PosixShell.Quote(path);
        return algorithm switch
        {
            TransferHashAlgorithm.Sha256 =>
                $"if command -v sha256sum >/dev/null 2>&1; then sha256sum -- {quoted}; elif command -v shasum >/dev/null 2>&1; then shasum -a 256 -- {quoted}; else exit 127; fi",
            TransferHashAlgorithm.Sha1 =>
                $"if command -v sha1sum >/dev/null 2>&1; then sha1sum -- {quoted}; elif command -v shasum >/dev/null 2>&1; then shasum -a 1 -- {quoted}; else exit 127; fi",
            TransferHashAlgorithm.Md5 =>
                $"if command -v md5sum >/dev/null 2>&1; then md5sum -- {quoted}; elif command -v md5 >/dev/null 2>&1; then md5 -q -- {quoted}; else exit 127; fi",
            _ => "exit 127",
        };
    }

    public bool TryParseHash(TransferHashAlgorithm algorithm, string? output, out string hex)
    {
        hex = string.Empty;
        int expected = algorithm switch
        {
            TransferHashAlgorithm.Sha256 => 64,
            TransferHashAlgorithm.Sha1 => 40,
            TransferHashAlgorithm.Md5 => 32,
            _ => 0,
        };
        if (expected == 0 || string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        string line = FirstLine(output);
        int end = 0;
        while (end < line.Length && !char.IsWhiteSpace(line[end]))
        {
            end++;
        }

        string token = line[..end];
        // GNU coreutils 在文件名含换行或反斜杠时会在哈希前加一个反斜杠。
        if (token.StartsWith('\\') && token.Length > 1)
        {
            token = token[1..];
        }

        if (token.Length != expected || !IsHex(token))
        {
            return false;
        }

        hex = token.ToLowerInvariant();
        return true;
    }

    public string ArchiveProbeCommand(TransferArchiveFormat format) => format switch
    {
        TransferArchiveFormat.Tar => "command -v tar >/dev/null 2>&1",
        TransferArchiveFormat.Gzip => "command -v tar >/dev/null 2>&1 && command -v gzip >/dev/null 2>&1",
        TransferArchiveFormat.Zstd => "command -v tar >/dev/null 2>&1 && command -v zstd >/dev/null 2>&1",
        _ => "exit 127",
    };

    public string PackCommand(TransferArchiveFormat format, string sourceDirectory, string archivePath)
    {
        (string parent, string name) = SplitDirectory(sourceDirectory);
        string parentQuoted = PosixShell.Quote(parent);
        string nameQuoted = PosixShell.Quote(name);
        string archiveQuoted = PosixShell.Quote(archivePath);
        string tarToTemp = $"tar -C {parentQuoted} -cf \"$tmp\" -- {nameQuoted} || {{ rm -f \"$tmp\"; exit 1; }}";
        return format switch
        {
            TransferArchiveFormat.Tar => $"tar -C {parentQuoted} -cf {archiveQuoted} -- {nameQuoted}",
            TransferArchiveFormat.Gzip => Join(
                $"tmp=$(mktemp {archiveQuoted}.tar.XXXXXXXXXX) || exit 1",
                "trap 'rm -f \"$tmp\"' EXIT HUP INT TERM",
                tarToTemp,
                $"gzip -c \"$tmp\" > {archiveQuoted} || {{ status=$?; rm -f \"$tmp\"; exit $status; }}",
                "rm -f \"$tmp\""),
            TransferArchiveFormat.Zstd => Join(
                $"tmp=$(mktemp {archiveQuoted}.tar.XXXXXXXXXX) || exit 1",
                "trap 'rm -f \"$tmp\"' EXIT HUP INT TERM",
                tarToTemp,
                $"zstd -q -c \"$tmp\" > {archiveQuoted} || {{ status=$?; rm -f \"$tmp\"; exit $status; }}",
                "rm -f \"$tmp\""),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "没有对应的打包命令"),
        };
    }

    public string UnpackCommand(TransferArchiveFormat format, string archivePath, string destinationDirectory)
    {
        string archiveQuoted = PosixShell.Quote(archivePath);
        string destinationQuoted = PosixShell.Quote(destinationDirectory);
        string extract = $"tar -C {destinationQuoted} -xf \"$tmp\" || {{ status=$?; rm -f \"$tmp\"; exit $status; }}";
        return format switch
        {
            TransferArchiveFormat.Tar => $"tar -C {destinationQuoted} -xf {archiveQuoted}",
            TransferArchiveFormat.Gzip => Join(
                $"tmp=$(mktemp {archiveQuoted}.tar.XXXXXXXXXX) || exit 1",
                "trap 'rm -f \"$tmp\"' EXIT HUP INT TERM",
                $"gzip -dc {archiveQuoted} > \"$tmp\" || {{ status=$?; rm -f \"$tmp\"; exit $status; }}",
                extract,
                "rm -f \"$tmp\""),
            TransferArchiveFormat.Zstd => Join(
                $"tmp=$(mktemp {archiveQuoted}.tar.XXXXXXXXXX) || exit 1",
                "trap 'rm -f \"$tmp\"' EXIT HUP INT TERM",
                $"zstd -d -q -c {archiveQuoted} > \"$tmp\" || {{ status=$?; rm -f \"$tmp\"; exit $status; }}",
                extract,
                "rm -f \"$tmp\""),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "没有对应的解包命令"),
        };
    }

    public string PackTreeCommand(TransferArchiveFormat format, string sourceDirectory, string archivePath, string manifestPath)
    {
        // 不递归、不保存硬链接；文件软链按逐文件传输一样读取目标内容，目录软链不在清单中。
        string tar = $"tar -C {PosixShell.Quote(sourceDirectory)} --format=pax --no-recursion --dereference --hard-dereference --null --verbatim-files-from -cf";
        string list = $"-T {PosixShell.Quote(manifestPath)}";
        string output = PosixShell.Quote(archivePath);
        if (format == TransferArchiveFormat.Tar) return $"{tar} {output} {list}";
        string compressor = format switch
        {
            TransferArchiveFormat.Gzip => "gzip -c",
            TransferArchiveFormat.Zstd => "zstd -q -c",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        return Join($"tmp=$(mktemp {output}.tar.XXXXXXXXXX) || exit 1",
            "trap 'rm -f \"$tmp\"' EXIT HUP INT TERM",
            $"{tar} \"$tmp\" {list} || exit $?",
            $"{compressor} \"$tmp\" > {output} || exit $?");
    }

    private static string FirstLine(string output)
    {
        string trimmed = output.Trim();
        int newline = trimmed.IndexOfAny(['\r', '\n']);
        return newline < 0 ? trimmed : trimmed[..newline].Trim();
    }

    private static bool IsHex(string token)
    {
        foreach (char c in token)
        {
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
            {
                return false;
            }
        }

        return token.Length > 0;
    }

    // 目录路径拆成 tar -C 的父目录和 -- 后面的目录名。结尾斜杠丢掉，根目录没有可打包的名字。
    private static (string Parent, string Name) SplitDirectory(string path)
    {
        string trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return ("/", string.Empty);
        }

        int slash = trimmed.LastIndexOf('/');
        if (slash < 0)
        {
            return (".", trimmed);
        }

        if (slash == 0)
        {
            return ("/", trimmed[1..]);
        }

        return (trimmed[..slash], trimmed[(slash + 1)..]);
    }

    private static string Join(params string[] parts) => string.Join("; ", parts);
}

public static class RemoteToolsets
{
    // 目录归档实现使用 GNU tar 的清单/硬链接选项和 GNU mv -T；不把 macOS 的 BSD 工具误报成可用。
    public static IRemoteToolset? ForArchives(RemotePlatform platform)
        => platform == RemotePlatform.Linux ? PosixToolset.Instance : null;

    // Darwin 复用 POSIX（shasum / md5）。Windows 没有实现，调用方必须把它当成没有工具集。
    public static IRemoteToolset? For(RemotePlatform platform) => platform switch
    {
        RemotePlatform.Linux => PosixToolset.Instance,
        RemotePlatform.Darwin => PosixToolset.Instance,
        _ => null,
    };
}
