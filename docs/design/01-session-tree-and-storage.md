# 01 - 会话目录树与存储设计 (Session Tree & Storage)

## 1. 业务目标
还原传统经典 SSH 终端软件（如 SecureCRT / MobaXterm / Termius）的多级目录树管理体验。
- 支持无限层级文件夹分类（如 `生产环境/Kubernetes集群/Worker-01`）。
- 支持属性“级联继承”（Cascading Inheritance）：子节点若未配置特定属性（如默认端口、编码、代理跳板、凭据），自动继承父文件夹设置。
- 保证快速检索、拖拽移动排序、节点状态记忆（展开/折叠）。

---

## 2. 领域模型结构 (Domain Model)

采用组合模式（Composite Pattern）统一表达节点：

```csharp
namespace Kei.Term.Core.Models;

public abstract class TreeNodeBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // 级联配置项（文件夹和主机节点均可定义，主机优先，若空则追溯祖先）
    public NodeInheritableConfig InheritedConfig { get; set; } = new();
}

public class FolderNode : TreeNodeBase
{
    // 子节点运行时展开缓存（UI 树视图绑定使用）
    public List<TreeNodeBase> Children { get; set; } = new();
    public bool IsExpanded { get; set; }
}

public class SessionNode : TreeNodeBase
{
    public string Host { get; set; } = string.Empty;
    public int? Port { get; set; } // 若为 null 则从父文件夹继承或取默认值 22
    public string? Username { get; set; } // 若为 null 则可继承

    // 凭据引用（与具体密钥解耦）
    public Guid? CredentialId { get; set; }
    public CredentialResolutionMode CredentialMode { get; set; } = CredentialResolutionMode.InheritOrReference;

    // 终端与环境定制
    public string TerminalType { get; set; } = "xterm-256color";
    public string? StartupScript { get; set; } // 登录后自动执行命令
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    
    // 代理跳板配置（Proxy Jump / Bastion）
    public Guid? JumpHostSessionId { get; set; }
}

public class NodeInheritableConfig
{
    public int? DefaultPort { get; set; }
    public string? DefaultUsername { get; set; }
    public Guid? DefaultCredentialId { get; set; }
    public string? DefaultEncoding { get; set; }
    public Guid? DefaultJumpHostId { get; set; }
}
```

---

## 3. SQLite 持久化表结构设计

```sql
-- 节点表（树形自关联）
-- 设计考量：
-- 1. 为什么用 parent_id 配合单次全量加载内存组树：
--    对于普通用户和中大型运维场景，主机节点数普遍在 100~5000 之间，深度在 3~6 层左右。
--    SELECT * FROM tree_nodes 一次读取 5000 行在 SQLite 仅需几毫秒（内存开销不到 2MB）。
--    在内存中用 Dictionary<Guid, TreeNode> 递归建树 O(N) 极速完成，完全避免了关系型数据库在多层级联查询时的 Recursive CTE 开销。
-- 2. 数据库纯净不默认加密（不引入 SQLCipher）：
--    - 方便灾备、用户应急找回数据，或者外部脚本直接查看主机目录。
--    - 敏感机密（密码/私钥）单独在 Vault 层加密或送入系统 Keyring，元数据与机密彻底隔离。
CREATE TABLE IF NOT EXISTS tree_nodes (
    id TEXT PRIMARY KEY NOT NULL,
    parent_id TEXT,
    node_type INTEGER NOT NULL, -- 0: Folder, 1: Session
    name TEXT NOT NULL,
    description TEXT,
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY(parent_id) REFERENCES tree_nodes(id) ON DELETE CASCADE
);

-- 会话详情表（当 node_type = 1 时）
CREATE TABLE IF NOT EXISTS session_details (
    node_id TEXT PRIMARY KEY NOT NULL,
    host TEXT NOT NULL,
    port INTEGER,
    username TEXT,
    credential_id TEXT,
    terminal_type TEXT NOT NULL DEFAULT 'xterm-256color',
    jump_host_id TEXT,
    config_payload_json TEXT, -- 保存额外扩展字段（如环境变量、启动脚本等）
    FOREIGN KEY(node_id) REFERENCES tree_nodes(id) ON DELETE CASCADE,
    FOREIGN KEY(credential_id) REFERENCES credentials(id) ON DELETE SET NULL
);

-- 文件夹默认继承配置表（当 node_type = 0 时）
CREATE TABLE IF NOT EXISTS folder_configs (
    folder_id TEXT PRIMARY KEY NOT NULL,
    default_port INTEGER,
    default_username TEXT,
    default_credential_id TEXT,
    default_jump_host_id TEXT,
    FOREIGN KEY(folder_id) REFERENCES tree_nodes(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_tree_nodes_parent ON tree_nodes(parent_id);
```

---

## 4. 级联继承解析引擎 (Cascade Resolver)

为保证调用方无需关注复杂的继承关系，提供统一的服务接口：

```csharp
public interface ISessionConfigResolver
{
    /// <summary>
    /// 给定一个 SessionNode，向上追溯并合并所有父级 FolderNode 的默认配置，输出最终可执行配置
    /// </summary>
    ResolvedSessionConfig Resolve(SessionNode node, IReadOnlyDictionary<Guid, TreeNodeBase> allNodes);
}
```
解析规则：
1. `SessionNode` 上明确配置了有效值（如 `Port = 2222`）-> 直接采用。
2. `SessionNode` 为 null -> 沿 `ParentId` 链向根节点查找，直到发现第一个配置了 `DefaultPort` 的父级。
3. 全链路无配置 -> 采用系统全局缺省值（如 22 端口，当前系统登录用户名）。
