using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kei.Term.Tests;

// 删除保护的 TOCTOU 最小缓解：消费端遇到悬空 SecretId（条目已不存在）时不崩溃、跳过该方法并记日志。
public class VaultSecretDanglingReferenceTests : IDisposable
{
    private readonly string _dbPath = VaultTestDb.NewPath();
    private readonly ScriptedInteraction _ui = new();
    private readonly FixedSettingsService _settings = new(new AppSettings { PreferSystemAgent = false });
    private readonly ListLogger<AuthMaterialCollector> _logger = new();

    private string ConnStr => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private static ResolvedSessionConfig Config() => new(
        Guid.NewGuid(), "target.example", "target.example", 22, "ops", null, "xterm-256color", null, null, new Dictionary<string, string>());

    [Fact]
    public async Task DanglingSecretReference_SkipsMethodWithoutCrash_AndLogs()
    {
        Guid identityId = Guid.NewGuid();
        await VaultTestDb.CreateAsync(ConnStr, identityId);
        Guid dangling = Guid.NewGuid();
        string keyPath = Path.Combine(Path.GetTempPath(), $"keiterm_test_key_{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(keyPath, PrivateKeyFormatTests.PlainOpenSsh);
        try
        {
            // 身份同时引用一个已被删除的条目（悬空）和一把有效的文件私钥
            var identity = new Identity
            {
                Id = identityId,
                Name = "Dangling",
                Username = "ops",
                Methods =
                [
                    new VaultPasswordMethod { SecretId = dangling },
                    new FilePrivateKeyMethod { KeyFilePath = keyPath, PassphraseMode = PassphrasePersistence.AlwaysAsk },
                ],
            };
            var identities = new SqliteIdentityRepository(ConnStr);
            await identities.SaveAsync(identity);
            var vault = VaultTestDb.CreateVault(ConnStr);
            var session = new VaultSessionService(vault, vault, _settings, () => _ui);
            var collector = new AuthMaterialCollector(identities, _settings, session, () => _ui, _logger);

            CollectedAuth? result = await collector.CollectTargetAsync(
                new ConnectionRequest(Config() with { IdentityId = identityId }, UseIdentity: true));

            // 悬空引用被跳过，其余方法照常物化：不崩溃、不静默免密
            Assert.NotNull(result);
            MaterializedAuthMethod material = Assert.Single(result!.Materials);
            Assert.Equal(AuthMaterialKind.PrivateKey, material.Kind);
            Assert.Equal(PrivateKeyFormatTests.PlainOpenSsh, material.Secret!.PrivateKeyContent);
            Assert.Contains(_logger.Messages, line => line.Contains("条目不存在或不可用") && line.Contains(dangling.ToString()));
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    // 只记录全部日志文本，用于断言悬空引用被显式记录
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
