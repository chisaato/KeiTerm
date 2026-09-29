namespace Kei.Term.Core.Services;

using Kei.Term.Core.Models;

public sealed class JumpChainException : Exception
{
    public JumpChainException(string message)
        : base(message)
    {
    }
}

// 跳板链解析：目标 T 的跳板为 J1，J1 自身又经 J2 到达 → 连接顺序为 J2 → J1 → T。
// 跳板会话本身也是普通会话节点，可复用其身份、端口等全部配置（等价 OpenSSH 多级 ProxyJump）。
public static class JumpChainResolver
{
    // 超过该层数基本可以断定是配置错误；同时限制一次连接最多弹出的认证窗数量
    public const int MaxDepth = 8;

    // 返回由外到内的跳板列表（不含目标自身）；无跳板返回空列表
    public static IReadOnlyList<SessionNode> Resolve(SessionNode target, Func<Guid, SessionNode?> lookup)
    {
        List<SessionNode> innerToOuter = [];
        HashSet<Guid> visited = [target.Id];
        Guid? next = target.JumpHostSessionId;

        while (next is { } jumpId)
        {
            if (!visited.Add(jumpId))
            {
                throw new JumpChainException($"跳板链存在循环引用: {target.Name}");
            }

            if (innerToOuter.Count >= MaxDepth)
            {
                throw new JumpChainException($"跳板链超过最大层数 {MaxDepth}: {target.Name}");
            }

            SessionNode jump = lookup(jumpId)
                ?? throw new JumpChainException($"跳板会话不存在或已删除: {jumpId}");
            innerToOuter.Add(jump);
            next = jump.JumpHostSessionId;
        }

        innerToOuter.Reverse();
        return innerToOuter;
    }
}
