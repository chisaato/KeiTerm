namespace Kei.Term.Infrastructure.Storage;

using Kei.Term.Core.Abstractions;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;
using Kei.Term.Infrastructure.Storage.Schema;

public class SqliteTreeRepository : ITreeRepository
{
    // 目录在前（node_type 0）、会话在后；同类按 sort_order、name 排序
    private const string SelectNodesSql = @"
        SELECT t.id, t.parent_id, t.node_type, t.name, t.description, t.sort_order, t.created_at, t.updated_at,
               t.protocol, t.is_expanded,
               s.host, s.port, s.username, s.identity_id, s.terminal_type, s.startup_script, s.jump_host_id, s.env_vars_json,
               s.terminal_profile_id, s.file_transfer_protocol, s.sftp_mode
        FROM tree_nodes t
        LEFT JOIN session_details s ON t.id = s.node_id";

    private readonly SqliteConnectionFactory _factory;

    public SqliteTreeRepository(string connectionString)
        : this(new SqliteConnectionFactory(connectionString))
    {
    }

    public SqliteTreeRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    // Schema 统一由迁移器维护；保留该入口兼容既有调用方
    public Task InitializeAsync(CancellationToken ct = default) => SchemaMigrator.MigrateAsync(_factory, ct);

    public async Task<IReadOnlyList<TreeNodeBase>> GetAllNodesAsync(CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        IEnumerable<NodeRow> rows = await conn.QueryAsync<NodeRow>(new CommandDefinition(
            SelectNodesSql + " ORDER BY t.node_type ASC, t.sort_order ASC, t.name COLLATE NOCASE ASC;",
            cancellationToken: ct));
        return rows.Select(ToNode).ToList();
    }

