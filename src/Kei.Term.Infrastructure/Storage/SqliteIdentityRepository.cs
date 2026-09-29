namespace Kei.Term.Infrastructure.Storage;

using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage.Schema;

public class SqliteIdentityRepository : IIdentityRepository
{
    private const string SelectSql = @"
        SELECT id, name, description, username, methods_json, created_at, updated_at
        FROM identities";

    private readonly SqliteConnectionFactory _factory;

    public SqliteIdentityRepository(string connectionString)
        : this(new SqliteConnectionFactory(connectionString))
    {
    }

    public SqliteIdentityRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync(CancellationToken ct = default) => SchemaMigrator.MigrateAsync(_factory, ct);

    public async Task<IReadOnlyList<Identity>> GetAllAsync(CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        IEnumerable<IdentityRow> rows = await conn.QueryAsync<IdentityRow>(new CommandDefinition(
            SelectSql + " ORDER BY name ASC;",
            cancellationToken: ct));
        return rows.Select(ToIdentity).ToList();
    }

    public async Task<Identity?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        IdentityRow? row = await conn.QuerySingleOrDefaultAsync<IdentityRow>(new CommandDefinition(
            SelectSql + " WHERE id = @id;",
            new { id = id.ToString() },
            cancellationToken: ct));
        return row == null ? null : ToIdentity(row);
    }

    public async Task SaveAsync(Identity identity, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        DateTime createdAt = identity.CreatedAt == default ? DateTime.UtcNow : identity.CreatedAt;
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO identities (id, name, description, username, methods_json, created_at, updated_at)
            VALUES (@Id, @Name, @Description, @Username, @Methods, @CreatedAt, @UpdatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                description = excluded.description,
                username = excluded.username,
                methods_json = excluded.methods_json,
                updated_at = excluded.updated_at;
        ", new
        {
            Id = identity.Id.ToString(),
            identity.Name,
            identity.Description,
            identity.Username,
            // 整个方法列表以多态 JSON（$kind 判别、数组序 = 尝试序）整体序列化
            Methods = JsonSerializer.Serialize(identity.Methods),
            CreatedAt = SqliteValue.FormatUtc(createdAt),
            UpdatedAt = SqliteValue.FormatUtc(DateTime.UtcNow)
        }, cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        // identity_secrets 经 ON DELETE CASCADE 随身份一并清除；session_details.identity_id 置空
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM identities WHERE id = @id;",
            new { id = id.ToString() },
            cancellationToken: ct));
    }

    private static Identity ToIdentity(IdentityRow row) => new()
    {
        Id = Guid.Parse(row.Id),
        Name = row.Name,
        Description = row.Description,
        Username = row.Username,
        Methods = JsonSerializer.Deserialize<List<AuthMethodEntry>>(row.MethodsJson ?? "[]") ?? [],
        CreatedAt = SqliteValue.ParseUtc(row.CreatedAt),
        UpdatedAt = SqliteValue.ParseUtc(row.UpdatedAt)
    };

    private sealed class IdentityRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Username { get; set; }
        public string? MethodsJson { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }
}
