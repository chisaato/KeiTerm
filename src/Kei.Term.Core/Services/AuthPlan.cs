namespace Kei.Term.Core.Services;

using Kei.Term.Core.Vault;

// 认证尝试序列的步骤：显式方法 → Agent 兜底 → 单次弹窗兜底
public abstract record AuthStep;

// Identity 内显式声明的认证方法（已按 SortOrder 排定）
public sealed record MethodStep(AuthMethodEntry Method) : AuthStep;

// 未绑定 Identity 且全局 PreferSystemAgent 开启时的 Agent 全量身份尝试
public sealed record AgentFallbackStep : AuthStep;

// 单次认证弹窗兜底；SuggestedUsername 供 UI 预填（仅内存）
public sealed record SingleUsePromptStep(string? SuggestedUsername) : AuthStep;

public static class AuthPlanBuilder
{
    // 规则：显式优先，环境补充，交互兜底
    // 有 methods → 过滤 Enabled + 按 SortOrder 排序映射 MethodStep，末尾恒为单次弹窗；
    // 无 methods → 视 preferAgentFallback 决定是否加 AgentFallbackStep，再加单次弹窗。
    public static IReadOnlyList<AuthStep> Plan(
        IReadOnlyList<AuthMethodEntry>? methods,
        bool preferAgentFallback,
        string? identityUsername = null)
    {
        var steps = new List<AuthStep>();
        var hasMethods = methods is { Count: > 0 };

        if (hasMethods)
        {
            // 显式方法永远优先：过滤禁用项后按 SortOrder 升序
            foreach (var method in methods!.Where(m => m.Enabled).OrderBy(m => m.SortOrder))
            {
                steps.Add(new MethodStep(method));
            }
        }
        else if (preferAgentFallback)
        {
            // 无 Identity 时才由全局开关注入 Agent 兜底
            steps.Add(new AgentFallbackStep());
        }

        // 末尾恒为单次认证弹窗兜底；有 Identity 时预填其 Username
        steps.Add(new SingleUsePromptStep(hasMethods ? identityUsername : null));
        return steps;
    }
}
