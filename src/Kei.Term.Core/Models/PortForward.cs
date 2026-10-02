namespace Kei.Term.Core.Models;

// 会话上的端口转发。Dynamic 是本机监听，不是出站 SOCKS5 防火墙。
public enum PortForwardMode
{
    Local,
    Remote,
    Dynamic
}

public sealed class PortForward
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string? Name { get; set; }
    public PortForwardMode Mode { get; set; } = PortForwardMode.Local;
    public string BindAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; }
    public string? DestinationHost { get; set; }
    public int? DestinationPort { get; set; }
}

// 同一会话上 bind + listen + mode 重复
public sealed class PortForwardConflictException : Exception
{
    public PortForwardConflictException(string message)
        : base(message)
    {
    }
}
