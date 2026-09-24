namespace Kei.Term.Infrastructure.Storage;

using Microsoft.Data.Sqlite;
using System.Text.Json;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;

public class SqliteIdentityRepository : IIdentityRepository
{
    private readonly string _connectionString;

    public SqliteIdentityRepository(string connectionString)
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

    // 独立可用：确保身份表存在（与 SqliteTreeRepository 的建表幂等共存）
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS identities (
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                description TEXT,
                username TEXT,
                methods_json TEXT NOT NULL DEFAULT '[]',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
        ";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Identity>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);

        var list = new List<Identity>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, description, username, methods_json, created_at, updated_at
            FROM identities
            ORDER BY name ASC;
        ";

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadIdentity(reader));
        }

        return list;
    }

    public async Task<Identity?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, description, username, methods_json, created_at, updated_at
            FROM identities
            WHERE id = $id;
        ";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadIdentity(reader);
        }

        return null;
    }

    public async Task SaveAsync(Identity identity, CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO identities (id, name, description, username, methods_json, created_at, updated_at)
            VALUES ($id, $name, $description, $username, $methods, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = $name,
                description = $description,
                username = $username,
                methods_json = $methods,
                updated_at = $updatedAt;
        ";
        cmd.Parameters.AddWithValue("$id", identity.Id.ToString());
        cmd.Parameters.AddWithValue("$name", identity.Name);
        cmd.Parameters.AddWithValue("$description", (object?)identity.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$username", (object?)identity.Username ?? DBNull.Value);
        // 整个方法列表以多态 JSON（$kind 判别、数组序 = 尝试序）整体序列化
        cmd.Parameters.AddWithValue("$methods", JsonSerializer.Serialize(identity.Methods));
        cmd.Parameters.AddWithValue("$createdAt", identity.CreatedAt == default ? DateTime.UtcNow.ToString("O") : identity.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await CreateConnectionAsync(ct);

        // identity_secrets 通过 ON DELETE CASCADE 随身份一并清除（依赖 foreign_keys = ON）
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM identities WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Identity ReadIdentity(SqliteDataReader reader)
    {
        var methodsJson = reader.IsDBNull(4) ? "[]" : reader.GetString(4);
        return new Identity
        {
            Id = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1),
            Description = reader.IsDBNull(2) ? null : reader.GetString(2),
            Username = reader.IsDBNull(3) ? null : reader.GetString(3),
            Methods = JsonSerializer.Deserialize<List<AuthMethodEntry>>(methodsJson) ?? [],
            CreatedAt = DateTime.Parse(reader.GetString(5)),
            UpdatedAt = DateTime.Parse(reader.GetString(6))
        };
    }
}
