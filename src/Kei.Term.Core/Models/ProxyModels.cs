namespace Kei.Term.Core.Models;

// 全局代理档案。口令不进模型、不进 config_json，只进 proxy_secrets。HTTP 只建档不拨号。
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

// Username 不是机密。口令不在这条记录上。
public sealed record Socks5ProxyConfig(string Host, int Port, string? Username = null) : ProxyConfig;

public sealed record HttpProxyConfig(string Host, int Port) : ProxyConfig;

public sealed record SessionProxyConfig(Guid SessionId) : ProxyConfig;

// 会话编辑器里代理类型的选择，与持久化 type 一一对应
public enum ProxyConfigKind
{
    Socks5,
    Http,
    Session
}
