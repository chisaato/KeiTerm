namespace Kei.Term.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;

public class SqliteExternalEditorRepository : IExternalEditorRepository
{
    private readonly string _connectionString;

    public SqliteExternalEditorRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    private async Task<SqliteConnection> CreateConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(ct);

        return conn;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS external_editors (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                arguments_template TEXT NOT NULL DEFAULT '""{path}""',
                is_default INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS external_editor_paths (
                id TEXT PRIMARY KEY NOT NULL,
                editor_id TEXT NOT NULL REFERENCES external_editors(id) ON DELETE CASCADE,
                os TEXT NOT NULL,
                path TEXT NOT NULL,
                UNIQUE(editor_id, os)
            );

            CREATE TABLE IF NOT EXISTS file_associations (
                id TEXT PRIMARY KEY NOT NULL,
                pattern TEXT NOT NULL,
                editor_id TEXT NOT NULL REFERENCES external_editors(id) ON DELETE CASCADE,
                priority INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_file_associations_priority ON file_associations(priority DESC);
        ";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ExternalEditor>> GetAllEditorsAsync(CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);
        var editors = new Dictionary<Guid, ExternalEditor>();

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, arguments_template, is_default, created_at, updated_at
            FROM external_editors
            ORDER BY is_default DESC, name ASC;
        ";

        using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = Guid.Parse(reader.GetString(0));
                editors[id] = new ExternalEditor
                {
                    Id = id,
                    Name = reader.GetString(1),
                    ArgumentsTemplate = reader.GetString(2),
                    IsDefault = reader.GetInt32(3) == 1,
                    CreatedAt = DateTime.Parse(reader.GetString(4)),
                    UpdatedAt = DateTime.Parse(reader.GetString(5)),
                    Paths = []
                };
            }
        }

        if (editors.Count > 0)
        {
            var pathCmd = conn.CreateCommand();
            pathCmd.CommandText = @"
                SELECT id, editor_id, os, path
                FROM external_editor_paths;
            ";

            using var pathReader = await pathCmd.ExecuteReaderAsync(ct);
            while (await pathReader.ReadAsync(ct))
            {
                var pathId = Guid.Parse(pathReader.GetString(0));
                var editorId = Guid.Parse(pathReader.GetString(1));
                var os = pathReader.GetString(2);
                var path = pathReader.GetString(3);

                if (editors.TryGetValue(editorId, out var editor))
                {
                    editor.Paths.Add(new ExternalEditorPath(pathId, editorId, os, path));
                }
            }
        }

