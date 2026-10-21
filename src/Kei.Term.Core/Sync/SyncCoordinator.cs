using System.Security.Cryptography;
using System.Text.Json;

namespace Kei.Term.Core.Sync;

public sealed class SyncCoordinator(ISyncSnapshotStore store, ISyncPackage package,
    ISyncPassphraseStore secrets, ISyncDeviceId device)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SyncResult> SyncAsync(ISyncLocation location, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        char[]? passphrase = null;
        try
        {
            if (!secrets.IsUnlocked) return new(false, "保险库已锁定");
            passphrase = await secrets.GetPassphraseAsync(ct).ConfigureAwait(false);
            if (passphrase is not { Length: > 0 }) return new(false, "请先设定或加入同步口令");
            string deviceId = device.EnsureCreated();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                SyncObject? remoteObject = await location.ReadAsync(ct).ConfigureAwait(false);
                SyncSnapshot? remote = null;
                if (remoteObject != null)
                {
                    byte[] plaintext = package.Decrypt(remoteObject.Content, passphrase);
                    try
                    {
                        remote = JsonSerializer.Deserialize<SyncSnapshot>(plaintext) ?? throw new JsonException("空同步快照");
                        if (remote.FormatVersion > SyncSnapshot.CurrentFormatVersion)
                            return new(false, "同步包来自更高版本，请升级应用");
                        if (remote.FormatVersion != SyncSnapshot.CurrentFormatVersion)
                            return new(false, "不支持的同步快照版本");
                    }
                    finally { CryptographicOperations.ZeroMemory(plaintext); }
                }

                List<SyncEntity> local = await store.ExportAsync(ct).ConfigureAwait(false);
                List<SyncEntity> merged = Merge(local, remote?.Entities ?? []);
                // 在上传前保留合并结果到本机：读回发现覆盖后，下一轮仍含本轮双方的修改。
                if (!SameEntities(local, merged)) merged = await store.ImportAsync(merged, ct).ConfigureAwait(false);
                if (remote != null && SameEntities(remote.Entities, merged))
                {
                    await store.PurgeTombstonesBeforeAsync(DateTime.UtcNow.AddDays(-180), ct).ConfigureAwait(false);
                    return new(true, "同步成功", remote.WrittenAtUtc, remote.WriterDeviceId);
                }

                SyncSnapshot outgoing = new(SyncSnapshot.CurrentFormatVersion, deviceId, DateTime.UtcNow, merged);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(outgoing);
                byte[] encrypted;
                try { encrypted = package.Encrypt(json, passphrase); }
                finally { CryptographicOperations.ZeroMemory(json); }
                try
                {
                    await location.WriteAsync(encrypted, remoteObject?.VersionTag, ct).ConfigureAwait(false);
                }
                catch (SyncWriteConflictException) { continue; }
                if (!location.SupportsConditionalWrites)
                {
                    SyncObject? readBack = await location.ReadAsync(ct).ConfigureAwait(false);
                    if (readBack == null || !CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(encrypted), SHA256.HashData(readBack.Content))) continue;
                }
                await store.PurgeTombstonesBeforeAsync(DateTime.UtcNow.AddDays(-180), ct).ConfigureAwait(false);
                return new(true, "同步成功", outgoing.WrittenAtUtc, outgoing.WriterDeviceId);
            }
            return new(false, "同步包持续被其他设备修改，已重试 3 次，请稍后重试");
        }
        catch (SyncPackageException ex) when (ex.Error == SyncPackageError.WrongPassphrase)
        { return new(false, "同步口令不正确"); }
        catch (SyncPackageException ex) { return new(false, $"同步包无法读取：{ex.Message}"); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or CryptographicException or
            UnauthorizedAccessException or System.Data.Common.DbException or ArgumentException)
        { return new(false, ex.Message); }
        finally
        {
            if (passphrase != null) Array.Clear(passphrase);
            _gate.Release();
        }
    }

    public static List<SyncEntity> Merge(IEnumerable<SyncEntity> local, IEnumerable<SyncEntity> remote)
    {
        var winners = new Dictionary<(string Type, string Id), SyncEntity>();
        foreach (SyncEntity entity in local.Concat(remote))
        {
            var key = (entity.EntityType, entity.EntityId);
            if (!winners.TryGetValue(key, out SyncEntity? previous) || Compare(entity, previous) > 0)
                winners[key] = entity;
        }
        var trees = winners.Values.Where(e => e.EntityType == SyncSnapshot.TreeEntityType)
            .ToDictionary(e => Guid.Parse(e.EntityId));
        foreach (SyncEntity entity in trees.Values.Where(e => e.DeletedAtUtc == null))
        {
            SyncTreeData data = entity.Data.Deserialize<SyncTreeData>() ?? throw new JsonException("无效会话树节点");
            // 缺失、已删除、非目录以及循环父引用都移到根目录，避免加载树丢失实体。
            var seen = new HashSet<Guid> { data.Node.Id };
            Guid? parent = data.Node.ParentId;
            while (parent != null)
            {
                if (!seen.Add(parent.Value) || !trees.TryGetValue(parent.Value, out SyncEntity? ancestor) ||
                    ancestor.DeletedAtUtc != null || ancestor.Data.Deserialize<SyncTreeData>() is not { Folder: not null } ancestorData)
                {
                    data.Node.ParentId = null;
                    winners[(entity.EntityType, entity.EntityId)] = entity with { Data = JsonSerializer.SerializeToElement(data) };
                    break;
                }
                parent = ancestorData.Node.ParentId;
            }
        }
        return winners.Values.OrderBy(e => e.EntityType, StringComparer.Ordinal).ThenBy(e => e.EntityId, StringComparer.Ordinal).ToList();
    }

    private static int Compare(SyncEntity a, SyncEntity b)
    {
        int time = (a.DeletedAtUtc ?? a.UpdatedAtUtc).CompareTo(b.DeletedAtUtc ?? b.UpdatedAtUtc);
        if (time != 0) return time;
        int device = StringComparer.Ordinal.Compare(a.DeviceId ?? "", b.DeviceId ?? "");
        // 完全相同版本的异常内容也要确定性裁决，避免来回翻转。
        return device != 0 ? device : StringComparer.Ordinal.Compare(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
    }

    private static bool SameEntities(IEnumerable<SyncEntity> a, IEnumerable<SyncEntity> b) =>
        JsonSerializer.Serialize(a.OrderBy(e => e.EntityType, StringComparer.Ordinal).ThenBy(e => e.EntityId, StringComparer.Ordinal)) ==
        JsonSerializer.Serialize(b.OrderBy(e => e.EntityType, StringComparer.Ordinal).ThenBy(e => e.EntityId, StringComparer.Ordinal));
}
