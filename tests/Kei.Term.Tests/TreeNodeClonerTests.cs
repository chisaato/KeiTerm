using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class TreeNodeClonerTests
{
    // 构造带子树的测试树：
    // folder(Folder, "Group", IsExpanded=true)
    //   ├─ sub(Folder, "Sub")
    //   │    └─ deep(Session, "Deep", IdentityId, JumpHostSessionId, Env)
    //   └─ direct(Session, "Direct")
    private static (FolderNode folder, FolderNode sub, SessionNode deep, SessionNode direct) BuildTree()
    {
        var identityId = Guid.NewGuid();
        var jumpHostId = Guid.NewGuid();

        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            ParentId = Guid.NewGuid(),
            Name = "Group",
            Description = "desc",
            SortOrder = 7,
            IsExpanded = true
        };

        var sub = new FolderNode { Id = Guid.NewGuid(), ParentId = folder.Id, Name = "Sub" };
        var deep = new SessionNode
        {
            Id = Guid.NewGuid(),
            ParentId = sub.Id,
            Name = "Deep",
            Host = "10.0.0.9",
            Port = 2200,
            Username = "ops",
            IdentityId = identityId,
            Protocol = "ssh",
            TerminalType = "xterm",
            StartupScript = "echo hi",
            JumpHostSessionId = jumpHostId
        };
        deep.EnvironmentVariables["LANG"] = "zh_CN.UTF-8";

        var direct = new SessionNode
        {
            Id = Guid.NewGuid(),
            ParentId = folder.Id,
            Name = "Direct",
            Host = "10.0.0.2"
        };

        folder.Children = [sub, direct];
        sub.Children = [deep];

        return (folder, sub, deep, direct);
    }

    // 深度优先收集整棵克隆树的所有节点
    private static List<TreeNodeBase> Collect(TreeNodeBase node)
    {
        var list = new List<TreeNodeBase> { node };
        if (node is FolderNode folder)
        {
            foreach (var child in folder.Children)
            {
                list.AddRange(Collect(child));
            }
        }

        return list;
    }

    [Fact]
    public void DeepClone_GeneratesDistinctNewIds()
    {
        var (folder, sub, deep, direct) = BuildTree();
        var originalIds = new[] { folder.Id, sub.Id, deep.Id, direct.Id };

        var clone = TreeNodeCloner.DeepClone(folder);
        var cloneIds = Collect(clone).Select(n => n.Id).ToList();

        // 所有克隆 Id 互不相同
        Assert.Equal(4, cloneIds.Count);
        Assert.Equal(4, cloneIds.Distinct().Count());
        // 且都不等于任何原 Id
        Assert.All(cloneIds, id => Assert.DoesNotContain(id, originalIds));
    }

    [Fact]
    public void DeepClone_KeepsRootParentId_AndRewiresChildParentIds()
    {
        var (folder, sub, deep, direct) = BuildTree();
        var before = DateTime.UtcNow;

        var clone = Assert.IsType<FolderNode>(TreeNodeCloner.DeepClone(folder));

        var after = DateTime.UtcNow;

        // 克隆根的 ParentId 保持原值
        Assert.Equal(folder.ParentId, clone.ParentId);

        var cloneSub = Assert.IsType<FolderNode>(clone.Children.Single(c => c.Name == "Sub"));
        var cloneDeep = Assert.IsType<SessionNode>(cloneSub.Children.Single());
        var cloneDirect = Assert.IsType<SessionNode>(clone.Children.Single(c => c.Name == "Direct"));

        // 子节点 ParentId 指向各自新父的 Id
        Assert.Equal(clone.Id, cloneSub.ParentId);
        Assert.Equal(cloneSub.Id, cloneDeep.ParentId);
        Assert.Equal(clone.Id, cloneDirect.ParentId);

        // CreatedAt/UpdatedAt 均重置为克隆时刻
        Assert.All(Collect(clone), n =>
        {
            Assert.InRange(n.CreatedAt, before, after);
            Assert.InRange(n.UpdatedAt, before, after);
        });
    }

    [Fact]
    public void DeepClone_PreservesRuntimeTypes_AndScalarFields()
    {
        var (folder, sub, deep, direct) = BuildTree();

        var clone = Assert.IsType<FolderNode>(TreeNodeCloner.DeepClone(folder));

        // FolderNode 递归克隆，子树完整且运行时类型保持
        var cloneSub = Assert.IsType<FolderNode>(clone.Children.Single(c => c.Name == "Sub"));
        var cloneDeep = Assert.IsType<SessionNode>(cloneSub.Children.Single());
        var cloneDirect = Assert.IsType<SessionNode>(clone.Children.Single(c => c.Name == "Direct"));

        // 基础标量字段原样保留
        Assert.Equal(folder.Name, clone.Name);
        Assert.Equal(folder.Description, clone.Description);
        Assert.Equal(folder.SortOrder, clone.SortOrder);
        Assert.Equal(deep.Host, cloneDeep.Host);
        Assert.Equal(deep.Port, cloneDeep.Port);
        Assert.Equal(deep.Username, cloneDeep.Username);
        Assert.Equal(deep.TerminalType, cloneDeep.TerminalType);
        Assert.Equal(deep.StartupScript, cloneDeep.StartupScript);

        // IsExpanded 重置为 false
        Assert.False(clone.IsExpanded);

        // 环境变量内容一致且为独立字典实例
        Assert.NotSame(deep.EnvironmentVariables, cloneDeep.EnvironmentVariables);
        Assert.Equal(deep.EnvironmentVariables, cloneDeep.EnvironmentVariables);
    }

    [Fact]
    public void DeepClone_PreservesCrossReferences()
    {
        var (folder, sub, deep, direct) = BuildTree();

        var clone = Assert.IsType<FolderNode>(TreeNodeCloner.DeepClone(folder));
        var cloneSub = Assert.IsType<FolderNode>(clone.Children.Single(c => c.Name == "Sub"));
        var cloneDeep = Assert.IsType<SessionNode>(cloneSub.Children.Single());

        // IdentityId / JumpHostSessionId / Protocol 为跨节点/会话属性，原样保留（禁止重新生成）
        Assert.Equal(deep.IdentityId, cloneDeep.IdentityId);
        Assert.Equal(deep.JumpHostSessionId, cloneDeep.JumpHostSessionId);
        Assert.Equal(deep.Protocol, cloneDeep.Protocol);
    }

    [Fact]
    public void DeepClone_SessionRoot_KeepsOriginalParentId()
    {
        var (folder, sub, deep, direct) = BuildTree();

        var clone = Assert.IsType<SessionNode>(TreeNodeCloner.DeepClone(deep));

        // 会话节点作为克隆根时同样保持原 ParentId 与标量字段
        Assert.Equal(deep.ParentId, clone.ParentId);
        Assert.NotEqual(deep.Id, clone.Id);
        Assert.Equal(deep.Host, clone.Host);
        Assert.Equal(deep.Name, clone.Name);
    }

    [Fact]
    public void DeepClone_KeepsProfileTransferAndOverrides_WithIndependentOverrides()
    {
        var session = new SessionNode
        {
            Name = "S",
            Host = "h",
            TerminalProfileId = "solarized",
            FileTransferProtocol = FileTransferProtocol.Scp,
            SftpMode = SftpChannelMode.Dedicated,
            Overrides = new SessionOverrides { FollowRemoteTitle = false, CwdFollow = CwdFollowMode.Always }
        };

        var clone = Assert.IsType<SessionNode>(TreeNodeCloner.DeepClone(session));

        // 粘贴出的会话不应悄悄变回默认配色 / SFTP / 继承
        Assert.Equal("solarized", clone.TerminalProfileId);
        Assert.Equal(FileTransferProtocol.Scp, clone.FileTransferProtocol);
        Assert.Equal(SftpChannelMode.Dedicated, clone.SftpMode);
        Assert.False(clone.Overrides.FollowRemoteTitle);
        Assert.Equal(CwdFollowMode.Always, clone.Overrides.CwdFollow);

        // 改副本的覆盖项不影响原会话
        clone.Overrides.CwdFollow = CwdFollowMode.Off;
        Assert.Equal(CwdFollowMode.Always, session.Overrides.CwdFollow);
    }
}
