using System;
using System.Linq;
using Kei.Term.Core.Vault;

namespace Kei.Term.Tests;

// 引用统计：Vault 方法按 SecretId；文件私钥持久化口令条目 Id = 方法 Id；其余不计
public class SecretReferencesTests
{
    [Fact]
    public void Find_CountsVaultMethodsBySecretId_AndFileKeyByMethodId()
    {
        Guid keyEntry = Guid.NewGuid();
        Guid pwEntry = Guid.NewGuid();
        Guid keyMethodId = Guid.NewGuid();
        var a = new Identity
        {
            Id = Guid.NewGuid(),
            Name = "A",
            Methods =
            [
                new VaultPrivateKeyMethod { Id = Guid.NewGuid(), SecretId = keyEntry },
                new VaultPasswordMethod { Id = Guid.NewGuid(), SecretId = pwEntry },
                new FilePrivateKeyMethod { Id = keyMethodId, PassphraseMode = PassphrasePersistence.Persistent },
                new FilePrivateKeyMethod { Id = Guid.NewGuid(), PassphraseMode = PassphrasePersistence.AlwaysAsk },
            ]
        };
        Assert.Equal(new[] { "A" }, SecretReferences.Find([a], keyEntry).Select(r => r.IdentityName));
        Assert.Single(SecretReferences.Find([a], pwEntry));
        Assert.Single(SecretReferences.Find([a], keyMethodId)); // 文件私钥持久化口令条目 Id = 方法 Id
        Assert.Empty(SecretReferences.Find([a], Guid.NewGuid()));
    }

    [Fact]
    public void SecretIdOf_VaultMethodsUseSecretId_FileKeyOnlyWhenPersistent()
    {
        Guid secretId = Guid.NewGuid();
        Guid fileKeyId = Guid.NewGuid();

        // 已绑定与未绑定的 Vault 方法都要覆盖：null = 尚未绑定材料
        Assert.Equal(secretId, SecretReferences.SecretIdOf(new VaultPasswordMethod { SecretId = secretId }));
        Assert.Equal(secretId, SecretReferences.SecretIdOf(new VaultPrivateKeyMethod { SecretId = secretId }));
        Assert.Null(SecretReferences.SecretIdOf(new VaultPasswordMethod()));
        Assert.Null(SecretReferences.SecretIdOf(new VaultPrivateKeyMethod()));

        // 文件私钥仅在持久化口令模式下按方法 Id 计
        Assert.Equal(fileKeyId, SecretReferences.SecretIdOf(
            new FilePrivateKeyMethod { Id = fileKeyId, PassphraseMode = PassphrasePersistence.Persistent }));
        Assert.Null(SecretReferences.SecretIdOf(
            new FilePrivateKeyMethod { Id = Guid.NewGuid(), PassphraseMode = PassphrasePersistence.AlwaysAsk }));
        Assert.Null(SecretReferences.SecretIdOf(
            new FilePrivateKeyMethod { Id = Guid.NewGuid(), PassphraseMode = PassphrasePersistence.SessionOnly }));

        // 无材料的方法一律不计
        Assert.Null(SecretReferences.SecretIdOf(new AgentMethod()));
        Assert.Null(SecretReferences.SecretIdOf(new InteractiveMethod()));
    }

    [Fact]
    public void Find_EmptySecretId_ReturnsEmpty()
    {
        var a = new Identity
        {
            Id = Guid.NewGuid(),
            Name = "A",
            Methods = [new VaultPasswordMethod { SecretId = Guid.Empty }]
        };

        Assert.Empty(SecretReferences.Find([a], Guid.Empty));
    }
}
