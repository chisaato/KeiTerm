using System;
using System.Collections.Generic;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class TreeDropResolverTests
{
    // root(Folder)
    //   ├─ prod(Folder)
    //   │    └─ web(Session)
    //   └─ local(Session)
    private static (FolderNode root, FolderNode prod, SessionNode web, SessionNode local) Build()
    {
        var root = new FolderNode { Id = Guid.NewGuid(), Name = "Root" };
        var prod = new FolderNode { Id = Guid.NewGuid(), ParentId = root.Id, Name = "Production" };
        var web = new SessionNode { Id = Guid.NewGuid(), ParentId = prod.Id, Name = "Web-1", Host = "10.0.0.1" };
        var local = new SessionNode { Id = Guid.NewGuid(), ParentId = root.Id, Name = "Local", Host = "127.0.0.1" };
        root.Children = [prod, local];
        prod.Children = [web];
        return (root, prod, web, local);
    }

    private static List<TreeNodeBase> Flatten(TreeNodeBase[] nodes) => [.. nodes];

    [Fact]
    public void Resolve_TargetFolder_ReturnsFolderId()
    {
        var (root, prod, web, local) = Build();
        var result = TreeDropResolver.Resolve(Flatten([root, prod, web, local]), local.Id, prod);
        Assert.True(result.IsValid);
        Assert.Equal(prod.Id, result.NewParentId);
    }

    [Fact]
    public void Resolve_TargetSession_ReturnsSessionParentId()
    {
        var (root, prod, web, local) = Build();
        var result = TreeDropResolver.Resolve(Flatten([root, prod, web, local]), local.Id, web);
        Assert.True(result.IsValid);
        Assert.Equal(prod.Id, result.NewParentId);
    }

    [Fact]
    public void Resolve_TargetVirtualRootOrBlank_ReturnsTopLevel()
    {
        var (root, prod, web, local) = Build();
        var all = Flatten([root, prod, web, local]);
        Assert.Null(TreeDropResolver.Resolve(all, local.Id, new VirtualRootNode()).NewParentId);
        Assert.Null(TreeDropResolver.Resolve(all, local.Id, null).NewParentId);
    }

    [Fact]
    public void Resolve_DropOntoSelf_IsInvalid()
    {
        var (root, prod, web, local) = Build();
        var result = TreeDropResolver.Resolve(Flatten([root, prod, web, local]), prod.Id, prod);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Resolve_DropOntoOwnDescendant_IsInvalid()
    {
        var (root, prod, web, local) = Build();
        // prod 拖到自己的子孙 web 上 → 成环，非法
        var result = TreeDropResolver.Resolve(Flatten([root, prod, web, local]), prod.Id, web);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Resolve_SameParentAsCurrent_IsNoOp()
    {
        var (root, prod, web, local) = Build();
        // local 本来就在 root 下，再拖到 root → 有效但为空操作
        var result = TreeDropResolver.Resolve(Flatten([root, prod, web, local]), local.Id, root);
        Assert.True(result.IsValid);
        Assert.True(result.IsNoOp);
    }

    [Fact]
    public void Resolve_DraggedNodeMissing_IsInvalid()
    {
        var (root, prod, web, local) = Build();
        var result = TreeDropResolver.Resolve(Flatten([root, prod, web, local]), Guid.NewGuid(), prod);
        Assert.False(result.IsValid);
    }
}
