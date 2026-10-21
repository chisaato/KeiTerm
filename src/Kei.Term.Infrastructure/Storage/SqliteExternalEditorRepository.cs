namespace Kei.Term.Infrastructure.Storage;

using Dapper;
using Microsoft.Data.Sqlite;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;
using Kei.Term.Infrastructure.Storage.Schema;

public class SqliteExternalEditorRepository : IExternalEditorRepository
{
    private const string SelectEditorsSql = @"
        SELECT id, name, arguments_template, is_default, created_at, updated_at
        FROM external_editors";

    private const string SelectPathsSql = @"
        SELECT id, editor_id, os, path
        FROM external_editor_paths";

    private readonly SqliteConnectionFactory _factory;

    public SqliteExternalEditorRepository(string connectionString)
        : this(new SqliteConnectionFactory(connectionString))
    {
    }

    public SqliteExternalEditorRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync(CancellationToken ct = default) => SchemaMigrator.MigrateAsync(_factory, ct);

    public async Task<IReadOnlyList<ExternalEditor>> GetAllEditorsAsync(CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        IEnumerable<EditorRow> editorRows = await conn.QueryAsync<EditorRow>(new CommandDefinition(
            SelectEditorsSql + " WHERE deleted_at IS NULL ORDER BY is_default DESC, name ASC;",
            cancellationToken: ct));
        List<ExternalEditor> editors = editorRows.Select(ToEditor).ToList();
        if (editors.Count == 0)
        {
            return editors;
        }

        IEnumerable<PathRow> pathRows = await conn.QueryAsync<PathRow>(new CommandDefinition(SelectPathsSql + " WHERE deleted_at IS NULL;", cancellationToken: ct));
        Dictionary<Guid, ExternalEditor> byId = editors.ToDictionary(e => e.Id);
        foreach (PathRow path in pathRows)
        {
            if (byId.TryGetValue(Guid.Parse(path.EditorId), out ExternalEditor? editor))
            {
                editor.Paths.Add(ToPath(path));
            }
        }

        return editors;
    }

    public async Task<ExternalEditor?> GetEditorByIdAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        EditorRow? row = await conn.QuerySingleOrDefaultAsync<EditorRow>(new CommandDefinition(
            SelectEditorsSql + " WHERE id = @id AND deleted_at IS NULL;",
            new { id = id.ToString() },
            cancellationToken: ct));
        if (row == null)
        {
            return null;
        }

