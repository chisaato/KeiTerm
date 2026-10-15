using System;
using System.Linq;
using System.Net;
using Kei.Term.Core.Models;

namespace Kei.Term.Core.Services;

// 被动接收目录信号，不执行远端命令。首个有效报告的主机名作为本连接的基准，支持 IP/SSH 别名登录。
public sealed class TerminalDirectoryTracker
{
    private string? _reportedHost;

    public string? Directory { get; private set; }
    public string? ReportedHost => RejectedHost == null ? _reportedHost : null;
    public string? RejectedHost { get; private set; }
    public TerminalMultiplexerMode? DetectedMultiplexer { get; private set; }

    public bool AcceptOsc7(string? directory, string? host)
        => Accept(directory, host);

    public bool AcceptTitle(string? title, TerminalMultiplexerMode mode)
    {
        if (mode == TerminalMultiplexerMode.Off || string.IsNullOrEmpty(title)
            || title.Length > 8192 || title.Any(char.IsControl)) return false;

        if (title.StartsWith("kei;tmux;", StringComparison.Ordinal))
        {
            if (mode is not (TerminalMultiplexerMode.Auto or TerminalMultiplexerMode.Tmux)) return false;
            string[] parts = title.Split(';', 5);
            if (parts.Length != 5) return false;
            DetectedMultiplexer = TerminalMultiplexerMode.Tmux;
            // pane_current_path 属于外层服务器；ssh/mosh/telnet 运行时不能拿它冒充内层主机目录。
            if (parts[3] is "ssh" or "mosh" or "mosh-client" or "telnet")
            {
                Directory = null;
                return true;
            }
            return Accept(parts[4], parts[2]);
        }

        if (title.StartsWith("kei;screen;", StringComparison.Ordinal))
        {
            if (mode is not (TerminalMultiplexerMode.Auto or TerminalMultiplexerMode.Screen)) return false;
            string[] parts = title.Split(';', 4);
            if (parts.Length != 4) return false;
            DetectedMultiplexer = TerminalMultiplexerMode.Screen;
            return Accept(parts[3], parts[2]);
        }

        // 兼容原有 tmux host:/absolute/path 与 Debian user@host: /absolute/path 标题。
        int separator = title.IndexOf(":/", StringComparison.Ordinal);
        int padding = 1;
        if (separator < 0)
        {
            separator = title.IndexOf(": /", StringComparison.Ordinal);
            padding = 2;
        }
        if (separator <= 0) return false;
        string host = title[..separator];
        int user = host.LastIndexOf('@');
        if (user >= 0) host = host[(user + 1)..];
        return Accept(title[(separator + padding)..], host);
    }

    private bool Accept(string? directory, string? host)
    {
        if (!IsValidDirectory(directory) || !IsValidHost(host)) return false;
        string normalizedHost = host!.TrimEnd('.').Trim('[', ']');
        if (_reportedHost == null) _reportedHost = normalizedHost;
        else if (!SameHost(_reportedHost, normalizedHost))
        {
            RejectedHost = normalizedHost;
            Directory = null;
            return true;
        }

        RejectedHost = null;
        Directory = directory;
        return true;
    }

    public static bool IsValidDirectory(string? path)
        => path is { Length: > 0 and <= 8192 } && path.StartsWith('/') && !path.Any(char.IsControl);

    private static bool IsValidHost(string? host)
        => !string.IsNullOrWhiteSpace(host) && host.Length <= 255
            && host.Any(char.IsLetterOrDigit)
            && host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']');

    private static bool SameHost(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || !IPAddress.TryParse(left, out _) && !IPAddress.TryParse(right, out _)
                && (!left.Contains('.') || !right.Contains('.'))
                && string.Equals(left.Split('.')[0], right.Split('.')[0], StringComparison.OrdinalIgnoreCase);
}
