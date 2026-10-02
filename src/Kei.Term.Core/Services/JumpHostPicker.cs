namespace Kei.Term.Core.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.Core.Models;

// 会话编辑器跳板下拉的候选筛选。只看传入的会话列表，不查库。
public static class JumpHostPicker
{
    public static IReadOnlyList<SessionNode> Selectable(Guid editingId, IReadOnlyList<SessionNode> sessions)
    {
        var lookup = new Dictionary<Guid, SessionNode>(sessions.Count);
        foreach (var session in sessions)
        {
            // 重复 Id 保留先出现的节点，避免字典抛错打断下拉
            lookup.TryAdd(session.Id, session);
        }

        return sessions
            .Where(session => IsSelectable(session, editingId, lookup))
            .OrderBy(session => session.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    // 沿候选自己的 JumpHostSessionId 走。不把「再挂到正在编辑的会话上」算进层数。
    private static bool IsSelectable(
        SessionNode candidate,
        Guid editingId,
        Dictionary<Guid, SessionNode> lookup)
    {
        if (candidate.Id == editingId)
        {
            return false;
        }

        // 与 JumpChainResolver 一样，起点算已访问，自跳即循环
        var visited = new HashSet<Guid> { candidate.Id };
        Guid? next = candidate.JumpHostSessionId;
        var recorded = 0;

        while (next is Guid jumpId)
        {
            // 链已经经过正在编辑的会话：选中后会立刻成环
            if (jumpId == editingId)
            {
                return false;
            }

            if (!visited.Add(jumpId))
            {
                return false;
            }

            // 已记录的跳板数达到 MaxDepth 仍有下一跳 = 超层；下一跳是否缺失都要排除
            if (recorded >= JumpChainResolver.MaxDepth)
            {
                return false;
            }

            if (!lookup.TryGetValue(jumpId, out var jump))
            {
                // 上游已删除：解析时会失败，但下拉仍允许选
                return true;
            }

            recorded++;
            next = jump.JumpHostSessionId;
        }

        return true;
    }
}
