using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class JumpHostPickerTests
{
    private static SessionNode Session(string name, Guid? jumpHostId = null)
        => new()
        {
            Name = name,
            Host = name,
            JumpHostSessionId = jumpHostId
        };

    [Fact]
    public void Selectable_ExcludesSelf()
    {
        var self = Session("self");
        var other = Session("other");

        var result = JumpHostPicker.Selectable(self.Id, [self, other]);

        Assert.DoesNotContain(result, node => node.Id == self.Id);
        Assert.Contains(result, node => node.Id == other.Id);
    }

    [Fact]
    public void Selectable_ExcludesChainThatAlreadyPassesThroughEditingSession()
    {
        var editing = Session("editing");
        var direct = Session("direct", editing.Id);
        var mid = Session("mid", editing.Id);
        var indirect = Session("indirect", mid.Id);
        var unrelated = Session("unrelated");
        // 正在编辑的会话不在传入列表里时，指向它的候选同样要排除
        var pointsAtEditing = Session("points-at-editing", editing.Id);

        var result = JumpHostPicker.Selectable(editing.Id, [direct, mid, indirect, unrelated, pointsAtEditing]);

        Assert.DoesNotContain(result, node => node.Id == direct.Id);
        Assert.DoesNotContain(result, node => node.Id == mid.Id);
        Assert.DoesNotContain(result, node => node.Id == indirect.Id);
        Assert.DoesNotContain(result, node => node.Id == pointsAtEditing.Id);
        Assert.Contains(result, node => node.Id == unrelated.Id);
    }

    [Fact]
    public void Selectable_ExcludesCyclicChain()
    {
        var editing = Session("editing");
        var a = Session("a");
        var b = Session("b", a.Id);
        a.JumpHostSessionId = b.Id;
        var selfLoop = Session("self-loop");
        selfLoop.JumpHostSessionId = selfLoop.Id;
        var ok = Session("ok");

        var result = JumpHostPicker.Selectable(editing.Id, [editing, a, b, selfLoop, ok]);

        Assert.DoesNotContain(result, node => node.Id == a.Id);
        Assert.DoesNotContain(result, node => node.Id == b.Id);
        Assert.DoesNotContain(result, node => node.Id == selfLoop.Id);
        Assert.Contains(result, node => node.Id == ok.Id);
    }

    [Fact]
    public void Selectable_ExcludesChainExceedingMaxDepth()
    {
        var editing = Session("editing");
        var nodes = new List<SessionNode> { Session("h0") };
        for (int i = 1; i <= JumpChainResolver.MaxDepth + 1; i++)
        {
            nodes.Add(Session($"h{i}", nodes[^1].Id));
        }

        // h{MaxDepth} 自身链恰好满层，仍应可选；再多一跳的 h{MaxDepth+1} 超层
        var exactlyFull = nodes[JumpChainResolver.MaxDepth];
        var tooDeep = nodes[^1];
        var all = new List<SessionNode> { editing };
        all.AddRange(nodes);

        var result = JumpHostPicker.Selectable(editing.Id, all);

        Assert.DoesNotContain(result, node => node.Id == tooDeep.Id);
        Assert.Contains(result, node => node.Id == exactlyFull.Id);
    }

    [Fact]
    public void Selectable_KeepsCandidateWhenUpstreamJumpIsMissing()
    {
        var editing = Session("editing");
        var missingId = Guid.NewGuid();
        var directMissing = Session("direct-missing", missingId);
        var hop = Session("hop", missingId);
        var viaMissing = Session("via-missing", hop.Id);

        var result = JumpHostPicker.Selectable(editing.Id, [editing, directMissing, hop, viaMissing]);

        Assert.Contains(result, node => node.Id == directMissing.Id);
        Assert.Contains(result, node => node.Id == hop.Id);
        Assert.Contains(result, node => node.Id == viaMissing.Id);
    }

    [Fact]
    public void Selectable_DoesNotTreatMissingHopBeyondMaxDepthAsSelectable()
    {
        var editing = Session("editing");
        var missingId = Guid.NewGuid();
        // 最外层指向已删除会话；从内到外叠满 MaxDepth 后再多一跳，超层优先于「上游缺失」
        var nodes = new List<SessionNode> { Session("h0", missingId) };
        for (int i = 1; i <= JumpChainResolver.MaxDepth; i++)
        {
            nodes.Add(Session($"h{i}", nodes[^1].Id));
        }

        var tooDeep = nodes[^1];
        var withinDepth = nodes[JumpChainResolver.MaxDepth - 1];
        var all = new List<SessionNode> { editing };
        all.AddRange(nodes);

        var result = JumpHostPicker.Selectable(editing.Id, all);

        Assert.DoesNotContain(result, node => node.Id == tooDeep.Id);
        Assert.Contains(result, node => node.Id == withinDepth.Id);
    }

    [Fact]
    public void Selectable_SortsByNameIgnoringCase()
    {
        var editing = Session("editing");
        var bravo = Session("bravo");
        var alpha = Session("Alpha");
        var charlie = Session("charlie");

        var result = JumpHostPicker.Selectable(editing.Id, [editing, bravo, charlie, alpha]);

        Assert.Equal(["Alpha", "bravo", "charlie"], result.Select(node => node.Name).ToArray());
    }

    [Fact]
    public void Selectable_KeepsLegalChainOfExactlyMaxDepth()
    {
        var editing = Session("editing");
        var nodes = new List<SessionNode> { Session("h0") };
        for (int i = 1; i <= JumpChainResolver.MaxDepth; i++)
        {
            nodes.Add(Session($"h{i}", nodes[^1].Id));
        }

        var full = nodes[^1];
        var all = new List<SessionNode> { editing };
        all.AddRange(nodes);

        var result = JumpHostPicker.Selectable(editing.Id, all);

        Assert.Contains(result, node => node.Id == full.Id);
        Assert.Contains(result, node => node.Id == nodes[0].Id);
        Assert.Equal(nodes.Count, result.Count);
    }
}
