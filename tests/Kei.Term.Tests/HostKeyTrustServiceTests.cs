using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Security;
using Kei.Term.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

// 信任服务 + SQLite 信任库：TOFU、变更阻断、两段式人工确认、导入条目参与匹配
public class HostKeyTrustServiceTests : IDisposable
{
    private const string KeyA = "AAAAC3NzaC1lZDI1NTE5AAAAINNOCjYfYNvN56zScDjq/dDohnNs4wdauZTYtC54QykV";
    private const string KeyB = "AAAAC3NzaC1lZDI1NTE5AAAAIN/OYsUEL5deLJb9X+lVVMyeUpEed4y/0VWHOn369umv";
    private const string HashedExampleCom = "|1|qmrjyytC429Tb1XlRAA0VQKEX3I=|kC2qKmAL6B1TbF7DPWo+lb+eKzQ=";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"keiterm_hostkeys_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private async Task<SqliteKnownHostRepository> NewRepoAsync()
    {
        var repo = new SqliteKnownHostRepository($"Data Source={_dbPath}");
        await repo.InitializeAsync();
        return repo;
    }

    private static PresentedHostKey Presented(string key, int port = 22)
        => new("Example.com", port, "ssh-ed25519", Convert.FromBase64String(key));

    [Fact]
    public async Task AcceptNew_RemembersFirstKey_ThenTrustsAndBlocksChange()
    {
        var repo = await NewRepoAsync();
        var service = new HostKeyTrustService(repo, () => HostKeyPolicy.AcceptNew);

        var first = await service.VerifyAsync(Presented(KeyA));
        Assert.True(first.Accepted);
        Assert.Equal(HostKeyVerdict.Unknown, first.Evaluation.Verdict);

        var stored = Assert.Single(await repo.GetAllAsync());
        Assert.Equal("example.com", stored.Host);
        Assert.Equal(KnownHostSource.FirstUse, stored.Source);

        var second = await service.VerifyAsync(Presented(KeyA));
        Assert.True(second.Accepted);
        Assert.Equal(HostKeyVerdict.Trusted, second.Evaluation.Verdict);
        Assert.NotNull((await repo.GetAllAsync()).Single().LastSeenAt);

        var changed = await service.VerifyAsync(Presented(KeyB));
        Assert.False(changed.Accepted);
        Assert.Equal(HostKeyVerdict.Changed, changed.Evaluation.Verdict);
        Assert.Single(await repo.GetAllAsync());
    }

    [Fact]
    public async Task Strict_RejectsUnknownWithoutRecording()
    {
        var repo = await NewRepoAsync();
        var service = new HostKeyTrustService(repo, () => HostKeyPolicy.Strict);

        var outcome = await service.VerifyAsync(Presented(KeyA));

        Assert.False(outcome.Accepted);
        Assert.False(outcome.RequiresConfirmation);
        Assert.Empty(await repo.GetAllAsync());
    }

    [Fact]
    public async Task Ask_WithoutPromptUi_FallsBackToTofuButStillBlocksChange()
    {
        var repo = await NewRepoAsync();
        var service = new HostKeyTrustService(repo, () => HostKeyPolicy.Ask);

        Assert.True((await service.VerifyAsync(Presented(KeyA))).Accepted);
        var changed = await service.VerifyAsync(Presented(KeyB));

        Assert.False(changed.Accepted);
        Assert.False(changed.RequiresConfirmation);
    }

    [Fact]
    public async Task Ask_WithPrompt_VerifyNeverPrompts_ConfirmAcceptOnceIsNotPersisted()
    {
        var repo = await NewRepoAsync();
        var prompts = 0;
        var service = new HostKeyTrustService(
            repo,
            () => HostKeyPolicy.Ask,
            (_, _) =>
            {
                prompts++;
                return Task.FromResult(HostKeyDecision.AcceptOnce);
            });

        // 握手回调内：只裁决不弹窗
        var outcome = await service.VerifyAsync(Presented(KeyA));
        Assert.True(outcome.RequiresConfirmation);
        Assert.False(outcome.Accepted);
        Assert.Equal(0, prompts);

        // 握手外确认 → 本进程内放行、不落库
        Assert.True(await service.ConfirmAsync(outcome.Evaluation));
        Assert.Equal(1, prompts);
        Assert.Empty(await repo.GetAllAsync());
        Assert.True((await service.VerifyAsync(Presented(KeyA))).Accepted);
    }

