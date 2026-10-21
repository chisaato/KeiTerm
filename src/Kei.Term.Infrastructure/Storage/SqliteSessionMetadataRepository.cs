namespace Kei.Term.Infrastructure.Storage;

using System.Text.Json;
using Dapper;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Transfers;
using Microsoft.Data.Sqlite;

// session_details.metadata_json 的按键读写。只动 transferProbe，不碰 revision，也不整行替换。
public sealed class SqliteSessionMetadataRepository : ISessionMetadataRepository
{
    private const string ProbeKey = "transferProbe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SqliteConnectionFactory _factory;

    public SqliteSessionMetadataRepository(SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public SqliteSessionMetadataRepository(string connectionString)
        : this(new SqliteConnectionFactory(connectionString))
    {
    }

    public async Task<TransferProbeResult?> GetTransferProbeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(cancellationToken);
        string? json = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT metadata_json FROM session_details WHERE node_id = @id AND deleted_at IS NULL;",
            new { id = sessionId.ToString() },
            cancellationToken: cancellationToken));
        return Read(json);
    }

    public async Task<bool> SetTransferProbeAsync(
        Guid sessionId,
        TransferProbeResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        string payload = JsonSerializer.Serialize(new ProbeDto
        {
            Platform = result.Platform.ToString(),
            Hashes = result.Hashes.Select(item => item.ToString()).ToList(),
            Archives = result.Archives.Select(item => item.ToString()).ToList(),
            ProbedAtUtc = SqliteValue.FormatUtc(result.ProbedAtUtc),
        }, JsonOptions);

        using SqliteConnection conn = await _factory.OpenAsync(cancellationToken);
        using var tx = conn.BeginTransaction();
        // 损坏的旧文档整段换成 {}，否则 json_set 会把这一列写成 NULL。
        int rows = await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE session_details
            SET metadata_json = json_set(
                CASE WHEN json_valid(metadata_json) THEN metadata_json ELSE '{}' END,
                '$.transferProbe',
                json(@payload))
            WHERE node_id = @id AND deleted_at IS NULL;
        ", new { id = sessionId.ToString(), payload }, transaction: tx, cancellationToken: cancellationToken));
        if (rows > 0)
            await SqliteLocalChangeTracker.TrackCoreAsync(conn, tx, "metadata", sessionId + "/" + ProbeKey, _factory.DeviceId, false, cancellationToken);
        tx.Commit();
        return rows > 0;
    }

    public async Task ClearTransferProbeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(cancellationToken);
        using var tx = conn.BeginTransaction();
        int rows = await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE session_details
            SET metadata_json = json_remove(
                CASE WHEN json_valid(metadata_json) THEN metadata_json ELSE '{}' END,
                '$.transferProbe')
            WHERE node_id = @id AND deleted_at IS NULL;
        ", new { id = sessionId.ToString() }, transaction: tx, cancellationToken: cancellationToken));
        if (rows > 0)
            await SqliteLocalChangeTracker.TrackCoreAsync(conn, tx, "metadata", sessionId + "/" + ProbeKey, _factory.DeviceId, true, cancellationToken);
        tx.Commit();
    }

    private static TransferProbeResult? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(ProbeKey, out JsonElement probe)
                || probe.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            ProbeDto? dto = probe.Deserialize<ProbeDto>(JsonOptions);
            if (dto == null || string.IsNullOrWhiteSpace(dto.ProbedAtUtc))
            {
                return null;
            }

            RemotePlatform platform = Enum.TryParse(dto.Platform, out RemotePlatform parsed)
                ? parsed
                : RemotePlatform.Unknown;
            return new TransferProbeResult(platform, ParseList<TransferHashAlgorithm>(dto.Hashes), ParseList<TransferArchiveFormat>(dto.Archives), SqliteValue.ParseUtc(dto.ProbedAtUtc));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IReadOnlyList<T> ParseList<T>(IEnumerable<string>? values)
        where T : struct, Enum
    {
        if (values == null)
        {
            return [];
        }

        List<T> result = [];
        HashSet<T> seen = [];
        foreach (string value in values)
        {
            if (Enum.TryParse(value, out T parsed) && Enum.IsDefined(parsed) && seen.Add(parsed))
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    private sealed class ProbeDto
    {
        public string? Platform { get; set; }

        public List<string>? Hashes { get; set; }

        public List<string>? Archives { get; set; }

        public string? ProbedAtUtc { get; set; }
    }
}
