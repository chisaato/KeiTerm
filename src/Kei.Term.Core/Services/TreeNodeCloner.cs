namespace Kei.Term.Core.Services;

using Kei.Term.Core.Models;

// 节点复制/粘贴用深拷贝：纯静态、可单测
public static class TreeNodeCloner
{
    // 深拷贝 node 及其整个子树；克隆根的 ParentId 保持原值，是否换父由调用方决定
    public static TreeNodeBase DeepClone(TreeNodeBase node)
    {
        return CloneCore(node, node.ParentId, DateTime.UtcNow);
    }

    // 递归克隆核心：parentId 为克隆节点的父 Id（根保持原值，子节点指向各自新父）
    private static TreeNodeBase CloneCore(TreeNodeBase source, Guid? parentId, DateTime now)
    {
        switch (source)
        {
            case FolderNode folder:
            {
                var clone = new FolderNode
                {
                    Id = Guid.NewGuid(),
                    ParentId = parentId,
                    Name = folder.Name,
                    Description = folder.Description,
                    SortOrder = folder.SortOrder,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsExpanded = false
                };
                foreach (var child in folder.Children)
                {
                    clone.Children.Add(CloneCore(child, clone.Id, now));
                }

                return clone;
            }
            case SessionNode session:
            {
                return new SessionNode
                {
                    Id = Guid.NewGuid(),
                    ParentId = parentId,
                    Name = session.Name,
                    Description = session.Description,
                    SortOrder = session.SortOrder,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Host = session.Host,
                    Port = session.Port,
                    Username = session.Username,
                    // 身份绑定与协议为跨节点/会话属性，原样保留（禁止重新生成）
                    IdentityId = session.IdentityId,
                    Protocol = session.Protocol,
                    TerminalType = session.TerminalType,
                    StartupScript = session.StartupScript,
                    JumpHostSessionId = session.JumpHostSessionId,
                    EnvironmentVariables = new Dictionary<string, string>(
                        session.EnvironmentVariables,
                        session.EnvironmentVariables.Comparer)
                };
            }
            default:
                throw new NotSupportedException($"未知的节点类型: {source.GetType().Name}");
        }
    }
}
