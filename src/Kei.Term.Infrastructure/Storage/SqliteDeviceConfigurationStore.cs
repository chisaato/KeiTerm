using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;

namespace Kei.Term.Infrastructure.Storage;

// 类别验证器是可复用扩展点；默认只接收已知串口参数，不提供任意秘密 JSON 仓库。
public sealed class SqliteDeviceConfigurationStore(SqliteConnectionFactory factory,
    IReadOnlyDictionary<string, Action<IReadOnlyDictionary<string, JsonElement>>>? categoryValidators = null) : IDeviceConfigurationStore
{
    public const string EntityType = "device-configuration";
    internal static string RecordId(Guid owner, string category, DeviceConfigurationTarget target)
        => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object?[]
            { owner.ToString(), category, target.OperatingSystem, target.TargetDeviceId })))[..16]).ToString();

    internal void Validate(DeviceConfiguration config)
    {
        config.Target.Validate();
        if (config.Category == "serial")
        {
            foreach (var field in config.Fields)
            {
                bool valid = field.Key switch
                {
                    "portName" => field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() is { Length: > 0 and < 512 } port
                        && !port.Contains('\n') && !port.Contains('\r') && !port.Contains("PRIVATE KEY", StringComparison.Ordinal),
                    "baudRate" => field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out int baud) && baud > 0,
                    "dataBits" => field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out int bits) && bits is >= 5 and <= 8,
                    "parity" => field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() is "None" or "Odd" or "Even" or "Mark" or "Space",
                    "stopBits" => field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() is "One" or "Two" or "OnePointFive",
                    "flowControl" => field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() is "None" or "RtsCts" or "XonXoff",
                    "dtrEnable" or "rtsEnable" => field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    _ => false
                };
                if (!valid) throw new ArgumentException($"无效或不受支持的非秘密串口字段：{field.Key}");
            }
        }
        else if (categoryValidators != null && categoryValidators.TryGetValue(config.Category, out var validate)) validate(config.Fields);
        else throw new ArgumentException("未注册的设备配置类别");
    }

    public async Task<IReadOnlyList<DeviceConfiguration>> GetAsync(Guid ownerId, string category, CancellationToken ct = default)
    {
        using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ConfigurationRow>(new CommandDefinition(@"
            SELECT c.* FROM device_configurations c JOIN tree_nodes t ON t.id=c.owner_id
            WHERE c.owner_id=@owner AND c.category=@category AND c.deleted_at IS NULL AND t.deleted_at IS NULL;",
            new { owner = ownerId.ToString(), category }, cancellationToken: ct));
        return rows.Select(r => new DeviceConfiguration(ownerId, r.Category, new(r.Os, r.TargetDeviceId),
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.FieldsJson)!)).ToList();
    }

    public async Task SaveAsync(DeviceConfiguration configuration, string sourceDeviceId, CancellationToken ct = default)
    {
        Validate(configuration);
        using var gate = await LocalPersistenceGate.EnterAsync(ct);
        using var conn = await factory.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        string owner = configuration.OwnerId.ToString();
        if (await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM tree_nodes WHERE id=@owner AND deleted_at IS NULL;", new { owner }, tx, cancellationToken: ct)) != 1)
            throw new InvalidOperationException("配置归属对象不存在或已删除");
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO device_configurations(id,owner_id,category,os,target_device_id,fields_json,updated_at,device_id,revision)
            VALUES(@id,@owner,@category,@os,@target,@fields,@now,@source,1)
            ON CONFLICT(id) DO UPDATE SET fields_json=excluded.fields_json,updated_at=excluded.updated_at,
                device_id=excluded.device_id,deleted_at=NULL,revision=device_configurations.revision+1;",
            new { id = RecordId(configuration.OwnerId, configuration.Category, configuration.Target), owner,
                category = configuration.Category, os = configuration.Target.OperatingSystem, target = configuration.Target.TargetDeviceId,
                fields = JsonSerializer.Serialize(configuration.Fields), now = SqliteValue.FormatUtc(DateTime.UtcNow), source = sourceDeviceId }, tx, cancellationToken: ct));
        tx.Commit();
    }

    public async Task DeleteAsync(Guid ownerId, string category, DeviceConfigurationTarget target, string sourceDeviceId, CancellationToken ct = default)
    {
        target.Validate();
        using var gate = await LocalPersistenceGate.EnterAsync(ct);
        using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE device_configurations SET fields_json='{}',deleted_at=@now,updated_at=@now,device_id=@source,
                revision=revision+1 WHERE id=@id;",
            new { id = RecordId(ownerId, category, target), now = SqliteValue.FormatUtc(DateTime.UtcNow), source = sourceDeviceId }, cancellationToken: ct));
    }

    private sealed class ConfigurationRow
    {
        public string Category { get; set; } = "";
        public string? Os { get; set; }
        public string? TargetDeviceId { get; set; }
        public string FieldsJson { get; set; } = "{}";
    }
}
