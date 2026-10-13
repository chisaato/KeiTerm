using System;
using System.Collections.Generic;
using System.Linq;
using Kei.Term.Core.Vault;
using Xunit;

namespace Kei.Term.Tests;

// 旧身份级整包（Dictionary<methodId, SecretPayload>）→ 材料库的纯规划：
// 拆分、命名、相同私钥合并、文件私钥口令不参与合并、未匹配键仍建条目
public class LegacySecretMigrationTests
{
    [Fact]
    public void Plan_SplitsMethodsIntoEntries_AndBackfillsSecretId()
    {
        var idA = Guid.NewGuid();
        var m1 = new VaultPrivateKeyMethod();
        var m2 = new VaultPasswordMethod();
        var ident = new Identity { Id = idA, Name = "Prod", Methods = [m1, m2] };
        var secrets = new Dictionary<Guid, IReadOnlyDictionary<string, SecretPayload>>
        {
            [idA] = new Dictionary<string, SecretPayload>
            {
                [m1.Id.ToString()] = new() { PrivateKeyContent = "KEY", Passphrase = "pp" },
                [m2.Id.ToString()] = new() { Password = "pw" }
            }
        };

        SecretMigrationPlan plan = LegacySecretMigration.Plan([ident], secrets);

        Assert.Equal(2, plan.Secrets.Count);
        MigratedSecret keyEntry = plan.Secrets.Single(s => s.Id == m1.Id);
        Assert.Equal(SecretKind.SshPrivateKey, keyEntry.Kind);
        Assert.Equal("Prod · 私钥", keyEntry.Name);
        Assert.Equal("KEY", keyEntry.Payload.PrivateKeyContent);

        MigratedSecret passwordEntry = plan.Secrets.Single(s => s.Id == m2.Id);
        Assert.Equal(SecretKind.Password, passwordEntry.Kind);
        Assert.Equal("Prod · 密码", passwordEntry.Name);

        Assert.Contains(plan.Bindings, b => b.MethodId == m1.Id && b.SecretId == m1.Id);
        Assert.Contains(plan.Bindings, b => b.MethodId == m2.Id && b.SecretId == m2.Id);
    }

    [Fact]
    public void Plan_MergesIdenticalPrivateKeys_AndRepointsSecondMethod()
    {
        // 两身份两个 VaultPrivateKeyMethod，私钥内容与口令完全相同
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var m1 = new VaultPrivateKeyMethod();
        var m2 = new VaultPrivateKeyMethod();
        var identA = new Identity { Id = idA, Name = "Prod", Methods = [m1] };
        var identB = new Identity { Id = idB, Name = "Stage", Methods = [m2] };
        var secrets = new Dictionary<Guid, IReadOnlyDictionary<string, SecretPayload>>
        {
            [idA] = new Dictionary<string, SecretPayload> { [m1.Id.ToString()] = new() { PrivateKeyContent = "SAME", Passphrase = "pp" } },
            [idB] = new Dictionary<string, SecretPayload> { [m2.Id.ToString()] = new() { PrivateKeyContent = "SAME", Passphrase = "pp" } }
        };

        SecretMigrationPlan plan = LegacySecretMigration.Plan([identA, identB], secrets);

        // 只剩 1 个 ssh-private-key 条目，保留首个方法的 Id 与首个身份命名
        MigratedSecret only = Assert.Single(plan.Secrets);
        Assert.Equal(SecretKind.SshPrivateKey, only.Kind);
        Assert.Equal(m1.Id, only.Id);
        Assert.Equal("Prod · 私钥", only.Name);

        // 第二个方法的 SecretId 指向保留条目 Id（= 第一个方法的 Id）
        Assert.Contains(plan.Bindings, b => b.MethodId == m1.Id && b.SecretId == m1.Id);
        Assert.Contains(plan.Bindings, b => b.MethodId == m2.Id && b.SecretId == m1.Id);
    }

