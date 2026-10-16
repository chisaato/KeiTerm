namespace Kei.Term.Ssh.Services;

using Kei.Term.Core.Services;
using System.Globalization;
using Kei.Term.Core.Models;

internal sealed record RemoteCommandResult(string Output, string Error, int? ExitStatus, string? ExitSignal = null);

// SCP 的目录和元数据操作通过 shell 完成；命令成功检查集中处理，保持与 SFTP 的异常语义一致。
internal sealed class ScpShellFileSystem(Func<string, CancellationToken, Task<RemoteCommandResult>> execute)
{
    public async Task<string> ExecuteCheckedAsync(string command, CancellationToken ct)
    {
        RemoteCommandResult result = await execute(command, ct);
        if (result.ExitStatus != 0 || !string.IsNullOrEmpty(result.ExitSignal))
        {
            string reason = string.IsNullOrWhiteSpace(result.Error) ? "远端未提供错误信息" : result.Error.Trim();
            throw new IOException($"远端操作失败（退出码 {result.ExitStatus?.ToString(CultureInfo.InvariantCulture) ?? "未知"}，信号 {result.ExitSignal ?? "无"}）：{reason}");
        }

        return result.Output;
    }

    public async Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string directory, CancellationToken ct)
    {
        // 不解析 ls 的人类可读文件名。POSIX glob 保留所有名字，stat 仅输出固定元数据，NUL 分隔名称。
        // GNU/Linux 与 BSD/macOS 的 stat 选项不同，在远端探测后选用；不依赖远端 Python 或 GNU find。
        string command = $$"""
            cd -- {{ScpRemoteFileSystem.ShellQuote(directory)}} || exit
            LC_ALL=C
            export LC_ALL
            if [ ! -r . ] || [ ! -x . ]; then
                printf '%s\n' 'Cannot read directory' >&2
                exit 1
            fi
            if stat -c '%s' . >/dev/null 2>&1; then
                stat_kind=gnu
            else
                stat_kind=bsd
            fi
            for entry in ./* ./.[!.]* ./..?*; do
                if [ ! -e "$entry" ] && [ ! -L "$entry" ]; then continue; fi
                if [ "$stat_kind" = gnu ]; then
                    metadata=$(stat -c '%A %s %Y %u %g' -- "$entry") || exit
                else
                    metadata=$(stat -f '%Sp %z %m %u %g' "$entry") || exit
                fi
                link=-
                if [ -L "$entry" ]; then
                    if [ -d "$entry" ]; then
                        link=d
                    elif [ -e "$entry" ]; then
                        if [ "$stat_kind" = gnu ]; then
                            followed=$(stat -L -c '%s %Y' -- "$entry") || exit
                        else
                            followed=$(stat -L -f '%z %m' "$entry") || exit
                        fi
                        link="f $followed"
                    else
                        link=b
                    fi
                fi
                printf '%s\000%s\000%s\000' "$metadata" "$link" "${entry#./}"
            done
            """;
        // SSH exec 首先进入账户的默认 shell；显式选择 POSIX shell，避免 zsh 的 NOMATCH 或 fish 语法差异。
        string output = await ExecuteCheckedAsync($"/bin/sh -c {ScpRemoteFileSystem.ShellQuote(command)}", ct);
        string[] fields = output.Split('\0');
        if (fields.Length % 3 != 1 || fields[^1].Length != 0)
        {
            throw new IOException("远端目录数据不完整");
        }

        List<RemoteFileItem> items = [];
        for (int i = 0; i < fields.Length - 1; i += 3)
        {
            string[] metadata = fields[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 5
                || metadata[0].Length != 10
                || !long.TryParse(metadata[1], NumberStyles.None, CultureInfo.InvariantCulture, out long size)
                || !long.TryParse(metadata[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long modified)
                || !int.TryParse(metadata[3], NumberStyles.None, CultureInfo.InvariantCulture, out int uid)
                || !int.TryParse(metadata[4], NumberStyles.None, CultureInfo.InvariantCulture, out int gid))
            {
                throw new IOException("远端目录元数据无效");
            }

            string name = fields[i + 2];
            (bool isLink, bool? targetIsDirectory, long? targetSize, DateTimeOffset? targetModified, bool broken, bool indeterminate) = ParseLink(fields[i + 1]);
            items.Add(new RemoteFileItem(
                name,
                directory.TrimEnd('/') + "/" + name,
                metadata[0][0] == 'd',
                size,
                DateTimeOffset.FromUnixTimeSeconds(modified),
                metadata[0],
                uid,
                gid,
                isLink,
                targetIsDirectory,
                targetSize,
                targetModified,
                broken,
                indeterminate));
        }

        return items.OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, NaturalStringComparer.Instance).ToList();
    }

    private static (bool IsLink, bool? TargetIsDirectory, long? TargetSize, DateTimeOffset? TargetModified, bool Broken, bool Indeterminate) ParseLink(string link)
    {
        if (link == "-" || link.Length == 0)
        {
            return (false, null, null, null, false, false);
        }

        if (link == "d")
        {
            return (true, true, null, null, false, false);
        }

        if (link == "b")
        {
            // [ -e ] 对断开和无法 stat 都是假，不能断言已断开。
            return (true, null, null, null, false, true);
        }

        string[] parts = link.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3
            && parts[0] == "f"
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long size)
            && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long modified))
        {
            return (true, false, size, DateTimeOffset.FromUnixTimeSeconds(modified), false, false);
        }

        throw new IOException("远端符号链接元数据无效");
    }

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct)
        => ExecuteCheckedAsync($"rm {(isDirectory ? "-rf" : "-f")} -- {ScpRemoteFileSystem.ShellQuote(path)}", ct);

    public Task RenameAsync(string oldPath, string newPath, CancellationToken ct)
        => ExecuteCheckedAsync($"mv -- {ScpRemoteFileSystem.ShellQuote(oldPath)} {ScpRemoteFileSystem.ShellQuote(newPath)}", ct);

    public Task RenameOverwriteAsync(string oldPath, string newPath, CancellationToken ct)
        => ExecuteCheckedAsync($"mv -f -- {ScpRemoteFileSystem.ShellQuote(oldPath)} {ScpRemoteFileSystem.ShellQuote(newPath)}", ct);

    public Task SetLastWriteTimeUtcAsync(string path, DateTime lastWriteTimeUtc, CancellationToken ct)
    {
        DateTime utc = lastWriteTimeUtc.Kind switch
        {
            DateTimeKind.Local => lastWriteTimeUtc.ToUniversalTime(),
            DateTimeKind.Utc => lastWriteTimeUtc,
            _ => DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc),
        };
        long epoch = new DateTimeOffset(utc).ToUnixTimeSeconds();
        string stamp = utc.ToString("yyyyMMddHHmm.ss", CultureInfo.InvariantCulture);
        // GNU touch 接受 @epoch；BSD 没有 -d，退回 TZ=UTC touch -t。路径经 ShellQuote，避免文件名被 shell 展开。
        string command = $$"""
            path={{ScpRemoteFileSystem.ShellQuote(path)}}
            if touch -d @{{epoch}} -- "$path" 2>/dev/null; then
              exit 0
            fi
            TZ=UTC touch -t {{stamp}} -- "$path"
            """;
        return ExecuteCheckedAsync($"/bin/sh -c {ScpRemoteFileSystem.ShellQuote(command)}", ct);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
        => ExecuteCheckedAsync($"mkdir -p -- {ScpRemoteFileSystem.ShellQuote(path)}", ct);

    public Task ChangePermissionsAsync(string path, int permissions, CancellationToken ct)
        => ExecuteCheckedAsync($"chmod {Convert.ToString(permissions, 8)} -- {ScpRemoteFileSystem.ShellQuote(path)}", ct);
}
