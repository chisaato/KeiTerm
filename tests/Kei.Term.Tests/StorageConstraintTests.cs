using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using Dapper;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;
using Microsoft.Data.Sqlite;

namespace Kei.Term.Tests;

public class StorageConstraintTests
{
    public static TheoryData<DateTime> Dates => new()
    {
        new DateTime(2026, 10, 19, 12, 34, 56, DateTimeKind.Utc),
        new DateTime(2026, 10, 19, 12, 34, 56, DateTimeKind.Local),
        new DateTime(2026, 10, 19, 12, 34, 56, DateTimeKind.Unspecified),
        new DateTime(2026, 10, 19, 12, 34, 56, 789, DateTimeKind.Utc),
        new DateTime(2026, 10, 19, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567),
        new DateTime(2026, 10, 19, 12, 34, 56, DateTimeKind.Local).AddTicks(7654321),
        new DateTime(2026, 10, 19, 12, 34, 56, DateTimeKind.Unspecified).AddTicks(1),
        DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
        DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
        DateTime.MinValue,
        DateTime.MaxValue,
    };

    [Theory]
    [MemberData(nameof(Dates))]
    public void UtcText_RoundTripsKindAndEveryTick(DateTime value)
    {
        string text = SqliteValue.FormatUtc(value);
        DateTime parsed = SqliteValue.ParseUtc(text);
        DateTime expected = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(expected.Ticks, parsed.Ticks);
        Assert.Equal(value.Kind == DateTimeKind.Unspecified ? 27 : 28, text.Length);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-02-30T00:00:00Z")]
    [InlineData("10000-01-01T00:00:00Z")]
    public void InvalidUtcText_ThrowsFormatException(string text)
        => Assert.Throws<FormatException>(() => SqliteValue.ParseUtc(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void MissingUtcText_FallsBackToCurrentUtc(string? text)
    {
        DateTime before = DateTime.UtcNow;
        DateTime parsed = SqliteValue.ParseUtc(text);
        DateTime after = DateTime.UtcNow;
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.InRange(parsed, before, after);
    }

    [Fact]
    public void OffsetUtcText_NormalizesToUtcWithoutLosingTicks()
    {
        DateTime parsed = SqliteValue.ParseUtc("2026-10-19T12:34:56.1234567+05:30");
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(new DateTime(2026, 10, 19, 7, 4, 56, DateTimeKind.Utc).AddTicks(1234567), parsed);
    }

    // 每个当前外键一行；另一个测试用实际迁移后的元数据校验清单没有漏项。
    public static TheoryData<string, string, string, string, string, string> ForeignKeys => new()
    {
        { "tree_nodes", "parent_id", "leaf", "tree_nodes", "root", "CASCADE" },
        { "identity_secrets", "identity_id", "identity", "identities", "identity", "CASCADE" },
        { "external_editor_paths", "editor_id", "path", "external_editors", "editor", "CASCADE" },
        { "file_associations", "editor_id", "association", "external_editors", "editor", "CASCADE" },
        { "session_details", "node_id", "leaf", "tree_nodes", "leaf", "CASCADE" },
        { "session_details", "identity_id", "leaf", "identities", "identity", "SET NULL" },
        { "proxy_secrets", "proxy_id", "proxy", "proxies", "proxy", "CASCADE" },
        { "port_forwards", "session_id", "forward", "tree_nodes", "leaf", "CASCADE" },
    };

    [Fact]
    public async Task MigratedSchema_EveryForeignKeyHasABehaviorCase()
    {
        using TestDatabase database = new();
        await SchemaMigrator.MigrateAsync(database.Factory);
        using SqliteConnection connection = await database.Factory.OpenAsync();
        var actual = (await connection.QueryAsync<string>("""
            SELECT m.name || '.' || f."from" || '->' || f."table" || ':' || f.on_delete
            FROM sqlite_master m JOIN pragma_foreign_key_list(m.name) f
            WHERE m.type = 'table'
            ORDER BY 1;
            """)).ToArray();
        string[] expected = ForeignKeys.Select(row => $"{row[0]}.{row[1]}->{row[3]}:{row[5]}")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(ForeignKeys))]
    public async Task ForeignKeys_RejectOrphansAndApplyDeleteAction(
        string child, string foreignColumn, string childId, string parent, string parentId, string action)
    {
        using TestDatabase database = new();
        Assert.Equal(SchemaMigrator.LatestVersion, await SchemaMigrator.MigrateAsync(database.Factory));
        using SqliteConnection connection = await database.Factory.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>("PRAGMA foreign_keys;"));
        // 每次新开连接都必须设置，而非仅迁移时设置。
        using (SqliteConnection second = await database.Factory.OpenAsync())
            Assert.Equal(1, await second.ExecuteScalarAsync<long>("PRAGMA foreign_keys;"));

        await connection.ExecuteAsync(SeedSql);
        string childKey = child switch
        {
            "identity_secrets" => "identity_id",
            "proxy_secrets" => "proxy_id",
            "session_details" => "node_id",
            _ => "id",
        };
        string predicate = $"FROM {child} WHERE {childKey} = @childId";
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) {predicate}", new { childId }));
        SqliteException orphan = await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync(
            $"UPDATE {child} SET {foreignColumn} = 'missing-parent' WHERE {childKey} = @childId", new { childId }));
        Assert.Equal(787, orphan.SqliteExtendedErrorCode);

