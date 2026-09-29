namespace Kei.Term.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.Core.Models;

// 基于 parent_id 扁平节点表的遍历（不依赖已组装的 Children）
public static class TreeTraversal
{
    // 文件夹下全部层级的会话 Id；对环形 parent 引用免疫
    public static IReadOnlyList<Guid> DescendantSessionIds(Guid folderId, IEnumerable<TreeNodeBase> allNodes)
    {
        ILookup<Guid?, TreeNodeBase> children = allNodes.ToLookup(n => n.ParentId);
        var result = new List<Guid>();
        var visited = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(folderId);
        while (pending.Count > 0)
        {
            Guid current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (TreeNodeBase child in children[current])
            {
                if (child is SessionNode)
                {
                    result.Add(child.Id);
                }
                else
                {
                    pending.Push(child.Id);
                }
            }
        }

        return result;
    }
}
