namespace Kei.Term.Infrastructure.Storage;

using Dapper;
using Microsoft.Data.Sqlite;
using Kei.Term.Core.Security;
using Kei.Term.Core.Storage;
using Kei.Term.Infrastructure.Storage.Schema;

public class SqliteKnownHostRepository : IKnownHostRepository
{
    private const string SelectSql = @"
        SELECT id, host, port, key_type, public_key, fingerprint_sha256, status, source, comment, created_at, last_seen_at
        FROM known_hosts";

    private readonly SqliteConnectionFactory _factory;

    public SqliteKnownHostRepository(string connectionString)
        : this(new SqliteConnectionFactory(connectionString))
    {
    }

    public SqliteKnownHostRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync(CancellationToken ct = default) => SchemaMigrator.MigrateAsync(_factory, ct);

    public async Task<IReadOnlyList<KnownHostEntry>> GetAllAsync(CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        IEnumerable<KnownHostRow> rows = await conn.QueryAsync<KnownHostRow>(new CommandDefinition(
            SelectSql + " ORDER BY host, port, key_type;",
            cancellationToken: ct));
        return rows.Select(ToEntry).ToList();
    }

    public async Task<IReadOnlyList<KnownHostEntry>> GetCandidatesAsync(string host, int port, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        IEnumerable<KnownHostRow> rows = await conn.QueryAsync<KnownHostRow>(new CommandDefinition(
            SelectSql + " WHERE (host = @host AND port = @port) OR port = 0;",
            new { host = host.Trim().ToLowerInvariant(), port },
            cancellationToken: ct));
        return rows.Select(ToEntry).ToList();
    }

    public async Task SaveAsync(KnownHostEntry entry, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO known_hosts (id, host, port, key_type, public_key, fingerprint_sha256, status, source, comment, created_at, last_seen_at)
            VALUES (@Id, @Host, @Port, @KeyType, @PublicKey, @Fingerprint, @Status, @Source, @Comment, @CreatedAt, @LastSeenAt)
            ON CONFLICT(host, port, public_key) DO UPDATE SET
                key_type = excluded.key_type,
                fingerprint_sha256 = excluded.fingerprint_sha256,
                status = excluded.status,
                source = excluded.source,
                comment = COALESCE(excluded.comment, known_hosts.comment),
                last_seen_at = COALESCE(excluded.last_seen_at, known_hosts.last_seen_at);
        ", new
        {
            Id = entry.Id.ToString(),
            // 精确条目主机名统一小写；模式条目（哈希值大小写敏感）原样保存
            Host = entry.IsPattern ? entry.Host : entry.Host.Trim().ToLowerInvariant(),
            entry.Port,
            entry.KeyType,
            PublicKey = entry.PublicKeyBase64,
            Fingerprint = entry.FingerprintSha256,
            Status = (int)entry.Status,
            Source = (int)entry.Source,
            entry.Comment,
            CreatedAt = SqliteValue.FormatUtc(entry.CreatedAt),
            LastSeenAt = entry.LastSeenAt is { } seen ? SqliteValue.FormatUtc(seen) : null
        }, cancellationToken: ct));
    }

    public async Task TouchAsync(Guid id, DateTime seenAtUtc, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE known_hosts SET last_seen_at = @seen WHERE id = @id;",
            new { id = id.ToString(), seen = SqliteValue.FormatUtc(seenAtUtc) },
            cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM known_hosts WHERE id = @id;",
            new { id = id.ToString() },
            cancellationToken: ct));
    }

    private static KnownHostEntry ToEntry(KnownHostRow row) => new()
    {
        Id = Guid.Parse(row.Id),
        Host = row.Host,
        Port = (int)row.Port,
        KeyType = row.KeyType,
        PublicKeyBase64 = row.PublicKey,
        FingerprintSha256 = row.FingerprintSha256,
        Status = (KnownHostStatus)row.Status,
        Source = (KnownHostSource)row.Source,
        Comment = row.Comment,
        CreatedAt = SqliteValue.ParseUtc(row.CreatedAt),
        LastSeenAt = string.IsNullOrEmpty(row.LastSeenAt) ? null : SqliteValue.ParseUtc(row.LastSeenAt)
    };

    private sealed class KnownHostRow
    {
        public string Id { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public long Port { get; set; }
        public string KeyType { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public string FingerprintSha256 { get; set; } = string.Empty;
        public long Status { get; set; }
        public long Source { get; set; }
        public string? Comment { get; set; }
        public string? CreatedAt { get; set; }
        public string? LastSeenAt { get; set; }
    }
}
