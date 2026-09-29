using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kei.Term.App.Services;

// OpenSSH 客户端文件定位与路径展开（Core 不触碰文件系统，由此处为导入器提供 IO）
public static class OpenSshPaths
{
    public static string SshDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ssh");

    public static string UserConfig => Path.Combine(SshDirectory, "config");

    public static string UserKnownHosts => Path.Combine(SshDirectory, "known_hosts");

    // ~ 与环境变量展开；Windows 下 OpenSSH 同样接受 ~/ 前缀
    public static string Expand(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[2..]);
        }

        return expanded;
    }

    // Include 参数：空白分隔的多个路径，相对路径基于 ~/.ssh，文件名部分允许 * ? 通配
    public static IEnumerable<string> ResolveInclude(string argument)
    {
        foreach (string raw in argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string path = Expand(raw.Trim('"'));
            if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(SshDirectory, path);
            }

            string? directory = Path.GetDirectoryName(path);
            string fileName = Path.GetFileName(path);
            if (directory == null || !Directory.Exists(directory))
            {
                continue;
            }

            IEnumerable<string> files = fileName.IndexOfAny(['*', '?']) >= 0
                ? Directory.GetFiles(directory, fileName).OrderBy(f => f, StringComparer.Ordinal)
                : File.Exists(path) ? [path] : [];

            foreach (string file in files)
            {
                string? content = TryRead(file);
                if (content != null)
                {
                    yield return content;
                }
            }
        }
    }

    private static string? TryRead(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
