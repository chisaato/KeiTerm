namespace Kei.Term.Core.Services;

using Kei.Term.Core.Models;

// 拖拽落点解析结果
public sealed record TreeDropResult(bool IsValid, Guid? NewParentId, bool IsNoOp);

// 拖拽落点判定：纯静态、可单测（防环 + 落点映射）
public static class TreeDropResolver
{
    // target 为 null（空白处）或虚拟根 → 移到顶级；
    // 目标为会话 → 移到该会话同级；目标为文件夹 → 移入其下；
    // 目标为自身或自身子孙 → 非法（成环）；新父与现父相同 → 有效但 IsNoOp
    public static TreeDropResult Resolve(IReadOnlyList<TreeNodeBase> allNodes, Guid draggedId, TreeNodeBase? target)
    {
        var byId = new Dictionary<Guid, TreeNodeBase>(allNodes.Count);
        foreach (var node in allNodes)
        {
            byId[node.Id] = node;
        }

        if (!byId.TryGetValue(draggedId, out var dragged) || dragged is VirtualRootNode)
        {
            return new TreeDropResult(false, null, false);
        }

        // 落点映射
        Guid? newParentId = target switch
        {
            null => null,
            VirtualRootNode => null,
            FolderNode folder => folder.Id,
            SessionNode session => session.ParentId,
            _ => null
        };

        // 目标即自身 → 非法
        if (target != null && target.Id == draggedId)
        {
            return new TreeDropResult(false, null, false);
        }

        // 目标位于自身子孙内 → 非法（沿目标祖先链上溯，遇到 dragged 即成环）
        var cursor = target;
        while (cursor != null)
        {
            if (cursor.Id == draggedId)
            {
                return new TreeDropResult(false, null, false);
            }
            cursor = cursor.ParentId is { } pid ? byId.GetValueOrDefault(pid) : null;
        }

        var isNoOp = dragged.ParentId == newParentId;
        return new TreeDropResult(true, newParentId, isNoOp);
    }
}