    [Fact]
    public void Plan_SameKey_DifferentPassphrase_IsNotMerged()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var m1 = new VaultPrivateKeyMethod();
        var m2 = new VaultPrivateKeyMethod();
        var identA = new Identity { Id = idA, Name = "A", Methods = [m1] };
        var identB = new Identity { Id = idB, Name = "B", Methods = [m2] };
        var secrets = new Dictionary<Guid, IReadOnlyDictionary<string, SecretPayload>>
        {
            [idA] = new Dictionary<string, SecretPayload> { [m1.Id.ToString()] = new() { PrivateKeyContent = "SAME", Passphrase = "p1" } },
            [idB] = new Dictionary<string, SecretPayload> { [m2.Id.ToString()] = new() { PrivateKeyContent = "SAME", Passphrase = "p2" } }
        };

        SecretMigrationPlan plan = LegacySecretMigration.Plan([identA, identB], secrets);

        Assert.Equal(2, plan.Secrets.Count);
        Assert.DoesNotContain(plan.Bindings, b => b.MethodId == m2.Id && b.SecretId == m1.Id);
    }

    [Fact]
    public void Plan_KeepsFileKeyPassphraseEntriesUnmerged_ByIdMethodId()
    {
        // FilePrivateKeyMethod Persistent 的条目 Id == 方法 Id，相同口令也不合并，且不产生 binding
        var idA = Guid.NewGuid();
        var f1 = new FilePrivateKeyMethod { PassphraseMode = PassphrasePersistence.Persistent };
        var f2 = new FilePrivateKeyMethod { PassphraseMode = PassphrasePersistence.Persistent };
        var ident = new Identity { Id = idA, Name = "Prod", Methods = [f1, f2] };
        var secrets = new Dictionary<Guid, IReadOnlyDictionary<string, SecretPayload>>
        {
            [idA] = new Dictionary<string, SecretPayload>
            {
                [f1.Id.ToString()] = new() { Passphrase = "same" },
                [f2.Id.ToString()] = new() { Passphrase = "same" }
            }
        };

        SecretMigrationPlan plan = LegacySecretMigration.Plan([ident], secrets);

        Assert.Equal(2, plan.Secrets.Count);
        Assert.All(plan.Secrets, s => Assert.Equal(SecretKind.Passphrase, s.Kind));
        Assert.Contains(plan.Secrets, s => s.Id == f1.Id);
        Assert.Contains(plan.Secrets, s => s.Id == f2.Id);
        Assert.Empty(plan.Bindings);
    }

    [Fact]
    public void Plan_UnmatchedBundleKey_StillCreatesEntryWithoutBinding()
    {
        var idA = Guid.NewGuid();
        var strayId = Guid.NewGuid();
        var ident = new Identity { Id = idA, Name = "Prod", Methods = [] };
        var secrets = new Dictionary<Guid, IReadOnlyDictionary<string, SecretPayload>>
        {
            [idA] = new Dictionary<string, SecretPayload> { [strayId.ToString()] = new() { Password = "orphan" } }
        };

        SecretMigrationPlan plan = LegacySecretMigration.Plan([ident], secrets);

        MigratedSecret entry = Assert.Single(plan.Secrets);
        Assert.Equal(strayId, entry.Id);
        Assert.Equal("Prod · 密码", entry.Name);
        Assert.Empty(plan.Bindings);
    }

    [Fact]
    public void Plan_NormalizesNullAndEmptyPassphrase_AsSameMergeKey()
    {
        // 同内容私钥，口令一处为 null、一处为 ""：合并键规范化为 "" 后应视为同一把
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var m1 = new VaultPrivateKeyMethod();
        var m2 = new VaultPrivateKeyMethod();
        var identA = new Identity { Id = idA, Name = "A", Methods = [m1] };
        var identB = new Identity { Id = idB, Name = "B", Methods = [m2] };
        var secrets = new Dictionary<Guid, IReadOnlyDictionary<string, SecretPayload>>
        {
            [idA] = new Dictionary<string, SecretPayload> { [m1.Id.ToString()] = new() { PrivateKeyContent = "SAME", Passphrase = null } },
            [idB] = new Dictionary<string, SecretPayload> { [m2.Id.ToString()] = new() { PrivateKeyContent = "SAME", Passphrase = "" } }
        };

        SecretMigrationPlan plan = LegacySecretMigration.Plan([identA, identB], secrets);

        MigratedSecret only = Assert.Single(plan.Secrets);
        Assert.Equal(m1.Id, only.Id);
        Assert.Contains(plan.Bindings, b => b.MethodId == m2.Id && b.SecretId == m1.Id);
    }
}
