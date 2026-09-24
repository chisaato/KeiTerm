using System;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class TreePlacementTests
{
    // 新建落点：文件夹 → 建在其内；会话 → 建在其父级；虚拟根/无选中 → 顶级(null)
    [Fact]
    public void ResolveCreationParent_FolderSelected_ReturnsFolderId()
    {
        var folder = new FolderNode { Id = Guid.NewGuid(), Name = "gzz" };
        Assert.Equal(folder.Id, TreePlacement.ResolveCreationParent(folder));
    }

    [Fact]
    public void ResolveCreationParent_SessionSelected_ReturnsItsParentId()
    {
        var parentId = Guid.NewGuid();
        var session = new SessionNode { Id = Guid.NewGuid(), ParentId = parentId, Name = "web" };
        Assert.Equal(parentId, TreePlacement.ResolveCreationParent(session));
    }

    [Fact]
    public void ResolveCreationParent_VirtualRootOrNone_ReturnsNull()
    {
        Assert.Null(TreePlacement.ResolveCreationParent(new VirtualRootNode()));
        Assert.Null(TreePlacement.ResolveCreationParent(null));
    }
}
