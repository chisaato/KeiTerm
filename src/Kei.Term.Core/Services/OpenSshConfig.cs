namespace Kei.Term.Core.Services;

using Kei.Term.Core.Security;

// ssh_config 中的一个 Host 块（Match 块不支持，解析时整体跳过）
public sealed class SshConfigBlock
{
    public IReadOnlyList<string> Patterns { get; init; } = [];
    // 块内选项按出现顺序保存；键统一小写。同名键可多值（如 IdentityFile）
    public List<KeyValuePair<string, string>> Options { get; } = [];

    // 具体主机别名：不含通配符/否定的模式，可作为会话导入
    public IEnumerable<string> ConcreteAliases => Patterns.Where(p => p.IndexOfAny(['*', '?', '!']) < 0);

    public bool Matches(string alias)
        => KnownHostMatcher.MatchesPattern(string.Join(',', Patterns), alias.ToLowerInvariant());
}

// 某个别名的生效配置（按 OpenSSH 语义合并所有命中块：同一选项首次取得的值生效，IdentityFile 累加）
public sealed record SshConfigHostSettings(
    string Alias,
    string HostName,
    int? Port,
    string? User,
    IReadOnlyList<string> IdentityFiles,
    bool IdentitiesOnly,
    IReadOnlyList<string> ProxyJumps,
    bool HasUnsupportedProxyCommand);

public static class OpenSshConfigParser
{
    private const int MaxIncludeDepth = 8;

    // includeResolver：给定 Include 参数返回被包含文件的内容列表（通配/~ 展开交由宿主）；为空则忽略 Include
    public static IReadOnlyList<SshConfigBlock> Parse(string content, Func<string, IEnumerable<string>>? includeResolver = null)
    {
        List<SshConfigBlock> blocks = [];
        // 首个 Host 之前的选项对所有主机生效，等价于隐式 "Host *"
        var current = new SshConfigBlock { Patterns = ["*"] };
        blocks.Add(current);
        ParseInto(content, includeResolver, blocks, ref current, depth: 0);
        return blocks.Where(b => b.Options.Count > 0 || b.ConcreteAliases.Any()).ToList();
    }

    private static void ParseInto(
        string content,
        Func<string, IEnumerable<string>>? includeResolver,
        List<SshConfigBlock> blocks,
        ref SshConfigBlock current,
        int depth)
    {
        bool inMatchBlock = false;
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            (string key, string value) = SplitKeyValue(line);
            if (key.Length == 0)
            {
                continue;
            }

            switch (key)
            {
                case "host":
                    inMatchBlock = false;
                    current = new SshConfigBlock
                    {
                        Patterns = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Unquote).ToList()
                    };
                    blocks.Add(current);
                    continue;

                case "match":
                    // Match 条件（exec/user/localuser 等）无法静态求值：整块跳过，避免误把条件配置套到所有主机
                    inMatchBlock = true;
                    continue;

                case "include" when includeResolver != null && depth < MaxIncludeDepth && !inMatchBlock:
                    foreach (string included in includeResolver(value))
                    {
                        ParseInto(included, includeResolver, blocks, ref current, depth + 1);
                    }
                    continue;
            }

            if (!inMatchBlock)
            {
                current.Options.Add(new KeyValuePair<string, string>(key, Unquote(value)));
            }
        }
    }

    // 支持 "Key Value" 与 "Key=Value" 两种写法
    private static (string Key, string Value) SplitKeyValue(string line)
    {
        int split = line.IndexOfAny([' ', '\t', '=']);
        if (split <= 0)
        {
            return (string.Empty, string.Empty);
        }

        string key = line[..split].ToLowerInvariant();
        string value = line[split..].TrimStart(' ', '\t', '=').TrimEnd();
        return (key, value);
    }

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    public static SshConfigHostSettings Resolve(IReadOnlyList<SshConfigBlock> blocks, string alias)
    {
        Dictionary<string, string> first = new(StringComparer.Ordinal);
        List<string> identityFiles = [];

        foreach (SshConfigBlock block in blocks.Where(b => b.Matches(alias)))
        {
            foreach ((string key, string value) in block.Options)
            {
                if (key == "identityfile")
                {
                    identityFiles.Add(value);
                }
                else
                {
                    first.TryAdd(key, value);
                }
            }
        }

        string hostName = first.TryGetValue("hostname", out string? hn)
            ? hn.Replace("%h", alias, StringComparison.Ordinal).Replace("%%", "%", StringComparison.Ordinal)
            : alias;
        int? port = first.TryGetValue("port", out string? p) && int.TryParse(p, out int parsed) && parsed is > 0 and <= 65535
            ? parsed
            : null;
        IReadOnlyList<string> jumps = first.TryGetValue("proxyjump", out string? pj) && !string.Equals(pj, "none", StringComparison.OrdinalIgnoreCase)
            ? pj.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return new SshConfigHostSettings(
            alias,
            hostName,
            port,
            first.GetValueOrDefault("user"),
            identityFiles.Where(f => !string.Equals(f, "none", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToList(),
            string.Equals(first.GetValueOrDefault("identitiesonly"), "yes", StringComparison.OrdinalIgnoreCase),
            jumps,
            first.ContainsKey("proxycommand") && jumps.Count == 0);
    }

    // ProxyJump 单项：[user@]host[:port]，或 IPv6 的 [user@][addr]:port
    public static (string? User, string Host, int? Port) ParseJumpSpec(string spec)
    {
        string? user = null;
        int at = spec.LastIndexOf('@');
        string rest = spec;
        if (at > 0)
        {
            user = spec[..at];
            rest = spec[(at + 1)..];
        }

        if (rest.StartsWith('['))
        {
            int close = rest.IndexOf(']');
            if (close > 0)
            {
                string host = rest[1..close];
                int? bracketPort = rest.Length > close + 2 && rest[close + 1] == ':' && int.TryParse(rest[(close + 2)..], out int bp) ? bp : null;
                return (user, host, bracketPort);
            }
        }

        int colon = rest.LastIndexOf(':');
        // 仅一个冒号才视为 host:port（多冒号为裸 IPv6 地址）
        if (colon > 0 && rest.IndexOf(':') == colon && int.TryParse(rest[(colon + 1)..], out int port))
        {
            return (user, rest[..colon], port);
        }

        return (user, rest, null);
    }
}
