namespace Kei.Term.Core.Services;

using Kei.Term.Core.Models;

// 会话管理器树过滤：纯静态、可单测
public static class TreeFilter
{
    // 从扁平节点集合中选出 Name（SessionNode 额外匹配 Host）大小写不敏感包含 query 的节点及其全部祖先；
    // query 为 null/空白时返回包含全部节点的列表；返回元素均为原实例，不修改原集合与节点
    public static List<TreeNodeBase> Select(IReadOnlyList<TreeNodeBase> allNodes, string? query)
    {
        // 无有效关键字时直接返回全量副本列表
        if (string.IsNullOrWhiteSpace(query))
        {
            return [.. allNodes];
        }

        // 建立 Id -> 节点 映射，用于沿 ParentId 回溯祖先链
        var byId = new Dictionary<Guid, TreeNodeBase>(allNodes.Count);
        foreach (var node in allNodes)
        {
            byId[node.Id] = node;
        }

        // 收集命中节点与其祖先的 Id 集合
        var matched = new HashSet<Guid>();
        foreach (var node in allNodes)
        {
            var isHit = node.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (node is SessionNode session && session.Host.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (!isHit)
            {
                continue;
            }

            // 沿祖先链逐级入选；若节点已入选则其祖先必然已入选，可提前终止（同时防环）
            var cursor = (TreeNodeBase?)node;
            while (cursor != null && matched.Add(cursor.Id))
            {
                cursor = cursor.ParentId is { } parentId ? byId.GetValueOrDefault(parentId) : null;
            }
        }

        // 保持原集合顺序输出结果
        var result = new List<TreeNodeBase>(matched.Count);
        foreach (var node in allNodes)
        {
            if (matched.Contains(node.Id))
            {
                result.Add(node);
            }
        }

        return result;
    }
}
