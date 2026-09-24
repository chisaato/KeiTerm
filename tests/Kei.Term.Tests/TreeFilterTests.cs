using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class TreeFilterTests
{
    // 构造固定测试树：
    // root(Folder, "Root")
    //   ├─ prod(Folder, "Production")
    //   │    ├─ web(Session, "Web-1", Host=10.0.0.1)
    //   │    └─ db(Session, "DB-Main", Host=db.internal)
    //   └─ local(Session, "Local", Host=127.0.0.1)
    private static (FolderNode root, FolderNode prod, SessionNode web, SessionNode db, SessionNode local) BuildTree()
    {
        var root = new FolderNode { Id = Guid.NewGuid(), Name = "Root" };
        var prod = new FolderNode { Id = Guid.NewGuid(), ParentId = root.Id, Name = "Production" };
        var web = new SessionNode { Id = Guid.NewGuid(), ParentId = prod.Id, Name = "Web-1", Host = "10.0.0.1" };
        var db = new SessionNode { Id = Guid.NewGuid(), ParentId = prod.Id, Name = "DB-Main", Host = "db.internal" };
        var local = new SessionNode { Id = Guid.NewGuid(), ParentId = root.Id, Name = "Local", Host = "127.0.0.1" };

        root.Children = [prod, local];
        prod.Children = [web, db];

        return (root, prod, web, db, local);
    }

    // 将测试树展开为扁平节点集合（模拟仓库全量加载结果）
    private static List<TreeNodeBase> Flatten(
        TreeNodeBase root, TreeNodeBase prod, TreeNodeBase web, TreeNodeBase db, TreeNodeBase local)
    {
        return [root, prod, web, db, local];
    }

    [Fact]
    public void Select_NullOrWhitespaceQuery_ReturnsAllNodes()
    {
        var (root, prod, web, db, local) = BuildTree();
        var all = Flatten(root, prod, web, db, local);

        var nullResult = TreeFilter.Select(all, null);
        var whitespaceResult = TreeFilter.Select(all, "   ");

        // null 与空白关键字均返回全部节点
        Assert.Equal(5, nullResult.Count);
        Assert.Equal(5, whitespaceResult.Count);
        Assert.All(nullResult, n => Assert.Contains(n, all));
    }

    [Fact]
    public void Select_MatchByName_ReturnsHitAndAncestorsOnly()
    {
        var (root, prod, web, db, local) = BuildTree();
        var all = Flatten(root, prod, web, db, local);

        var result = TreeFilter.Select(all, "Web-1");

        // 命中节点与祖先链 root -> prod 一并返回，保持原集合顺序
        Assert.Equal(3, result.Count);
        Assert.Same(root, result[0]);
        Assert.Same(prod, result[1]);
        Assert.Same(web, result[2]);
        Assert.DoesNotContain(db, result);
        Assert.DoesNotContain(local, result);
    }

    [Fact]
    public void Select_SessionHostMatch_ReturnsSessionAndAncestors()
    {
        var (root, prod, web, db, local) = BuildTree();
        var all = Flatten(root, prod, web, db, local);

        // Name 不含关键字，仅 Host 命中
        var result = TreeFilter.Select(all, "10.0.0.1");

        Assert.Equal(3, result.Count);
        Assert.Same(web, result.Single(n => n is SessionNode && n.Name == "Web-1"));
        Assert.Contains(root, result);
        Assert.Contains(prod, result);
        Assert.DoesNotContain(db, result);
        Assert.DoesNotContain(local, result);
    }

    [Fact]
    public void Select_IsCaseInsensitive()
    {
        var (root, prod, web, db, local) = BuildTree();
        var all = Flatten(root, prod, web, db, local);

        // Name 匹配对大小写不敏感
        Assert.Contains(prod, TreeFilter.Select(all, "production"));
        Assert.Contains(web, TreeFilter.Select(all, "WEB-1"));
        // Host 匹配同样大小写不敏感
        Assert.Contains(db, TreeFilter.Select(all, "DB.INTERNAL"));
    }

    [Fact]
    public void Select_DeepSessionHit_ReturnsCompleteAncestorChain()
    {
        var (root, prod, web, db, local) = BuildTree();
        var all = Flatten(root, prod, web, db, local);

        var result = TreeFilter.Select(all, "DB-Main");

        // 深层命中时祖先链 root -> prod 完整返回，且不含兄弟节点
        Assert.Equal(3, result.Count);
        Assert.Contains(root, result);
        Assert.Contains(prod, result);
        Assert.Contains(db, result);
        Assert.DoesNotContain(web, result);
        Assert.DoesNotContain(local, result);
    }

    [Fact]
    public void Select_NoMatch_ReturnsEmptyList()
    {
        var (root, prod, web, db, local) = BuildTree();
        var all = Flatten(root, prod, web, db, local);

        var result = TreeFilter.Select(all, "no-such-keyword");

        Assert.Empty(result);
    }

    [Fact]
    public void Select_DoesNotMutateSourceCollectionOrNodes()
    {
        var (root, prod, web, db, local) = BuildTree();
        var all = Flatten(root, prod, web, db, local);
        var originalNames = all.Select(n => n.Name).ToList();
        var originalParentIds = all.Select(n => n.ParentId).ToList();

        var result = TreeFilter.Select(all, "Web-1");

        // 原集合数量不变，且结果元素均为原实例
        Assert.Equal(5, all.Count);
        Assert.All(result, n => Assert.Contains(n, all));
        // 节点字段未被修改
        Assert.Equal(originalNames, all.Select(n => n.Name).ToList());
        Assert.Equal(originalParentIds, all.Select(n => n.ParentId).ToList());
    }
}
