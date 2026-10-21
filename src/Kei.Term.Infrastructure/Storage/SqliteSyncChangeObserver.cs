namespace Kei.Term.Infrastructure.Storage;

using Kei.Term.Core.Sync;
using Microsoft.Data.Sqlite;
using SQLitePCL;

// 所有仓储写入都经过这条连接。只把会刷新同步记账的语句当成一次本地修改，展开折叠不算。
internal static class SqliteSyncChangeObserver
{
    private static readonly strdelegate_trace Trace = OnTrace;

    public static void Attach(SqliteConnection connection, ISyncChangeSignal changes)
        => raw.sqlite3_trace(connection.Handle, Trace, changes);

    internal static bool IsContentWrite(string? statement)
    {
        if (string.IsNullOrWhiteSpace(statement)) return false;
        string sql = statement.TrimStart();
        if (sql.Contains("local_vault_secrets", StringComparison.OrdinalIgnoreCase)) return false;
        bool writes = sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase);
        if (!writes) return false;
        return sql.Contains("updated_at", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("local_entity_stamps", StringComparison.OrdinalIgnoreCase);
    }

    private static void OnTrace(object user, string statement)
    {
        if (user is not ISyncChangeSignal changes || !IsContentWrite(statement)) return;
        changes.Publish();
    }
}