        return editors.Values.ToList();
    }

    public async Task<ExternalEditor?> GetEditorByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);

        ExternalEditor? editor = null;
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, arguments_template, is_default, created_at, updated_at
            FROM external_editors
            WHERE id = $id;
        ";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                editor = new ExternalEditor
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Name = reader.GetString(1),
                    ArgumentsTemplate = reader.GetString(2),
                    IsDefault = reader.GetInt32(3) == 1,
                    CreatedAt = DateTime.Parse(reader.GetString(4)),
                    UpdatedAt = DateTime.Parse(reader.GetString(5)),
                    Paths = []
                };
            }
        }

        if (editor == null) return null;

        var pathCmd = conn.CreateCommand();
        pathCmd.CommandText = @"
            SELECT id, editor_id, os, path
            FROM external_editor_paths
            WHERE editor_id = $editor_id;
        ";
        pathCmd.Parameters.AddWithValue("$editor_id", id.ToString());

        using var pathReader = await pathCmd.ExecuteReaderAsync(ct);
        while (await pathReader.ReadAsync(ct))
        {
            editor.Paths.Add(new ExternalEditorPath(
                Guid.Parse(pathReader.GetString(0)),
                id,
                pathReader.GetString(2),
                pathReader.GetString(3)
            ));
        }

        return editor;
    }

    public async Task SaveEditorAsync(ExternalEditor editor, CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);
        using var transaction = conn.BeginTransaction();

        // 如果设为默认编辑器，先将其他所有编辑器的 is_default 置为 0
        if (editor.IsDefault)
        {
            var resetCmd = conn.CreateCommand();
            resetCmd.Transaction = transaction;
            resetCmd.CommandText = "UPDATE external_editors SET is_default = 0;";
            await resetCmd.ExecuteNonQueryAsync(ct);
        }

        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO external_editors (id, name, arguments_template, is_default, created_at, updated_at)
            VALUES ($id, $name, $arguments_template, $is_default, $created_at, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                arguments_template = excluded.arguments_template,
                is_default = excluded.is_default,
                updated_at = excluded.updated_at;
        ";
        cmd.Parameters.AddWithValue("$id", editor.Id.ToString());
        cmd.Parameters.AddWithValue("$name", editor.Name);
        cmd.Parameters.AddWithValue("$arguments_template", editor.ArgumentsTemplate);
        cmd.Parameters.AddWithValue("$is_default", editor.IsDefault ? 1 : 0);
        cmd.Parameters.AddWithValue("$created_at", editor.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$updated_at", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);

        // 更新路径：先清理已移除的或全量重插
        var deletePathsCmd = conn.CreateCommand();
        deletePathsCmd.Transaction = transaction;
        deletePathsCmd.CommandText = "DELETE FROM external_editor_paths WHERE editor_id = $editor_id;";
        deletePathsCmd.Parameters.AddWithValue("$editor_id", editor.Id.ToString());
        await deletePathsCmd.ExecuteNonQueryAsync(ct);

        foreach (var p in editor.Paths)
        {
            var insertPathCmd = conn.CreateCommand();
            insertPathCmd.Transaction = transaction;
            insertPathCmd.CommandText = @"
                INSERT INTO external_editor_paths (id, editor_id, os, path)
                VALUES ($id, $editor_id, $os, $path);
            ";
            insertPathCmd.Parameters.AddWithValue("$id", (p.Id == Guid.Empty ? Guid.NewGuid() : p.Id).ToString());
            insertPathCmd.Parameters.AddWithValue("$editor_id", editor.Id.ToString());
            insertPathCmd.Parameters.AddWithValue("$os", p.Os);
            insertPathCmd.Parameters.AddWithValue("$path", p.Path);
            await insertPathCmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task DeleteEditorAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM external_editors WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<FileAssociationRule>> GetAllAssociationsAsync(CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);
        var list = new List<FileAssociationRule>();

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, pattern, editor_id, priority, created_at, updated_at
            FROM file_associations
            ORDER BY priority DESC, created_at ASC;
        ";

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new FileAssociationRule
            {
                Id = Guid.Parse(reader.GetString(0)),
                Pattern = reader.GetString(1),
                EditorId = Guid.Parse(reader.GetString(2)),
                Priority = reader.GetInt32(3),
                CreatedAt = DateTime.Parse(reader.GetString(4)),
                UpdatedAt = DateTime.Parse(reader.GetString(5))
            });
        }

        return list;
    }

    public async Task SaveAssociationAsync(FileAssociationRule association, CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO file_associations (id, pattern, editor_id, priority, created_at, updated_at)
            VALUES ($id, $pattern, $editor_id, $priority, $created_at, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                pattern = excluded.pattern,
                editor_id = excluded.editor_id,
                priority = excluded.priority,
                updated_at = excluded.updated_at;
        ";
        cmd.Parameters.AddWithValue("$id", association.Id.ToString());
        cmd.Parameters.AddWithValue("$pattern", association.Pattern);
        cmd.Parameters.AddWithValue("$editor_id", association.EditorId.ToString());
        cmd.Parameters.AddWithValue("$priority", association.Priority);
        cmd.Parameters.AddWithValue("$created_at", association.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$updated_at", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAssociationAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM file_associations WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