        await connection.ExecuteAsync($"DELETE FROM {parent} WHERE id = @parentId", new { parentId });
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {parent} WHERE id = @parentId", new { parentId }));
        if (action == "CASCADE")
        {
            Assert.Equal(0, await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) {predicate}", new { childId }));
        }
        else
        {
            Assert.Equal(1, await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) {predicate}", new { childId }));
            Assert.Null(await connection.ExecuteScalarAsync<string?>($"SELECT {foreignColumn} {predicate}", new { childId }));
        }

        if (child == "tree_nodes")
        {
            Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM session_details;"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM port_forwards;"));
        }

        Assert.Empty(await connection.QueryAsync("PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData(@"\bnew\s+(?:global::)?(?:Microsoft\.Data\.Sqlite\.)?SqliteConnection\s*\(", "Kei.Term.Infrastructure/Storage/SqliteConnectionFactory.cs")]
    [InlineData(@"\bCREATE\s+TABLE\b", "Kei.Term.Infrastructure/Storage/Schema/SchemaMigrations.cs")]
    public void StorageArchitecture_OnlyCentralEntryPointsMayCreateConnectionsAndTables(string pattern, string allowed)
    {
        DirectoryInfo? root = new(Path.GetDirectoryName(SourceFilePath())!);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Kei.Term.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string source = Path.Combine(root.FullName, "src");
        List<string> violations = [];
        foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (relative == allowed || relative.Split('/').Any(part => part is "obj" or "bin")) continue;
            string text = File.ReadAllText(file);
            // 保留字符串（SQL 就在字符串中），仅抹去注释；保留换行以报告实际源码行号。
            string code = Regex.Replace(text, "\"\"\"[\\s\\S]*?\"\"\"|@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/",
                match => match.Value.StartsWith("//", StringComparison.Ordinal) || match.Value.StartsWith("/*", StringComparison.Ordinal)
                    ? Regex.Replace(match.Value, @"[^\r\n]", " ") : match.Value);
            foreach (Match match in Regex.Matches(code, pattern, RegexOptions.IgnoreCase))
                violations.Add($"{relative}:{1 + code[..match.Index].Count(c => c == '\n')}: {match.Value}");
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    // VSTest 工作目录与 artifacts 目录均可能不在仓库内。
    private static string SourceFilePath([CallerFilePath] string path = "") => path;

    private const string SeedSql = """
        INSERT INTO tree_nodes(id, parent_id, node_type, name, created_at, updated_at)
            VALUES ('root', NULL, 0, 'Root', '2026-10-19', '2026-10-19'),
                   ('leaf', 'root', 1, 'Leaf', '2026-10-19', '2026-10-19');
        INSERT INTO identities(id, name, created_at, updated_at) VALUES ('identity', 'Identity', '2026-10-19', '2026-10-19');
        INSERT INTO identity_secrets(identity_id, secrets_blob) VALUES ('identity', X'0102');
        INSERT INTO external_editors(id, name, created_at, updated_at) VALUES ('editor', 'Editor', '2026-10-19', '2026-10-19');
        INSERT INTO external_editor_paths(id, editor_id, os, path) VALUES ('path', 'editor', 'linux', '/usr/bin/editor');
        INSERT INTO file_associations(id, pattern, editor_id, created_at, updated_at)
            VALUES ('association', '*.txt', 'editor', '2026-10-19', '2026-10-19');
        INSERT INTO session_details(node_id, host, identity_id) VALUES ('leaf', 'localhost', 'identity');
        INSERT INTO proxies(id, name, config_json, created_at, updated_at) VALUES ('proxy', 'Proxy', '{}', '2026-10-19', '2026-10-19');
        INSERT INTO proxy_secrets(proxy_id, secrets_blob, encryption_algorithm) VALUES ('proxy', X'0102', 'AES-256-GCM');
        INSERT INTO port_forwards(id, session_id, mode, bind_address, listen_port) VALUES ('forward', 'leaf', 'local', '127.0.0.1', 8080);
        """;

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"keiterm-storage-{Guid.NewGuid():N}");
        public SqliteConnectionFactory Factory { get; }

        public TestDatabase()
        {
            Directory.CreateDirectory(_directory);
            Factory = new SqliteConnectionFactory(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "test.db"),
                Pooling = false,
            }.ToString());
        }

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