    [Fact]
    public async Task Ask_ConfirmReplacingChangedKey_RemovesOldExactEntry()
    {
        var repo = await NewRepoAsync();
        var service = new HostKeyTrustService(
            repo,
            () => HostKeyPolicy.Ask,
            (_, _) => Task.FromResult(HostKeyDecision.AcceptAndRemember));

        var unknown = await service.VerifyAsync(Presented(KeyA));
        await service.ConfirmAsync(unknown.Evaluation);

        var changed = await service.VerifyAsync(Presented(KeyB));
        Assert.Equal(HostKeyVerdict.Changed, changed.Evaluation.Verdict);
        Assert.True(await service.ConfirmAsync(changed.Evaluation));

        var entry = Assert.Single(await repo.GetAllAsync());
        Assert.Equal(KeyB, entry.PublicKeyBase64);
        Assert.Equal(KnownHostSource.UserConfirmed, entry.Source);
    }

    [Fact]
    public async Task ImportedHashedEntry_TrustsMatchingHost_AndRevokedIsNeverConfirmable()
    {
        var repo = await NewRepoAsync();
        var parsed = OpenSshKnownHostsParser.Parse($"""
            {HashedExampleCom} ssh-ed25519 {KeyA}
            @revoked example.com ssh-ed25519 {KeyB}
            """);
        foreach (var entry in parsed.Entries)
        {
            await repo.SaveAsync(entry);
        }

        var service = new HostKeyTrustService(
            repo,
            () => HostKeyPolicy.Ask,
            (_, _) => Task.FromResult(HostKeyDecision.AcceptAndRemember));

        Assert.Equal(HostKeyVerdict.Trusted, (await service.VerifyAsync(Presented(KeyA))).Evaluation.Verdict);

        var revoked = await service.VerifyAsync(Presented(KeyB));
        Assert.Equal(HostKeyVerdict.Revoked, revoked.Evaluation.Verdict);
        Assert.False(revoked.RequiresConfirmation);
        Assert.False(await service.ConfirmAsync(revoked.Evaluation));
    }

    [Fact]
    public async Task Repository_SaveIsIdempotentPerHostPortKey()
    {
        var repo = await NewRepoAsync();
        var entry = new KnownHostEntry
        {
            Host = "Example.com",
            Port = 22,
            KeyType = "ssh-ed25519",
            PublicKeyBase64 = KeyA,
            FingerprintSha256 = "x"
        };

        await repo.SaveAsync(entry);
        await repo.SaveAsync(new KnownHostEntry
        {
            Host = "example.com",
            Port = 22,
            KeyType = "ssh-ed25519",
            PublicKeyBase64 = KeyA,
            FingerprintSha256 = "x",
            Status = KnownHostStatus.Revoked
        });

        var stored = Assert.Single(await repo.GetAllAsync());
        Assert.Equal(entry.Id, stored.Id);
        Assert.Equal(KnownHostStatus.Revoked, stored.Status);
        Assert.Single(await repo.GetCandidatesAsync("EXAMPLE.COM", 22, CancellationToken.None));
        Assert.Empty(await repo.GetCandidatesAsync("example.com", 2222, CancellationToken.None));
    }

    [Fact]
    public async Task KnownKeyTypes_IncludeMatchingPatternsButNotRevoked()
    {
        var repo = await NewRepoAsync();
        foreach (var entry in OpenSshKnownHostsParser.Parse($"{HashedExampleCom} ssh-ed25519 {KeyA}").Entries)
        {
            await repo.SaveAsync(entry);
        }

        // 被吊销的密钥不能影响协商偏好
        await repo.SaveAsync(new KnownHostEntry
        {
            Host = "example.com",
            Port = 22,
            KeyType = "ecdsa-sha2-nistp256",
            PublicKeyBase64 = "revoked-ecdsa",
            FingerprintSha256 = "x",
            Status = KnownHostStatus.Revoked
        });

        var service = new HostKeyTrustService(repo, () => HostKeyPolicy.Ask);

        Assert.Equal(["ssh-ed25519"], await service.GetKnownKeyTypesAsync("example.com", 22));
        Assert.Empty(await service.GetKnownKeyTypesAsync("example.com", 2222));
    }
}
