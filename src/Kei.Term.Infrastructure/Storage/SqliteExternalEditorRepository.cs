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
            SelectEditorsSql + " ORDER BY is_default DESC, name ASC;",
            cancellationToken: ct));
        List<ExternalEditor> editors = editorRows.Select(ToEditor).ToList();
        if (editors.Count == 0)
        {
            return editors;
        }

        IEnumerable<PathRow> pathRows = await conn.QueryAsync<PathRow>(new CommandDefinition(SelectPathsSql + ";", cancellationToken: ct));
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
            SelectEditorsSql + " WHERE id = @id;",
            new { id = id.ToString() },
            cancellationToken: ct));
        if (row == null)
        {
            return null;
        }

        ExternalEditor editor = ToEditor(row);
        IEnumerable<PathRow> pathRows = await conn.QueryAsync<PathRow>(new CommandDefinition(
            SelectPathsSql + " WHERE editor_id = @id;",
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
                "UPDATE external_editors SET is_default = 0;",
                transaction: tx,
                cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO external_editors (id, name, arguments_template, is_default, created_at, updated_at)
            VALUES (@Id, @Name, @ArgumentsTemplate, @IsDefault, @CreatedAt, @UpdatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                arguments_template = excluded.arguments_template,
                is_default = excluded.is_default,
                updated_at = excluded.updated_at;
        ", new
        {
            Id = editor.Id.ToString(),
            editor.Name,
            editor.ArgumentsTemplate,
            IsDefault = editor.IsDefault ? 1 : 0,
            CreatedAt = SqliteValue.FormatUtc(editor.CreatedAt),
            UpdatedAt = SqliteValue.FormatUtc(DateTime.UtcNow)
        }, transaction: tx, cancellationToken: ct));

        // 路径集合整体替换
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM external_editor_paths WHERE editor_id = @id;",
            new { id = editor.Id.ToString() },
            transaction: tx,
            cancellationToken: ct));

        // Dapper 对参数序列逐条执行同一语句
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO external_editor_paths (id, editor_id, os, path)
            VALUES (@Id, @EditorId, @Os, @Path);
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
        // 路径与关联规则经 ON DELETE CASCADE 一并清除
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM external_editors WHERE id = @id;",
            new { id = id.ToString() },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FileAssociationRule>> GetAllAssociationsAsync(CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        IEnumerable<AssociationRow> rows = await conn.QueryAsync<AssociationRow>(new CommandDefinition(@"
            SELECT id, pattern, editor_id, priority, created_at, updated_at
            FROM file_associations
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
            INSERT INTO file_associations (id, pattern, editor_id, priority, created_at, updated_at)
            VALUES (@Id, @Pattern, @EditorId, @Priority, @CreatedAt, @UpdatedAt)
            ON CONFLICT(id) DO UPDATE SET
                pattern = excluded.pattern,
                editor_id = excluded.editor_id,
                priority = excluded.priority,
                updated_at = excluded.updated_at;
        ", new
        {
            Id = association.Id.ToString(),
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
            "DELETE FROM file_associations WHERE id = @id;",
            new { id = id.ToString() },
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
