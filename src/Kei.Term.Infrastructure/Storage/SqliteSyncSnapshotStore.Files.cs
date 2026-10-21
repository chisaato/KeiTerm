using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Sync;
using Microsoft.Data.Sqlite;

namespace Kei.Term.Infrastructure.Storage;

public sealed partial class SqliteSyncSnapshotStore
{
    public sealed record ProfileData(string Kind, JsonElement Content);

    private sealed class Stamp
    {
        public string EntityType { get; set; } = "";
        public string EntityId { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string? DeviceId { get; set; }
        public string? DeletedAt { get; set; }
    }

    private async Task ExportTrackedAsync(SqliteConnection conn, SqliteTransaction tx, List<SyncEntity> entities, CancellationToken ct)
    {
        var settings = await ReadObjectAsync(settingsPath, ct);
        var profiles = await ReadObjectAsync(profilesPath, ct);
        if (profiles.Count == 0) profiles = JsonSerializer.SerializeToNode(new Kei.Term.Core.Services.ProfileBundle())!.AsObject();
        // 初始设备可能只有默认值，或仅落盘了另一设备改过的设置叶子；读取语义同设置服务。
        var values = LocalChangeTracking.Flatten(Kei.Term.Infrastructure.Settings.JsonSettingsService.DefaultSyncSnapshot());
        foreach (var item in LocalChangeTracking.Flatten(JsonSerializer.SerializeToElement(settings))) values[item.Key] = item.Value;
        var defaults = new Kei.Term.Core.Services.ProfileBundle();
        foreach (var item in LocalChangeTracking.Flatten(JsonSerializer.SerializeToElement(new
            { defaults.SelectedGuiProfileId, defaults.DefaultTerminalProfileId, defaults.TabBarSettings }), "profiles")) values[item.Key] = item.Value;
        // profiles.json 的磁盘键是 camelCase，01 的设置记账使用 PascalCase。
        foreach (var pair in ProfilePreferences)
            if (profiles.TryGetPropertyValue(pair.Value, out var value))
                foreach (var item in LocalChangeTracking.Flatten(JsonSerializer.SerializeToElement(value), "profiles." + pair.Key)) values[item.Key] = item.Value;
        var profileValues = new Dictionary<string, ProfileData>(StringComparer.Ordinal);
        foreach (string kind in new[] { "guiProfiles", "terminalProfiles" })
            foreach (var profile in profiles[kind]?.AsArray() ?? [])
                if (profile is JsonObject obj && obj["Id"]?.GetValue<string>() is { } id)
                    profileValues.Add(id, new(kind, JsonSerializer.SerializeToElement(obj)));
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var rows = await conn.QueryAsync<MetadataRow>(new CommandDefinition("SELECT node_id, metadata_json FROM session_details;", transaction: tx, cancellationToken: ct));
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.MetadataJson)) continue;
            using var document = JsonDocument.Parse(row.MetadataJson);
            foreach (var property in document.RootElement.EnumerateObject()) metadata[row.NodeId + "/" + property.Name] = property.Value.Clone();
        }
        var stamps = await conn.QueryAsync<Stamp>(new CommandDefinition("SELECT * FROM local_entity_stamps;", transaction: tx, cancellationToken: ct));
        foreach (var stamp in stamps)
        {
            JsonElement data = JsonSerializer.SerializeToElement<object?>(null);
            if (stamp.EntityType == "setting")
            {
                if (SyncLocalOnly.IsSettingExcluded(stamp.EntityId)) continue;
                if (stamp.EntityId.StartsWith("profiles.", StringComparison.Ordinal) ? profilesPath == null : settingsPath == null) continue;
                if (stamp.DeletedAt == null)
                {
                    if (!values.TryGetValue(stamp.EntityId, out var value)) throw new JsonException("设置记账缺少内容：" + stamp.EntityId);
                    using var doc = JsonDocument.Parse(value);
                    data = doc.RootElement.Clone();
                }
            }
            else if (stamp.EntityType == "profile")
            {
                if (profilesPath == null) continue;
                if (stamp.DeletedAt == null)
                {
                    if (!profileValues.TryGetValue(stamp.EntityId, out var value)) throw new JsonException("Profile 记账缺少内容：" + stamp.EntityId);
                    data = JsonSerializer.SerializeToElement(value);
                }
            }
            else if (stamp.EntityType == "metadata")
            {
                if (stamp.DeletedAt == null && !metadata.TryGetValue(stamp.EntityId, out data)) throw new JsonException("连接元数据记账缺少内容：" + stamp.EntityId);
            }
            else throw new InvalidOperationException("不支持的本地记账类型：" + stamp.EntityType);
            entities.Add(new(stamp.EntityType, stamp.EntityId, SqliteValue.ParseUtc(stamp.UpdatedAt), stamp.DeviceId,
                stamp.DeletedAt == null ? null : SqliteValue.ParseUtc(stamp.DeletedAt), data));
        }
    }

    private static readonly Dictionary<string, string> ProfilePreferences = new()
    {
        ["SelectedGuiProfileId"] = "selectedGuiProfileId", ["DefaultTerminalProfileId"] = "defaultTerminalProfileId", ["TabBarSettings"] = "tabBarSettings"
    };

    private sealed class MetadataRow
    {
        public string NodeId { get; set; } = "";
        public string? MetadataJson { get; set; }
    }

    private static async Task ImportTrackedAsync(SqliteConnection conn, SqliteTransaction tx, SyncEntity entity, CancellationToken ct)
    {
        if (entity.EntityType == "metadata")
        {
            int separator = entity.EntityId.IndexOf('/');
            if (separator < 0 || !Guid.TryParse(entity.EntityId[..separator], out _)) throw new JsonException("无效连接元数据标识");
            string id = entity.EntityId[..separator], key = entity.EntityId[(separator + 1)..];
            string? json = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT metadata_json FROM session_details WHERE node_id = @id;", new { id }, tx, cancellationToken: ct));
            var document = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json)!.AsObject();
            if (entity.DeletedAtUtc != null) document.Remove(key);
            else document[key] = JsonNode.Parse(entity.Data.GetRawText());
            int count = await conn.ExecuteAsync(new CommandDefinition("UPDATE session_details SET metadata_json = @json WHERE node_id = @id;", new { id, json = document.ToJsonString() }, tx, cancellationToken: ct));
            if (count == 0 && entity.DeletedAtUtc == null) throw new JsonException("连接元数据缺少所属会话");
        }
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO local_entity_stamps(entity_type,entity_id,updated_at,device_id,deleted_at,revision)
            VALUES (@type,@id,@updated,@device,@deleted,1)
            ON CONFLICT(entity_type,entity_id) DO UPDATE SET updated_at=excluded.updated_at,device_id=excluded.device_id,
                deleted_at=excluded.deleted_at,revision=local_entity_stamps.revision+1;
            """, new { type = entity.EntityType, id = entity.EntityId, updated = Date(entity.UpdatedAtUtc), device = entity.DeviceId, deleted = Date(entity.DeletedAtUtc) }, tx, cancellationToken: ct));
    }

    private async Task CommitFilesAsync(SqliteConnection conn, SqliteTransaction tx, List<SyncEntity> changed, CancellationToken ct)
    {
        var operations = changed.Where(e => e.EntityType is "setting" or "profile").ToList();
        // 必须在提交数据库前完成所有内容验证，但不提前替换任何文件。
        await BuildFileDocumentsAsync(settingsPath, profilesPath, operations, ct);
        if ((await conn.QueryAsync(new CommandDefinition("PRAGMA foreign_key_check;", transaction: tx, cancellationToken: ct))).Any()) throw new JsonException("同步实体外键不完整");
        if (operations.Count > 0)
            await conn.ExecuteAsync(new CommandDefinition("INSERT INTO sync_file_journal(id,payload) VALUES(1,@payload);",
                new { payload = JsonSerializer.Serialize(operations) }, tx, cancellationToken: ct));
        RecoveryCheckpoint?.Invoke(SyncFileRecoveryCheckpoint.BeforeCommit);
        tx.Commit();
        if (operations.Count == 0) return;
        try
        {
            RecoveryCheckpoint?.Invoke(SyncFileRecoveryCheckpoint.AfterCommit);
            await new SyncFileRecovery(factory, settingsPath, profilesPath) { Checkpoint = RecoveryCheckpoint }.ReplayCoreAsync(ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new SyncFileRecoveryException("同步数据已提交，但设置/Profile 待恢复；请修复文件访问问题后重启。", ex);
        }
    }

    internal static async Task<Dictionary<string, JsonObject>> BuildFileDocumentsAsync(string? settingsPath, string? profilesPath,
        IReadOnlyList<SyncEntity> operations, CancellationToken ct)
    {
        var documents = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var entity in operations)
        {
            if (entity.EntityType is not ("setting" or "profile") || entity.EntityType == "setting" && SyncLocalOnly.IsSettingExcluded(entity.EntityId))
                throw new JsonException("恢复记录包含不允许的实体");
            bool profileFile = entity.EntityType == "profile" || entity.EntityId.StartsWith("profiles.", StringComparison.Ordinal);
            string path = (profileFile ? profilesPath : settingsPath) ?? throw new InvalidOperationException("未配置同步设置/Profile 文件");
            if (!documents.TryGetValue(path, out var document)) documents[path] = document = await ReadObjectAsync(path, ct);
            if (entity.EntityType == "profile")
            {
                foreach (string kind in new[] { "guiProfiles", "terminalProfiles" })
                    if (document[kind] is JsonArray items)
                        foreach (var item in items.Where(n => n?["Id"]?.GetValue<string>() == entity.EntityId).ToList()) items.Remove(item);
                if (entity.DeletedAtUtc == null)
                {
                    var data = entity.Data.Deserialize<ProfileData>() ?? throw new JsonException("无效 Profile");
                    if (data.Kind is not ("guiProfiles" or "terminalProfiles") || data.Content.GetProperty("Id").GetString() != entity.EntityId) throw new JsonException("无效 Profile 类型或标识");
                    if (document[data.Kind] == null) document[data.Kind] = new JsonArray();
                    document[data.Kind]!.AsArray().Add(JsonNode.Parse(data.Content.GetRawText()));
                }
            }
            else
            {
                string key = entity.EntityId;
                if (profileFile)
                {
                    string[] parts = key.Split('.');
                    if (parts.Length < 2 || !ProfilePreferences.TryGetValue(parts[1], out var diskKey)) throw new JsonException("无效 Profile 设置路径");
                    key = diskKey + (parts.Length > 2 ? "." + string.Join('.', parts.Skip(2)) : "");
                }
                Patch(document, key, entity.Data, entity.DeletedAtUtc != null);
            }
        }
        return documents;
    }

    private static void Patch(JsonObject document, string path, JsonElement data, bool deleted)
    {
        string[] parts = path.Split('.');
        if (parts.Any(string.IsNullOrWhiteSpace)) throw new JsonException("无效设置路径");
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (document[parts[i]] is not JsonObject child) document[parts[i]] = child = new JsonObject();
            document = child;
        }
        if (deleted) document.Remove(parts[^1]);
        else document[parts[^1]] = JsonNode.Parse(data.GetRawText());
    }

    private static async Task<JsonObject> ReadObjectAsync(string? path, CancellationToken ct)
        => path != null && File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject() : new JsonObject();

    internal static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".sync-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
