namespace Kei.Term.Infrastructure.Vault;

using Microsoft.Data.Sqlite;

public partial class InternalVaultManager
{
    private async Task WithSecretWriteLockAsync(Func<Task> operation, CancellationToken ct)
    {
        await _secretWriteLock.WaitAsync(ct);
        try
        {
            await operation();
        }
        finally
        {
            _secretWriteLock.Release();
        }
    }

    private async Task CompletePendingCleanupAsync(SqliteConnection conn, CancellationToken ct)
    {
        if (!_cleanupPending) return;

        try
        {
            // 重建有效页，同时移除设置主密码之前已删除材料遗留的空闲页。
            // VACUUM 也会写 WAL，因此必须在其后检查 TRUNCATE 完成，不能只看 SQL 执行成功。
            using (SqliteCommand vacuum = conn.CreateCommand())
            {
                vacuum.CommandText = "VACUUM;";
                await vacuum.ExecuteNonQueryAsync(ct);
            }

            using (SqliteCommand checkpoint = conn.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                // 活跃读取事务可能阻止截断。及时报告失败并保留持久化重试标记。
                checkpoint.CommandTimeout = 1;
                using SqliteDataReader result = await checkpoint.ExecuteReaderAsync(ct);
                if (!await result.ReadAsync(ct) || result.GetInt64(0) != 0)
                {
                    throw new InvalidOperationException("数据库仍被读取，无法截断历史日志");
                }
            }

            using SqliteCommand clearPending = conn.CreateCommand();
            clearPending.CommandText = "UPDATE vault_metadata SET value = '0' WHERE key = $key;";
            clearPending.Parameters.AddWithValue("$key", KeyCleanupPending);
            await clearPending.ExecuteNonQueryAsync(ct);
            _cleanupPending = false;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException or OperationCanceledException)
        {
            // 新密钥事务不能再回滚，必须明确告知密码已经改变；下次启动、解锁或写入会继续清理。
            throw new InvalidOperationException("主密码已保存，但历史明文清理尚未完成；请结束其他数据库操作后重试或重启应用。", ex);
        }
    }
}