        ExternalEditor editor = ToEditor(row);
        IEnumerable<PathRow> pathRows = await conn.QueryAsync<PathRow>(new CommandDefinition(
            SelectPathsSql + " WHERE editor_id = @id AND deleted_at IS NULL;",
            new { id = id.ToString() },
            cancellationToken: ct));
        editor.Paths.AddRange(pathRows.Select(ToPath));
        return editor;
    }

    public async Task SaveEditorAsync(ExternalEditor editor, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        using SqliteTransaction tx = conn.BeginTransaction();

        // 默认编辑器全局唯一：先清掉其他编辑器的默认标记
        if (editor.IsDefault)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE external_editors SET is_default = 0, updated_at = @now, device_id = @deviceId, revision = revision + 1 WHERE is_default = 1 AND id != @id AND deleted_at IS NULL;",
                new { id = editor.Id.ToString(), now = SqliteValue.FormatUtc(DateTime.UtcNow), deviceId = _factory.DeviceId },
                transaction: tx,
                cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO external_editors (id, name, arguments_template, is_default, created_at, updated_at, device_id, revision)
            VALUES (@Id, @Name, @ArgumentsTemplate, @IsDefault, @CreatedAt, @UpdatedAt, @DeviceId, 1)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                arguments_template = excluded.arguments_template,
                is_default = excluded.is_default,
                updated_at = excluded.updated_at, device_id = excluded.device_id,
                revision = external_editors.revision + 1, deleted_at = NULL;
        ", new
        {
            Id = editor.Id.ToString(),
            DeviceId = _factory.DeviceId,
            editor.Name,
            editor.ArgumentsTemplate,
            IsDefault = editor.IsDefault ? 1 : 0,
            CreatedAt = SqliteValue.FormatUtc(editor.CreatedAt),
            UpdatedAt = SqliteValue.FormatUtc(DateTime.UtcNow)
        }, transaction: tx, cancellationToken: ct));

        // 路径集合整体替换
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE external_editor_paths SET deleted_at = @now WHERE editor_id = @id AND deleted_at IS NULL;",
            new { id = editor.Id.ToString(), now = SqliteValue.FormatUtc(DateTime.UtcNow) },
            transaction: tx,
            cancellationToken: ct));

        // Dapper 对参数序列逐条执行同一语句
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO external_editor_paths (id, editor_id, os, path)
            VALUES (@Id, @EditorId, @Os, @Path)
            ON CONFLICT(editor_id, os) DO UPDATE SET path = excluded.path, deleted_at = NULL
            ON CONFLICT(id) DO UPDATE SET os = excluded.os, path = excluded.path, deleted_at = NULL;
        ", editor.Paths.Select(p => new
        {
            Id = (p.Id == Guid.Empty ? Guid.NewGuid() : p.Id).ToString(),
            EditorId = editor.Id.ToString(),
            p.Os,
            p.Path
        }).ToList(), transaction: tx, cancellationToken: ct));

        tx.Commit();
    }

    public async Task DeleteEditorAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE external_editor_paths SET deleted_at = @now WHERE editor_id = @id AND deleted_at IS NULL;
              UPDATE file_associations SET deleted_at = @now, updated_at = @now, device_id = @deviceId, revision = revision + 1 WHERE editor_id = @id AND deleted_at IS NULL;
              UPDATE external_editors SET deleted_at = @now, updated_at = @now, device_id = @deviceId, revision = revision + 1 WHERE id = @id AND deleted_at IS NULL;",
            new { id = id.ToString(), now = SqliteValue.FormatUtc(DateTime.UtcNow), deviceId = _factory.DeviceId },
            transaction: tx, cancellationToken: ct));
        tx.Commit();
    }

    public async Task<IReadOnlyList<FileAssociationRule>> GetAllAssociationsAsync(CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        IEnumerable<AssociationRow> rows = await conn.QueryAsync<AssociationRow>(new CommandDefinition(@"
            SELECT id, pattern, editor_id, priority, created_at, updated_at
            FROM file_associations
            WHERE deleted_at IS NULL
            ORDER BY priority DESC, created_at ASC;
        ", cancellationToken: ct));

        return rows.Select(r => new FileAssociationRule
        {
            Id = Guid.Parse(r.Id),
            Pattern = r.Pattern,
            EditorId = Guid.Parse(r.EditorId),
            Priority = (int)r.Priority,
            CreatedAt = SqliteValue.ParseUtc(r.CreatedAt),
            UpdatedAt = SqliteValue.ParseUtc(r.UpdatedAt)
        }).ToList();
    }

    public async Task SaveAssociationAsync(FileAssociationRule association, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO file_associations (id, pattern, editor_id, priority, created_at, updated_at, device_id, revision)
            VALUES (@Id, @Pattern, @EditorId, @Priority, @CreatedAt, @UpdatedAt, @DeviceId, 1)
            ON CONFLICT(id) DO UPDATE SET
                pattern = excluded.pattern,
                editor_id = excluded.editor_id,
                priority = excluded.priority,
                updated_at = excluded.updated_at, device_id = excluded.device_id,
                revision = file_associations.revision + 1, deleted_at = NULL;
        ", new
        {
            Id = association.Id.ToString(),
            DeviceId = _factory.DeviceId,
            association.Pattern,
            EditorId = association.EditorId.ToString(),
            association.Priority,
            CreatedAt = SqliteValue.FormatUtc(association.CreatedAt),
            UpdatedAt = SqliteValue.FormatUtc(DateTime.UtcNow)
        }, cancellationToken: ct));
    }

    public async Task DeleteAssociationAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE file_associations SET deleted_at = @now, updated_at = @now, device_id = @deviceId, revision = revision + 1 WHERE id = @id AND deleted_at IS NULL;",
            new { id = id.ToString(), now = SqliteValue.FormatUtc(DateTime.UtcNow), deviceId = _factory.DeviceId },
            cancellationToken: ct));
    }

    private static ExternalEditor ToEditor(EditorRow row) => new()
    {
        Id = Guid.Parse(row.Id),
        Name = row.Name,
        ArgumentsTemplate = row.ArgumentsTemplate,
        IsDefault = row.IsDefault == 1,
        CreatedAt = SqliteValue.ParseUtc(row.CreatedAt),
        UpdatedAt = SqliteValue.ParseUtc(row.UpdatedAt),
        Paths = []
    };

    private static ExternalEditorPath ToPath(PathRow row)
        => new(Guid.Parse(row.Id), Guid.Parse(row.EditorId), row.Os, row.Path);

    private sealed class EditorRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ArgumentsTemplate { get; set; } = string.Empty;
        public long IsDefault { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }

    private sealed class PathRow
    {
        public string Id { get; set; } = string.Empty;
        public string EditorId { get; set; } = string.Empty;
        public string Os { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    private sealed class AssociationRow
    {
        public string Id { get; set; } = string.Empty;
        public string Pattern { get; set; } = string.Empty;
        public string EditorId { get; set; } = string.Empty;
        public long Priority { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }
}
