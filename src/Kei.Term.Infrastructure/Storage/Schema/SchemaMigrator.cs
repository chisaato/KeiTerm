namespace Kei.Term.Infrastructure.Storage.Schema;

using Dapper;
using Microsoft.Data.Sqlite;

// 单条迁移：版本号严格递增；Apply 在迁移事务内执行
public sealed record SchemaMigration(
    int Version,
    string Name,
    Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> Apply);

// 基于 PRAGMA user_version 的版本化迁移：
// - 全库 Schema 集中在 SchemaMigrations 一处定义，仓储不再各自 CREATE TABLE
// - 每条迁移与 user_version 更新在同一事务内提交，失败整体回滚，可安全重入
// - v1 为「基线」：兼容 user_version=0 的存量库（旧版本靠 IF NOT EXISTS/补列演进而来）
public static class SchemaMigrator
{
    public static int LatestVersion => SchemaMigrations.All[^1].Version;

    public static async Task<int> MigrateAsync(SqliteConnectionFactory factory, CancellationToken ct = default)
    {
        using SqliteConnection conn = await factory.OpenAsync(ct);

        // WAL 为库文件级持久设置，且不能在事务内切换：读写并发更友好，崩溃恢复更稳
        await conn.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode = WAL;", cancellationToken: ct));

        int current = await GetUserVersionAsync(conn, null, ct);
        if (current >= LatestVersion)
        {
            return current;
        }

        foreach (SchemaMigration migration in SchemaMigrations.All)
        {
            // BEGIN IMMEDIATE：并发启动的两个迁移者只有一个能拿到写锁，另一个等锁后在事务内复检版本
            using SqliteTransaction tx = conn.BeginTransaction(deferred: false);
            int versionInTx = await GetUserVersionAsync(conn, tx, ct);
            if (migration.Version <= versionInTx)
            {
                tx.Rollback();
                continue;
            }

            await migration.Apply(conn, tx, ct);

            // user_version 不支持参数绑定；Version 为内部常量整数，无注入风险
            await conn.ExecuteAsync(new CommandDefinition(
                $"PRAGMA user_version = {migration.Version};",
                transaction: tx,
                cancellationToken: ct));
            tx.Commit();
            current = migration.Version;
        }

        return current;
    }

    public static async Task<int> GetUserVersionAsync(SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
        => await conn.ExecuteScalarAsync<int>(new CommandDefinition("PRAGMA user_version;", transaction: tx, cancellationToken: ct));
}
