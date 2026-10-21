using System.Security.Cryptography;
using System.Text.Json;

namespace Kei.Term.Core.Sync;

public sealed partial class SyncCoordinator
{
    private (string Join, SyncJoinChoice Choice, string Delete)? _pendingJoin;

    private static string PreviewToken(SyncObject? remote, IEnumerable<SyncEntity> local, IEnumerable<SyncEntity> merged, string purpose)
    {
        // 绑定远端密文字节/版本、本机信封和最终预览；不把业务内容带到交互层。
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { purpose, remoteHash = remote == null ? null : Convert.ToHexString(SHA256.HashData(remote.Content)), remote?.VersionTag,
            local = local.OrderBy(e => e.EntityType, StringComparer.Ordinal).ThenBy(e => e.EntityId, StringComparer.Ordinal),
            merged = merged.OrderBy(e => e.EntityType, StringComparer.Ordinal).ThenBy(e => e.EntityId, StringComparer.Ordinal) });
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static List<SyncEntity> Replacement(IEnumerable<SyncEntity> local, IEnumerable<SyncEntity> remote, IEnumerable<SyncEntity> desired, string deviceId)
    {
        var all = Merge(local, remote);
        var chosen = desired.ToDictionary(e => (e.EntityType, e.EntityId));
        // 超过双方版本，包含未来时钟；缺失项必须生成墓碑，不能仅刷新存在项。
        DateTime newest = all.Select(e => e.DeletedAtUtc ?? e.UpdatedAtUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        DateTime now = newest.AddTicks(1);
        return all.Select(e => chosen.TryGetValue((e.EntityType, e.EntityId), out var keep)
            ? keep with { UpdatedAtUtc = now, DeviceId = deviceId, DeletedAtUtc = keep.DeletedAtUtc == null ? null : now }
            : e with { UpdatedAtUtc = now, DeviceId = deviceId, DeletedAtUtc = now }).ToList();
    }

    private static async Task ArchiveAsync(ISyncLocation location, byte[] content, CancellationToken ct)
    {
        await location.WriteHistoryAsync($"{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.age", content, ct).ConfigureAwait(false);
        foreach (var entry in (await location.ListHistoryAsync(ct).ConfigureAwait(false)).OrderByDescending(e => e.WrittenAtUtc).ThenByDescending(e => e.Name).Skip(5))
            await location.DeleteHistoryAsync(entry.Name, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SyncLocalBackup>> ListBackupsAsync(CancellationToken ct = default) => RequiredSafety.ListBackupsAsync(ct);
    private ISyncSafetyStore RequiredSafety => safety ?? throw new InvalidOperationException("未配置同步安全网");

    public async Task<SyncResult> RestoreLocalAsync(string id, CancellationToken ct = default)
        => await RestoreAsync(() => RequiredSafety.ReadBackupAsync(id, ct), ct).ConfigureAwait(false);

    public async Task<SyncResult> RestoreHistoryAsync(ISyncLocation location, string name, CancellationToken ct = default)
        => await RestoreAsync(async () =>
        {
            byte[] bytes = await location.ReadHistoryAsync(name, ct).ConfigureAwait(false) ?? throw new IOException("历史同步包不存在");
            char[] password = await secrets.GetPassphraseAsync(ct).ConfigureAwait(false) ?? throw new InvalidOperationException("请先输入同步口令");
            try
            {
                try { return Decode(bytes, password).Entities; }
                catch (SyncPackageException ex) when (ex.Error == SyncPackageError.WrongPassphrase)
                {
                    char[]? previous = await RequiredSafety.GetPreviousPassphraseAsync(ct).ConfigureAwait(false);
                    if (previous == null) throw;
                    try { return Decode(bytes, previous).Entities; }
                    finally { Array.Clear(previous); }
                }
            }
            finally { Array.Clear(password); }
        }, ct).ConfigureAwait(false);

    private async Task<SyncResult> RestoreAsync(Func<Task<List<SyncEntity>>> load, CancellationToken ct)
    {
        using var suppression = changes?.Suppress();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!secrets.IsUnlocked) return new(false, "保险库已锁定");
            await RequiredSafety.EnsureSyncMasterPasswordAsync(ct).ConfigureAwait(false);
            var desired = await load().ConfigureAwait(false);
            var current = await store.ExportAsync(ct).ConfigureAwait(false);
            var replacement = Replacement(current, desired, desired, device.EnsureCreated());
            DateTime now = DateTime.UtcNow;
            replacement = replacement.Select(e => e.UpdatedAtUtc < now
                ? e with { UpdatedAtUtc = now, DeletedAtUtc = e.DeletedAtUtc == null ? null : now } : e).ToList();
            await RequiredSafety.SaveBackupAsync(current, ct).ConfigureAwait(false);
            await store.ImportGuardedAsync(replacement, current, ct).ConfigureAwait(false);
            return new(true, "恢复成功，下次同步将传播恢复结果");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException) { return new(false, ex.Message); }
        finally { _gate.Release(); }
    }

    private SyncSnapshot Decode(byte[] bytes, char[] password)
    {
        byte[] plain = package.Decrypt(bytes, password);
        try
        {
            var snapshot = JsonSerializer.Deserialize<SyncSnapshot>(plain) ?? throw new JsonException("空同步快照");
            if (snapshot.FormatVersion != SyncSnapshot.CurrentFormatVersion) throw new InvalidOperationException("不支持的同步快照版本，请升级应用");
            return snapshot;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<SyncResult> ResumePassphraseChangeAsync(ISyncLocation location, CancellationToken ct = default)
    {
        char[]? pending = null;
        try
        {
            pending = await RequiredSafety.GetPendingPassphraseAsync(ct).ConfigureAwait(false);
            if (pending == null) return new(false, "没有待完成的口令更换");
            return await ChangePassphraseAsync(location, pending, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { return new(false, ex.Message); }
        finally { if (pending != null) Array.Clear(pending); }
    }

    public async Task<SyncResult> ChangePassphraseAsync(ISyncLocation location, ReadOnlyMemory<char> newPassphrase, CancellationToken ct = default)
    {
        using var suppression = changes?.Suppress();
        if (newPassphrase.IsEmpty) return new(false, "同步口令不能为空");
        char[]? pending;
        try { pending = await RequiredSafety.GetPendingPassphraseAsync(ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex) { return new(false, ex.Message); }
        if (pending == null)
        {
            var sync = await SyncAsync(location, ct).ConfigureAwait(false);
            if (!sync.Success) return sync;
        }
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch { if (pending != null) Array.Clear(pending); throw; }
        char[]? old = null;
        try
        {
            old = await secrets.GetPassphraseAsync(ct).ConfigureAwait(false) ?? throw new InvalidOperationException("缺少旧同步口令");
            var remote = await location.ReadAsync(ct).ConfigureAwait(false) ?? throw new IOException("远端同步包不存在");
            SyncSnapshot snapshot;
            try { snapshot = Decode(remote.Content, old); }
            catch (SyncPackageException) when (pending != null) { snapshot = Decode(remote.Content, pending); }
            // 网络结果未知时只允许继续同一次更换，不覆盖恢复凭据。
            if (pending != null && !pending.AsSpan().SequenceEqual(newPassphrase.Span))
                return new(false, "请使用上次待完成的新口令重试更换");
            var current = await store.ExportAsync(ct).ConfigureAwait(false);
            if (pending == null && !SameEntities(snapshot.Entities, current)) return new(false, "同步数据已变化，请先完成同步再更换口令");
            if (pending == null) await RequiredSafety.SavePendingPassphraseAsync(newPassphrase, ct).ConfigureAwait(false);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(snapshot with { WriterDeviceId = device.EnsureCreated(), WrittenAtUtc = DateTime.UtcNow });
            byte[] encrypted;
            try { encrypted = package.Encrypt(plain, newPassphrase.Span); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            await location.WriteAsync(encrypted, remote.VersionTag, ct).ConfigureAwait(false);
            // 无条件写仍然仅是 best-effort，读回不构成并发无损保证。
            var readBack = await location.ReadAsync(ct).ConfigureAwait(false);
            if (readBack == null || !SHA256.HashData(readBack.Content).AsSpan().SequenceEqual(SHA256.HashData(encrypted)))
                throw new IOException("口令更换上传结果不确定，请重试；新旧口令已在本机保险库保留");
            await secrets.SavePassphraseAsync(newPassphrase, ct).ConfigureAwait(false);
            foreach (var entry in await location.ListHistoryAsync(ct).ConfigureAwait(false)) await location.DeleteHistoryAsync(entry.Name, ct).ConfigureAwait(false);
            await RequiredSafety.ClearPendingPassphraseAsync(ct).ConfigureAwait(false);
            return new(true, "同步口令已更换；不能撤回已下载数据，也不撤销存储访问凭据");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        { return new(false, "口令更换未完成：" + ex.Message); }
        finally { if (pending != null) Array.Clear(pending); if (old != null) Array.Clear(old); _gate.Release(); }
    }

    public async Task<SyncResult> DisableAsync(ISyncLocation location, bool deleteRemote, CancellationToken ct = default)
    {
        using var suppression = changes?.Suppress();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (deleteRemote)
            {
                await location.DeleteAsync(ct).ConfigureAwait(false);
                foreach (var entry in await location.ListHistoryAsync(ct).ConfigureAwait(false)) await location.DeleteHistoryAsync(entry.Name, ct).ConfigureAwait(false);
            }
            await secrets.DeletePassphraseAsync(ct).ConfigureAwait(false);
            await RequiredSafety.ClearPendingPassphraseAsync(ct).ConfigureAwait(false);
            await RequiredSafety.SetJoinedAsync(false, ct).ConfigureAwait(false);
            _pendingJoin = null;
            return new(true, "同步已停用，本机数据未删除");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException) { return new(false, ex.Message); }
        finally { _gate.Release(); }
    }
}
