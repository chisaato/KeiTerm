namespace Kei.Term.Core.Models;

// Telnet 是未加密协议；这些参数不携带 SSH 凭据或主机信任设置。
public sealed record TelnetConnectionOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 23;
    public string TerminalType { get; init; } = "xterm-256color";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
