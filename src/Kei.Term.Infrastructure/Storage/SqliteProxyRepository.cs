namespace Kei.Term.Infrastructure.Storage;

using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;
using Kei.Term.Infrastructure.Storage.Schema;

public class SqliteProxyRepository : IProxyRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger _logger;

    public SqliteProxyRepository(string connectionString, ILogger? logger = null)
        : this(new SqliteConnectionFactory(connectionString), logger)
    {
    }

    public SqliteProxyRepository(SqliteConnectionFactory factory, ILogger? logger = null)
    {
        _factory = factory;
        _logger = logger ?? NullLogger.Instance;
    }

    public Task InitializeAsync(CancellationToken ct = default) => SchemaMigrator.MigrateAsync(_factory, ct);

    public async Task<IReadOnlyList<ProxyProfile>> GetAllAsync(CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        IEnumerable<ProxyRow> rows = await conn.QueryAsync<ProxyRow>(new CommandDefinition(@"
            SELECT id, name, sort_order, config_json, created_at, updated_at
            FROM proxies
            ORDER BY sort_order ASC, name COLLATE NOCASE ASC;
        ", cancellationToken: ct));

        var list = new List<ProxyProfile>();
        foreach (ProxyRow row in rows)
        {
            ProxyConfig? config = ProxyJsonCodec.ReadConfig(row.ConfigJson, _logger, row.Id);
            if (config == null)
            {
                continue;
            }

            list.Add(ToProfile(row, config));
        }

        return list;
    }

    public async Task<ProxyProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        ProxyRow? row = await conn.QuerySingleOrDefaultAsync<ProxyRow>(new CommandDefinition(@"
            SELECT id, name, sort_order, config_json, created_at, updated_at
            FROM proxies
            WHERE id = @id;
        ", new { id = id.ToString() }, cancellationToken: ct));
        if (row == null)
        {
            return null;
        }

        ProxyConfig? config = ProxyJsonCodec.ReadConfig(row.ConfigJson, _logger, row.Id);
        return config == null ? null : ToProfile(row, config);
    }

    public async Task SaveAsync(ProxyProfile proxy, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        DateTime now = DateTime.UtcNow;
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO proxies (id, name, sort_order, config_json, created_at, updated_at)
            VALUES (@Id, @Name, @SortOrder, @ConfigJson, @CreatedAt, @UpdatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                sort_order = excluded.sort_order,
                config_json = excluded.config_json,
                updated_at = excluded.updated_at;
        ", new
        {
            Id = proxy.Id.ToString(),
            proxy.Name,
            proxy.SortOrder,
            ConfigJson = ProxyJsonCodec.WriteConfig(proxy.Config),
            CreatedAt = SqliteValue.FormatUtc(proxy.CreatedAt == default ? now : proxy.CreatedAt),
            UpdatedAt = SqliteValue.FormatUtc(now)
        }, cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM proxies WHERE id = @id;",
            new { id = id.ToString() },
            cancellationToken: ct));
    }

    private static ProxyProfile ToProfile(ProxyRow row, ProxyConfig config) => new()
    {
        Id = Guid.Parse(row.Id),
        Name = row.Name,
        SortOrder = (int)row.SortOrder,
        Config = config,
        CreatedAt = SqliteValue.ParseUtc(row.CreatedAt),
        UpdatedAt = SqliteValue.ParseUtc(row.UpdatedAt)
    };

    private sealed class ProxyRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long SortOrder { get; set; }
        public string? ConfigJson { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }
}
