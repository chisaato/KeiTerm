namespace Kei.Term.Core.Services;

using Kei.Term.Core.Models;

// 新建/粘贴落点判定：纯静态、可单测
public static class TreePlacement
{
    // 文件夹 → 建在其内；会话 → 建在其父级；虚拟根或无选中 → 顶级(null)
    public static Guid? ResolveCreationParent(TreeNodeBase? selected)
    {
        return selected switch
        {
            FolderNode folder => folder.Id,
            SessionNode session => session.ParentId,
            _ => null // VirtualRootNode / null / 未知类型
        };
    }
}
