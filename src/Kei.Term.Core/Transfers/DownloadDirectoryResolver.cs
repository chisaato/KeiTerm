namespace Kei.Term.Core.Transfers;

public static class DownloadDirectoryResolver
{
    public const string FolderName = "Downloads";

    // 空或空白配置表示系统下载目录。Core 不做平台 P/Invoke：Linux 读 XDG user-dirs，其余回退到用户目录下的 Downloads。
    public static string Resolve(string? configured)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Resolve(configured, home, TryReadUserDirs(home));
    }

    public static string Resolve(string? configured, string? userProfile, string? userDirsText)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        string? fromXdg = TryParseXdgDownloadDir(userDirsText, userProfile);
        if (!string.IsNullOrEmpty(fromXdg))
        {
            return fromXdg;
        }

        if (string.IsNullOrEmpty(userProfile))
        {
            return FolderName;
        }

        return Path.Combine(userProfile, FolderName);
    }

    public static string? TryParseXdgDownloadDir(string? userDirsText, string? userProfile)
    {
        if (string.IsNullOrEmpty(userDirsText))
        {
            return null;
        }

        string text = userDirsText.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        string? found = null;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = line[..eq].Trim();
            if (!string.Equals(key, "XDG_DOWNLOAD_DIR", StringComparison.Ordinal))
            {
                continue;
            }

            string expanded = ExpandHome(Unquote(line[(eq + 1)..].Trim()), userProfile);
            if (!string.IsNullOrWhiteSpace(expanded))
            {
                found = expanded;
            }
        }

        return found;
    }

    private static string? TryReadUserDirs(string home)
    {
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        string path = Path.Combine(home, ".config", "user-dirs.dirs");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return File.ReadAllText(path);
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

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            char quote = value[0];
            if ((quote == '"' || quote == '\'') && value[^1] == quote)
            {
                return value[1..^1];
            }
        }

        return value;
    }

    private static string ExpandHome(string value, string? home)
    {
        string replacement = home ?? string.Empty;
        string expanded = ReplaceHomeToken(value, replacement);
        if (string.IsNullOrWhiteSpace(expanded))
        {
            return string.Empty;
        }

        if (expanded.StartsWith('/') || Path.IsPathRooted(expanded))
        {
            return expanded;
        }

        if (string.IsNullOrEmpty(home))
        {
            return expanded;
        }

        return Path.Combine(home, expanded);
    }

    // ${HOME} 必须先于 $HOME，否则 ${HOME} 会被截成 {…}。$HOME 后面若还是标识符字符则不替换。
    private static string ReplaceHomeToken(string value, string home)
    {
        string expanded = value.Replace("${HOME}", home, StringComparison.Ordinal);
        System.Text.StringBuilder builder = new(expanded.Length);
        for (int i = 0; i < expanded.Length; i++)
        {
            if (MatchesHome(expanded, i))
            {
                builder.Append(home);
                i += 4;
                continue;
            }

            builder.Append(expanded[i]);
        }

        return builder.ToString();
    }

    private static bool MatchesHome(string value, int index)
    {
        if (index + 5 > value.Length || !value.AsSpan(index).StartsWith("$HOME"))
        {
            return false;
        }

        if (index + 5 == value.Length)
        {
            return true;
        }

        char next = value[index + 5];
        return !char.IsAsciiLetterOrDigit(next) && next != '_';
    }
}
