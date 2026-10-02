namespace Kei.Term.Core.Services;

using Kei.Term.Core.Models;

// 一条会话的出口裁决。连接路径只认这个结果，不再读 jump_host_id。
public static class SessionProxyExit
{
    // 错误文本必须能互相区分：一边含「尚未接入」，另一边含「已删除」，且不混用
    public const string NotWiredMessage = "此代理类型尚未接入拨号";
    public const string DeletedMessage = "代理已删除";

    public static SessionProxyDecision Resolve(Guid? proxyId, Func<Guid, ProxyProfile?> findProxy)
    {
        if (proxyId is not Guid id)
        {
            return SessionProxyDecision.None();
        }

        ProxyProfile? proxy = findProxy(id);
        if (proxy == null)
        {
            return SessionProxyDecision.Failed(DeletedMessage);
        }

        return proxy.Config switch
        {
            Socks5ProxyConfig socks when IsEndpoint(socks.Host, socks.Port)
                => SessionProxyDecision.Socks5(socks.Host.Trim(), socks.Port),
            HttpProxyConfig => SessionProxyDecision.Failed(NotWiredMessage),
            SessionProxyConfig session => SessionProxyDecision.Jump(session.SessionId),
            _ => SessionProxyDecision.Failed(NotWiredMessage)
        };
    }

    public static bool IsEndpoint(string? host, int port)
        => !string.IsNullOrWhiteSpace(host) && port is >= 1 and <= 65535;
}

public abstract record SessionProxyDecision
{
    public static SessionProxyDecision None() => new NoneDecision();
    public static SessionProxyDecision Failed(string message) => new FailedDecision(message);
    public static SessionProxyDecision Socks5(string host, int port) => new Socks5Decision(host, port);
    public static SessionProxyDecision Jump(Guid sessionId) => new JumpDecision(sessionId);
}

public sealed record NoneDecision : SessionProxyDecision;

public sealed record FailedDecision(string Message) : SessionProxyDecision;

public sealed record Socks5Decision(string Host, int Port) : SessionProxyDecision;

public sealed record JumpDecision(Guid SessionId) : SessionProxyDecision;
