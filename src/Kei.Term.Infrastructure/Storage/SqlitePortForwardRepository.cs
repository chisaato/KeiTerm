namespace Kei.Term.Infrastructure.Storage;

using Dapper;
using Microsoft.Data.Sqlite;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;
using Kei.Term.Infrastructure.Storage.Schema;

public class SqlitePortForwardRepository : IPortForwardRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqlitePortForwardRepository(string connectionString)
        : this(new SqliteConnectionFactory(connectionString))
    {
    }

    public SqlitePortForwardRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<PortForward>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        IEnumerable<ForwardRow> rows = await conn.QueryAsync<ForwardRow>(new CommandDefinition(@"
            SELECT id, session_id, name, mode, bind_address, listen_port, destination_host, destination_port
            FROM port_forwards
            WHERE session_id = @id
            ORDER BY listen_port ASC, mode ASC;
        ", new { id = sessionId.ToString() }, cancellationToken: ct));
        return rows.Select(ToModel).ToList();
    }

    public async Task SaveAsync(PortForward forward, CancellationToken ct = default)
    {
        string bind = string.IsNullOrWhiteSpace(forward.BindAddress) ? "127.0.0.1" : forward.BindAddress.Trim();
        if (forward.ListenPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(forward), "监听端口必须在 1–65535");
        }

        string mode = ToMode(forward.Mode);
        string? destHost = forward.DestinationHost?.Trim();
        int? destPort = forward.DestinationPort;
        if (forward.Mode == PortForwardMode.Dynamic)
        {
            destHost = null;
            destPort = null;
        }
        else if (string.IsNullOrWhiteSpace(destHost) || destPort is null or < 1 or > 65535)
        {
            throw new ArgumentException("Local / Remote 转发必须填写目标主机和端口", nameof(forward));
        }

        using SqliteConnection conn = await _factory.OpenAsync(ct);
        long occupied = await conn.ExecuteScalarAsync<long>(new CommandDefinition(@"
            SELECT COUNT(*) FROM port_forwards
            WHERE session_id = @sessionId
              AND bind_address = @bind
              AND listen_port = @port
              AND mode = @mode
              AND id != @id;
        ", new
        {
            sessionId = forward.SessionId.ToString(),
            bind,
            port = forward.ListenPort,
            mode,
            id = forward.Id.ToString()
        }, cancellationToken: ct));
        if (occupied > 0)
        {
            throw new PortForwardConflictException($"端口 {bind}:{forward.ListenPort} 已被该会话占用");
        }

        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO port_forwards (id, session_id, name, mode, bind_address, listen_port, destination_host, destination_port)
            VALUES (@Id, @SessionId, @Name, @Mode, @Bind, @Listen, @DestHost, @DestPort)
            ON CONFLICT(id) DO UPDATE SET
                session_id = excluded.session_id,
                name = excluded.name,
                mode = excluded.mode,
                bind_address = excluded.bind_address,
                listen_port = excluded.listen_port,
                destination_host = excluded.destination_host,
                destination_port = excluded.destination_port;
        ", new
        {
            Id = forward.Id.ToString(),
            SessionId = forward.SessionId.ToString(),
            Name = string.IsNullOrWhiteSpace(forward.Name) ? null : forward.Name.Trim(),
            Mode = mode,
            Bind = bind,
            Listen = forward.ListenPort,
            DestHost = destHost,
            DestPort = destPort
        }, cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM port_forwards WHERE id = @id;",
            new { id = id.ToString() },
            cancellationToken: ct));
    }

    private static string ToMode(PortForwardMode mode) => mode switch
    {
        PortForwardMode.Remote => "remote",
        PortForwardMode.Dynamic => "dynamic",
        _ => "local"
    };

    private static PortForwardMode ParseMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        "remote" => PortForwardMode.Remote,
        "dynamic" => PortForwardMode.Dynamic,
        _ => PortForwardMode.Local
    };

    private static PortForward ToModel(ForwardRow row)
    {
        var mode = ParseMode(row.Mode);
        return new PortForward
        {
            Id = Guid.Parse(row.Id),
            SessionId = Guid.Parse(row.SessionId),
            Name = row.Name,
            Mode = mode,
            BindAddress = row.BindAddress,
            ListenPort = (int)row.ListenPort,
            DestinationHost = mode == PortForwardMode.Dynamic ? null : row.DestinationHost,
            DestinationPort = mode == PortForwardMode.Dynamic ? null : row.DestinationPort is { } port ? (int)port : null
        };
    }

    private sealed class ForwardRow
    {
        public string Id { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string Mode { get; set; } = "local";
        public string BindAddress { get; set; } = "127.0.0.1";
        public long ListenPort { get; set; }
        public string? DestinationHost { get; set; }
        public long? DestinationPort { get; set; }
    }
}
