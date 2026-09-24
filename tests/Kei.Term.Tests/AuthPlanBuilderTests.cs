using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;
using Xunit;

namespace Kei.Term.Tests;

// 认证计划构建：有/无 Identity × PreferSystemAgent 四象限 + Enabled/排序
public class AuthPlanBuilderTests
{
    private static List<AuthMethodEntry> SampleMethods() =>
    [
        new VaultPasswordMethod { Id = Guid.NewGuid(), SortOrder = 2 },
        new InteractiveMethod { Id = Guid.NewGuid(), SortOrder = 0 },
        new FilePrivateKeyMethod { Id = Guid.NewGuid(), SortOrder = 1, KeyFilePath = "/tmp/id" },
        new AgentMethod { Id = Guid.NewGuid(), SortOrder = 3, Enabled = false }
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Plan_WithMethods_IgnoresAgentFallback(bool preferAgentFallback)
    {
        var methods = SampleMethods();

        var plan = AuthPlanBuilder.Plan(methods, preferAgentFallback, "alice");

        // 显式方法优先：禁用项被过滤，其余按 SortOrder 升序
        Assert.DoesNotContain(plan, s => s is AgentFallbackStep);
        var methodSteps = plan.OfType<MethodStep>().ToList();
        Assert.Equal(3, methodSteps.Count);
        Assert.Equal(new[] { 0, 1, 2 }, methodSteps.Select(s => s.Method.SortOrder).ToArray());
        Assert.All(methodSteps, s => Assert.True(s.Method.Enabled));
        // 末尾恒为单次弹窗兜底，且预填身份用户名
        Assert.IsType<SingleUsePromptStep>(plan[^1]);
        Assert.Equal("alice", ((SingleUsePromptStep)plan[^1]).SuggestedUsername);
    }

    [Fact]
    public void Plan_WithMethods_AllDisabled_OnlyPromptFallback()
    {
        var methods = new List<AuthMethodEntry>
        {
            new VaultPasswordMethod { SortOrder = 0, Enabled = false },
            new AgentMethod { SortOrder = 1, Enabled = false }
        };

        var plan = AuthPlanBuilder.Plan(methods, preferAgentFallback: true, identityUsername: "bob");

        Assert.Single(plan);
        Assert.Equal("bob", ((SingleUsePromptStep)plan[0]).SuggestedUsername);
    }

    [Fact]
    public void Plan_NoMethods_PreferAgent_AddsAgentFallbackThenPrompt()
    {
        var plan = AuthPlanBuilder.Plan(methods: null, preferAgentFallback: true);

        Assert.Equal(2, plan.Count);
        Assert.IsType<AgentFallbackStep>(plan[0]);
        var prompt = Assert.IsType<SingleUsePromptStep>(plan[1]);
        // 无 Identity 时无可预填用户名
        Assert.Null(prompt.SuggestedUsername);
    }

    [Fact]
    public void Plan_EmptyMethods_PreferAgent_AddsAgentFallback()
    {
        var plan = AuthPlanBuilder.Plan(Array.Empty<AuthMethodEntry>(), preferAgentFallback: true);

        Assert.Equal(2, plan.Count);
        Assert.IsType<AgentFallbackStep>(plan[0]);
        Assert.IsType<SingleUsePromptStep>(plan[1]);
    }

    [Fact]
    public void Plan_NoMethods_NoAgent_PromptOnly()
    {
        var plan = AuthPlanBuilder.Plan(methods: null, preferAgentFallback: false);

        Assert.Single(plan);
        var prompt = Assert.IsType<SingleUsePromptStep>(plan[0]);
        Assert.Null(prompt.SuggestedUsername);
    }

    [Fact]
    public void Plan_WithMethods_DoesNotPreFillUsername_WhenNotProvided()
    {
        var plan = AuthPlanBuilder.Plan(SampleMethods(), preferAgentFallback: false);

        Assert.Null(((SingleUsePromptStep)plan[^1]).SuggestedUsername);
    }
}
