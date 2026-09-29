using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class JumpChainResolverTests
{
    private static Func<Guid, SessionNode?> Lookup(params SessionNode[] nodes)
    {
        var map = nodes.ToDictionary(n => n.Id);
        return id => map.TryGetValue(id, out var node) ? node : null;
    }

    [Fact]
    public void NoJumpHost_ReturnsEmpty()
        => Assert.Empty(JumpChainResolver.Resolve(new SessionNode { Name = "t" }, Lookup()));

    [Fact]
    public void MultiLevel_ReturnsOuterToInner()
    {
        var outer = new SessionNode { Name = "outer" };
        var inner = new SessionNode { Name = "inner", JumpHostSessionId = outer.Id };
        var target = new SessionNode { Name = "target", JumpHostSessionId = inner.Id };

        var chain = JumpChainResolver.Resolve(target, Lookup(outer, inner, target));

        Assert.Equal(new[] { "outer", "inner" }, chain.Select(n => n.Name));
    }

    [Fact]
    public void Cycle_Throws()
    {
        var a = new SessionNode { Name = "a" };
        var b = new SessionNode { Name = "b", JumpHostSessionId = a.Id };
        a.JumpHostSessionId = b.Id;
        var target = new SessionNode { Name = "t", JumpHostSessionId = a.Id };

        Assert.Throws<JumpChainException>(() => JumpChainResolver.Resolve(target, Lookup(a, b, target)));
    }

    [Fact]
    public void SelfReference_Throws()
    {
        var target = new SessionNode { Name = "t" };
        target.JumpHostSessionId = target.Id;

        Assert.Throws<JumpChainException>(() => JumpChainResolver.Resolve(target, Lookup(target)));
    }

    [Fact]
    public void MissingJumpHost_Throws()
    {
        var target = new SessionNode { Name = "t", JumpHostSessionId = Guid.NewGuid() };

        Assert.Throws<JumpChainException>(() => JumpChainResolver.Resolve(target, Lookup(target)));
    }

    [Fact]
    public void ExceedingMaxDepth_Throws()
    {
        var nodes = new List<SessionNode> { new() { Name = "h0" } };
        for (int i = 1; i <= JumpChainResolver.MaxDepth + 1; i++)
        {
            nodes.Add(new SessionNode { Name = $"h{i}", JumpHostSessionId = nodes[^1].Id });
        }

        var target = new SessionNode { Name = "t", JumpHostSessionId = nodes[^1].Id };
        nodes.Add(target);

        Assert.Throws<JumpChainException>(() => JumpChainResolver.Resolve(target, Lookup(nodes.ToArray())));
    }
}
