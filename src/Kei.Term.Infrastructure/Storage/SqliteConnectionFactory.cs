namespace Kei.Term.Infrastructure.Storage;

using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

// 所有仓储共用的连接入口：统一连接级 PRAGMA，避免各仓储各自复制一份开连接样板
public sealed class SqliteConnectionFactory
{
    // busy_timeout：后台任务（传输/保存）与 UI 并发写时等待锁而非立即 SQLITE_BUSY
    private const int BusyTimeoutMilliseconds = 5000;

    static SqliteConnectionFactory()
    {
        // 列名 snake_case 自动映射到行对象 PascalCase 属性（node_id -> NodeId）
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public SqliteConnectionFactory(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        // foreign_keys 为连接级设置，每条连接都必须重新开启，否则级联约束失效
        await conn.ExecuteAsync(new CommandDefinition(
            $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {BusyTimeoutMilliseconds};",
            cancellationToken: ct));

        return conn;
    }
}

// 时间列统一以 ISO-8601 往返格式存 TEXT；读取保持 UTC Kind，避免 DateTime.Parse 转成本地时间
internal static class SqliteValue
{
    public static string FormatUtc(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return utc.ToString("O", CultureInfo.InvariantCulture);
    }

    public static DateTime ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTime.UtcNow;
        }

        DateTime parsed = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        // 历史数据可能不带时区标记（Unspecified），按写入约定视为 UTC
        return parsed.Kind switch
        {
            DateTimeKind.Local => parsed.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
            _ => parsed
        };
    }

    public static Guid? ParseGuid(string? value)
        => string.IsNullOrEmpty(value) ? null : Guid.Parse(value);
}
