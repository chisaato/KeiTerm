namespace Kei.Term.Core.Models;

// 全局代理档案。口令不进模型：本轮 SOCKS5 无认证，HTTP 只建档不拨号。
public sealed class ProxyProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public ProxyConfig Config { get; set; } = new Socks5ProxyConfig("127.0.0.1", 1080);
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public abstract record ProxyConfig;

public sealed record Socks5ProxyConfig(string Host, int Port) : ProxyConfig;

public sealed record HttpProxyConfig(string Host, int Port) : ProxyConfig;

public sealed record SessionProxyConfig(Guid SessionId) : ProxyConfig;

// 会话编辑器里代理类型的选择，与持久化 type 一一对应
public enum ProxyConfigKind
{
    Socks5,
    Http,
    Session
}