    public async Task<TreeNodeBase?> GetNodeByIdAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        NodeRow? row = await conn.QuerySingleOrDefaultAsync<NodeRow>(new CommandDefinition(
            SelectNodesSql + " WHERE t.id = @id;",
            new { id = id.ToString() },
            cancellationToken: ct));
        return row == null ? null : ToNode(row);
    }

    public async Task SaveNodeAsync(TreeNodeBase node, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        using SqliteTransaction tx = conn.BeginTransaction();

        string protocol = node is SessionNode p && !string.IsNullOrWhiteSpace(p.Protocol) ? p.Protocol : SessionProtocols.Ssh;
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO tree_nodes (id, parent_id, node_type, name, description, sort_order, protocol, is_expanded, created_at, updated_at)
            VALUES (@Id, @ParentId, @NodeType, @Name, @Description, @SortOrder, @Protocol, @IsExpanded, @CreatedAt, @UpdatedAt)
            ON CONFLICT(id) DO UPDATE SET
                parent_id = excluded.parent_id,
                name = excluded.name,
                description = excluded.description,
                sort_order = excluded.sort_order,
                protocol = excluded.protocol,
                is_expanded = excluded.is_expanded,
                updated_at = excluded.updated_at;
        ", new
        {
            Id = node.Id.ToString(),
            ParentId = node.ParentId?.ToString(),
            NodeType = (int)node.NodeType,
            node.Name,
            node.Description,
            node.SortOrder,
            Protocol = protocol,
            IsExpanded = node is FolderNode f && f.IsExpanded ? 1 : 0,
            CreatedAt = SqliteValue.FormatUtc(node.CreatedAt),
            UpdatedAt = SqliteValue.FormatUtc(DateTime.UtcNow)
        }, transaction: tx, cancellationToken: ct));

        if (node is SessionNode session)
        {
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO session_details (node_id, host, port, username, identity_id, terminal_type, startup_script, jump_host_id,
                                             env_vars_json, terminal_profile_id, file_transfer_protocol, sftp_mode)
                VALUES (@NodeId, @Host, @Port, @Username, @IdentityId, @TerminalType, @StartupScript, @JumpHostId,
                        @EnvJson, @TerminalProfileId, @FileTransferProtocol, @SftpMode)
                ON CONFLICT(node_id) DO UPDATE SET
                    host = excluded.host,
                    port = excluded.port,
                    username = excluded.username,
                    identity_id = excluded.identity_id,
                    terminal_type = excluded.terminal_type,
                    startup_script = excluded.startup_script,
                    jump_host_id = excluded.jump_host_id,
                    env_vars_json = excluded.env_vars_json,
                    terminal_profile_id = excluded.terminal_profile_id,
                    file_transfer_protocol = excluded.file_transfer_protocol,
                    sftp_mode = excluded.sftp_mode;
            ", new
            {
                NodeId = session.Id.ToString(),
                session.Host,
                session.Port,
                session.Username,
                IdentityId = session.IdentityId?.ToString(),
                session.TerminalType,
                session.StartupScript,
                JumpHostId = session.JumpHostSessionId?.ToString(),
                EnvJson = JsonSerializer.Serialize(session.EnvironmentVariables),
                session.TerminalProfileId,
                FileTransferProtocol = (int)session.FileTransferProtocol,
                SftpMode = (int)session.SftpMode
            }, transaction: tx, cancellationToken: ct));
        }

        tx.Commit();
    }

    public async Task DeleteNodeAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        // 子树与 session_details 经 ON DELETE CASCADE 一并清除
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tree_nodes WHERE id = @id;",
            new { id = id.ToString() },
            cancellationToken: ct));
    }

    public async Task MoveNodeAsync(Guid nodeId, Guid? newParentId, int sortOrder, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE tree_nodes
            SET parent_id = @parentId, sort_order = @sortOrder, updated_at = @updatedAt
            WHERE id = @id;
        ", new
        {
            id = nodeId.ToString(),
            parentId = newParentId?.ToString(),
            sortOrder,
            updatedAt = SqliteValue.FormatUtc(DateTime.UtcNow)
        }, cancellationToken: ct));
    }

    public async Task UpdateFolderExpandedAsync(Guid folderId, bool isExpanded, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        // 展开/折叠属于视图状态，不刷新 updated_at（避免未来同步把 UI 状态当成内容变更）
        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE tree_nodes
            SET is_expanded = @isExpanded
            WHERE id = @id AND node_type = @nodeType;
        ", new
        {
            id = folderId.ToString(),
            isExpanded = isExpanded ? 1 : 0,
            nodeType = (int)NodeType.Folder
        }, cancellationToken: ct));
    }

    private static TreeNodeBase ToNode(NodeRow row)
    {
        Guid id = Guid.Parse(row.Id);
        Guid? parentId = SqliteValue.ParseGuid(row.ParentId);
        DateTime createdAt = SqliteValue.ParseUtc(row.CreatedAt);
        DateTime updatedAt = SqliteValue.ParseUtc(row.UpdatedAt);

        if ((NodeType)row.NodeType == NodeType.Folder)
        {
            // 文件夹为纯分类容器，仅加载基础列与展开状态
            return new FolderNode
            {
                Id = id,
                ParentId = parentId,
                Name = row.Name,
                Description = row.Description,
                SortOrder = (int)row.SortOrder,
                IsExpanded = row.IsExpanded != 0,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt
            };
        }

        Dictionary<string, string> envVars = string.IsNullOrEmpty(row.EnvVarsJson)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(row.EnvVarsJson) ?? new(StringComparer.Ordinal);

        return new SessionNode
        {
            Id = id,
            ParentId = parentId,
            Name = row.Name,
            Description = row.Description,
            SortOrder = (int)row.SortOrder,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Host = row.Host ?? string.Empty,
            Port = row.Port is { } port ? (int)port : null,
            Username = row.Username,
            IdentityId = SqliteValue.ParseGuid(row.IdentityId),
            TerminalType = row.TerminalType ?? "xterm-256color",
            StartupScript = row.StartupScript,
            JumpHostSessionId = SqliteValue.ParseGuid(row.JumpHostId),
            Protocol = row.Protocol ?? SessionProtocols.Ssh,
            EnvironmentVariables = envVars,
            TerminalProfileId = row.TerminalProfileId,
            FileTransferProtocol = (FileTransferProtocol)(row.FileTransferProtocol ?? 0),
            SftpMode = (SftpChannelMode)(row.SftpMode ?? 0)
        };
    }

    // 行对象与表列一一对应（snake_case 自动映射）；SQLite INTEGER 统一按 long 读取
    private sealed class NodeRow
    {
        public string Id { get; set; } = string.Empty;
        public string? ParentId { get; set; }
        public long NodeType { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public long SortOrder { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
        public string? Protocol { get; set; }
        public long IsExpanded { get; set; }
        public string? Host { get; set; }
        public long? Port { get; set; }
        public string? Username { get; set; }
        public string? IdentityId { get; set; }
        public string? TerminalType { get; set; }
        public string? StartupScript { get; set; }
        public string? JumpHostId { get; set; }
        public string? EnvVarsJson { get; set; }
        public string? TerminalProfileId { get; set; }
        public long? FileTransferProtocol { get; set; }
        public long? SftpMode { get; set; }
    }
}
