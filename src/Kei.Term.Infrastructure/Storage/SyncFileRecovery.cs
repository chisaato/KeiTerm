using System.Text.Json;
using Dapper;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Storage.Schema;
using Microsoft.Data.Sqlite;

namespace Kei.Term.Infrastructure.Storage;

public sealed class SyncFileRecoveryException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

// 仅供故障测试，在真实 SQLite/文件边界中断；生产不注入回调。
internal enum SyncFileRecoveryCheckpoint { BeforeCommit, AfterCommit, AfterSettingsFile, AfterProfilesFile, BeforeJournalClear }

// 单份、仅限 settings/Profile 的 redo。目标只取自本机装配，不接受 journal/远端提供路径。
public sealed class SyncFileRecovery(SqliteConnectionFactory factory, string? settingsPath, string? profilesPath)
{
    internal Action<SyncFileRecoveryCheckpoint>? Checkpoint { get; init; }

    // 启动入口，必须先于任何设置预读、加载或记账。重复调用不重新盖章。
    public async Task<int> PrepareStartupAsync(CancellationToken ct = default)
    {
        using var persistence = await LocalPersistenceGate.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            int version = await SchemaMigrator.MigrateAsync(factory, ct).ConfigureAwait(false);
            await ReplayCoreAsync(ct).ConfigureAwait(false);
            return version;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not SyncFileRecoveryException)
        {
            throw new SyncFileRecoveryException("设置/Profile 恢复失败，已暂停设置加载；请保留数据库并修复文件访问问题后重启。", ex);
        }
    }

    internal static async Task EnsureReadyAsync(SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
    {
        // 老版本局部表测试/只读未迁移库不建立任何新表；生产入口先执行迁移。
        if (await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sync_file_journal';", transaction: tx, cancellationToken: ct)) == 0) return;
        if (await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM sync_file_journal;", transaction: tx, cancellationToken: ct)) != 0)
            throw new SyncFileRecoveryException("设置/Profile 待恢复，已暂停加载、保存及同步；请重启完成恢复。旧文件不会记为本地修改。");
    }

    // 调用方已持有 LocalPersistenceGate。运行中失败不自动越过 journal 后保存旧内存草稿。
    internal async Task ReplayCoreAsync(CancellationToken ct)
    {
        using var conn = await factory.OpenAsync(ct).ConfigureAwait(false);
        // 重放和清记录持有同一个写事务；并发启动的恢复者等锁后必须重新读取，不能重放旧副本。
        using var tx = conn.BeginTransaction(deferred: false);
        string? payload = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload FROM sync_file_journal WHERE id=1;", transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
        if (payload == null) return;
        var operations = JsonSerializer.Deserialize<List<SyncEntity>>(payload) ?? throw new JsonException("恢复记录为空");
        if (operations.Count == 0) throw new JsonException("恢复记录没有文件操作");
        var documents = await SqliteSyncSnapshotStore.BuildFileDocumentsAsync(settingsPath, profilesPath, operations, ct).ConfigureAwait(false);
        foreach (var (path, document) in documents)
        {
            await SqliteSyncSnapshotStore.WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(document), ct).ConfigureAwait(false);
            Checkpoint?.Invoke(path == settingsPath ? SyncFileRecoveryCheckpoint.AfterSettingsFile : SyncFileRecoveryCheckpoint.AfterProfilesFile);
        }
        Checkpoint?.Invoke(SyncFileRecoveryCheckpoint.BeforeJournalClear);
        int cleared = await conn.ExecuteAsync(new CommandDefinition("DELETE FROM sync_file_journal WHERE id=1 AND payload=@payload;", new { payload }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (cleared != 1) throw new SyncFileRecoveryException("恢复记录发生变化，已暂停设置加载。");
        tx.Commit();
    }
}
