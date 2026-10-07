namespace Kei.Term.Ssh.Services;

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
                printf '%s\000%s\000' "$metadata" "${entry#./}"
            done
            """;
        // SSH exec 首先进入账户的默认 shell；显式选择 POSIX shell，避免 zsh 的 NOMATCH 或 fish 语法差异。
        string output = await ExecuteCheckedAsync($"/bin/sh -c {ScpRemoteFileSystem.ShellQuote(command)}", ct);
        string[] fields = output.Split('\0');
        if (fields.Length % 2 != 1 || fields[^1].Length != 0)
        {
            throw new IOException("远端目录数据不完整");
        }

        List<RemoteFileItem> items = [];
        for (int i = 0; i < fields.Length - 1; i += 2)
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

            string name = fields[i + 1];
            items.Add(new RemoteFileItem(
                name,
                directory.TrimEnd('/') + "/" + name,
                metadata[0][0] == 'd',
                size,
                DateTimeOffset.FromUnixTimeSeconds(modified),
                metadata[0],
                uid,
                gid));
        }

        return items.OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct)
        => ExecuteCheckedAsync($"rm {(isDirectory ? "-rf" : "-f")} -- {ScpRemoteFileSystem.ShellQuote(path)}", ct);

    public Task RenameAsync(string oldPath, string newPath, CancellationToken ct)
        => ExecuteCheckedAsync($"mv -- {ScpRemoteFileSystem.ShellQuote(oldPath)} {ScpRemoteFileSystem.ShellQuote(newPath)}", ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
        => ExecuteCheckedAsync($"mkdir -p -- {ScpRemoteFileSystem.ShellQuote(path)}", ct);

    public Task ChangePermissionsAsync(string path, int permissions, CancellationToken ct)
        => ExecuteCheckedAsync($"chmod {Convert.ToString(permissions, 8)} -- {ScpRemoteFileSystem.ShellQuote(path)}", ct);
}
