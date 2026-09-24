using System;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;
using Xunit;

namespace Kei.Term.Tests;

// 方法物化：文件缺失、口令三态、取消跳过、Vault 锁定跳过、Agent/Interactive
public class AuthMaterializerTests
{
    private static AuthMaterializerContext Context(
        Func<string, CancellationToken, Task<FileKeyReadResult?>>? read = null,
        Func<Guid, string?>? session = null,
        Func<Guid, SecretPayload?>? vault = null,
        Func<FilePrivateKeyMethod, CancellationToken, Task<PassphrasePromptResult?>>? prompt = null,
        Func<Guid, SecretPayload, Task>? save = null,
        Func<InteractiveMethod, string, CancellationToken, Task<SecretPayload?>>? interactive = null)
        => new()
        {
            ReadPrivateKeyFileAsync = read ?? ((_, _) => Task.FromResult<FileKeyReadResult?>(null)),
            GetSessionPassphrase = session ?? (_ => null),
            GetVaultSecret = vault ?? (_ => null),
            PromptPassphraseAsync = prompt ?? ((_, _) => Task.FromResult<PassphrasePromptResult?>(null)),
            SaveVaultSecretAsync = save,
            PromptInteractiveAsync = interactive
        };

    private static Func<FilePrivateKeyMethod, CancellationToken, Task<PassphrasePromptResult?>> ThrowingPrompt()
        => (_, _) => throw new InvalidOperationException("不应弹出口令框");

    [Fact]
    public async Task FileMissing_ReturnsNull()
    {
        var method = new FilePrivateKeyMethod { KeyFilePath = "/no/such/key" };

        var result = await AuthMaterializer.MaterializeAsync(method, "u", Context());

        Assert.Null(result);
    }

    [Fact]
    public async Task EmptyKeyFilePath_ReturnsNull()
    {
        var method = new FilePrivateKeyMethod { KeyFilePath = "  " };

        var result = await AuthMaterializer.MaterializeAsync(method, "u", Context());

        Assert.Null(result);
    }

    [Fact]
    public async Task NoPassphraseFile_NoPrompt()
    {
        var method = new FilePrivateKeyMethod { KeyFilePath = "/k", PassphraseMode = PassphrasePersistence.AlwaysAsk };

        var result = await AuthMaterializer.MaterializeAsync(
            method,
            "u",
            Context(
                read: (_, _) => Task.FromResult<FileKeyReadResult?>(new FileKeyReadResult("KEY", false)),
                prompt: ThrowingPrompt()));

        Assert.NotNull(result);
        Assert.Equal(AuthMaterialKind.PrivateKey, result!.Kind);
        Assert.Equal("KEY", result.Secret!.PrivateKeyContent);
        Assert.Null(result.Secret.Passphrase);
    }

    [Fact]
    public async Task SessionOnly_Hit_ReusesCacheWithoutPrompt()
    {
        var method = new FilePrivateKeyMethod { KeyFilePath = "/k", PassphraseMode = PassphrasePersistence.SessionOnly };

        var result = await AuthMaterializer.MaterializeAsync(
            method,
            "u",
            Context(
                read: (_, _) => Task.FromResult<FileKeyReadResult?>(new FileKeyReadResult("KEY", true)),
                session: _ => "cached-pp",
                prompt: ThrowingPrompt()));

        Assert.NotNull(result);
        Assert.Equal("cached-pp", result!.Secret!.Passphrase);
    }

    [Fact]
    public async Task AlwaysAsk_PromptsEveryTime()
    {
        var method = new FilePrivateKeyMethod { KeyFilePath = "/k", PassphraseMode = PassphrasePersistence.AlwaysAsk };
        var promptCount = 0;
        var context = Context(
            read: (_, _) => Task.FromResult<FileKeyReadResult?>(new FileKeyReadResult("KEY", true)),
            prompt: (_, _) =>
            {
                promptCount++;
                return Task.FromResult<PassphrasePromptResult?>(new PassphrasePromptResult("pp", false));
            });

        var first = await AuthMaterializer.MaterializeAsync(method, "u", context);
        var second = await AuthMaterializer.MaterializeAsync(method, "u", context);

        Assert.Equal("pp", first!.Secret!.Passphrase);
        Assert.Equal("pp", second!.Secret!.Passphrase);
        // AlwaysAsk：每次连接都弹
        Assert.Equal(2, promptCount);
    }

    [Fact]
    public async Task PromptCancelled_ReturnsNull()
    {
        var method = new FilePrivateKeyMethod { KeyFilePath = "/k", PassphraseMode = PassphrasePersistence.AlwaysAsk };

        var result = await AuthMaterializer.MaterializeAsync(
            method,
            "u",
            Context(
                read: (_, _) => Task.FromResult<FileKeyReadResult?>(new FileKeyReadResult("KEY", true)),
                prompt: (_, _) => Task.FromResult<PassphrasePromptResult?>(null)));

        Assert.Null(result);
    }

    [Fact]
    public async Task Persistent_UsesVaultPassphrase_WithoutPrompt()
    {
        var method = new FilePrivateKeyMethod { KeyFilePath = "/k", PassphraseMode = PassphrasePersistence.Persistent };

        var result = await AuthMaterializer.MaterializeAsync(
            method,
            "u",
            Context(
                read: (_, _) => Task.FromResult<FileKeyReadResult?>(new FileKeyReadResult("KEY", true)),
                vault: _ => new SecretPayload { Passphrase = "vault-pp" },
                prompt: ThrowingPrompt()));

        Assert.Equal("vault-pp", result!.Secret!.Passphrase);
    }

    [Fact]
    public async Task Persistent_Miss_PromptsAndSavesWhenRemembered()
    {
        var method = new FilePrivateKeyMethod { KeyFilePath = "/k", PassphraseMode = PassphrasePersistence.Persistent };
        Guid? savedId = null;
        SecretPayload? saved = null;

        var result = await AuthMaterializer.MaterializeAsync(
            method,
            "u",
            Context(
                read: (_, _) => Task.FromResult<FileKeyReadResult?>(new FileKeyReadResult("KEY", true)),
                prompt: (_, _) => Task.FromResult<PassphrasePromptResult?>(new PassphrasePromptResult("new-pp", true)),
                save: (id, payload) =>
                {
                    savedId = id;
                    saved = payload;
                    return Task.CompletedTask;
                }));

        Assert.Equal("new-pp", result!.Secret!.Passphrase);
        Assert.Equal(method.Id, savedId);
        Assert.Equal("new-pp", saved!.Passphrase);
    }

    [Fact]
    public async Task Persistent_Miss_DoesNotSaveWhenNotRemembered()
    {
        var method = new FilePrivateKeyMethod { KeyFilePath = "/k", PassphraseMode = PassphrasePersistence.Persistent };
        var saved = false;

        var result = await AuthMaterializer.MaterializeAsync(
            method,
            "u",
            Context(
                read: (_, _) => Task.FromResult<FileKeyReadResult?>(new FileKeyReadResult("KEY", true)),
                prompt: (_, _) => Task.FromResult<PassphrasePromptResult?>(new PassphrasePromptResult("new-pp", false)),
                save: (_, _) =>
                {
                    saved = true;
                    return Task.CompletedTask;
                }));

        Assert.Equal("new-pp", result!.Secret!.Passphrase);
        Assert.False(saved);
    }

    [Fact]
    public async Task VaultPassword_Locked_ReturnsNull()
    {
        var method = new VaultPasswordMethod();

        var result = await AuthMaterializer.MaterializeAsync(method, "u", Context(vault: _ => null));

        Assert.Null(result);
    }

    [Fact]
    public async Task VaultPassword_Unlocked_ReturnsMaterial()
    {
        var method = new VaultPasswordMethod();

        var result = await AuthMaterializer.MaterializeAsync(
            method,
            "u",
            Context(vault: _ => new SecretPayload { Password = "pw" }));

        Assert.Equal(AuthMaterialKind.Password, result!.Kind);
        Assert.Equal("pw", result.Secret!.Password);
    }

    [Fact]
    public async Task VaultPrivateKey_ReturnsMaterial()
    {
        var method = new VaultPrivateKeyMethod();

        var result = await AuthMaterializer.MaterializeAsync(
            method,
            "u",
            Context(vault: _ => new SecretPayload { PrivateKeyContent = "KEY", Passphrase = "pp" }));

        Assert.Equal(AuthMaterialKind.PrivateKey, result!.Kind);
        Assert.Equal("KEY", result.Secret!.PrivateKeyContent);
    }

    [Fact]
    public async Task Agent_ReturnsAgentMaterialWithFingerprint()
    {
        var method = new AgentMethod { AgentFingerprint = "SHA256:fp" };

        var result = await AuthMaterializer.MaterializeAsync(method, "u", Context());

        Assert.Equal(AuthMaterialKind.Agent, result!.Kind);
        Assert.Equal("SHA256:fp", result.AgentFingerprint);
        Assert.Null(result.Secret);
    }

    [Fact]
    public async Task Interactive_ReturnsPromptedPassword()
    {
        var method = new InteractiveMethod();

        var result = await AuthMaterializer.MaterializeAsync(
            method,
            "alice",
            Context(interactive: (_, username, _) =>
            {
                Assert.Equal("alice", username);
                return Task.FromResult<SecretPayload?>(new SecretPayload { Password = "typed" });
            }));

        Assert.Equal(AuthMaterialKind.Password, result!.Kind);
        Assert.Equal("typed", result.Secret!.Password);
    }

    [Fact]
    public async Task Interactive_Cancelled_ReturnsNull()
    {
        var method = new InteractiveMethod();

        var result = await AuthMaterializer.MaterializeAsync(
            method,
            "alice",
            Context(interactive: (_, _, _) => Task.FromResult<SecretPayload?>(null)));

        Assert.Null(result);
    }

    [Fact]
    public async Task Interactive_WithoutDelegate_Skips()
    {
        var method = new InteractiveMethod();

        var result = await AuthMaterializer.MaterializeAsync(method, "alice", Context());

        Assert.Null(result);
    }
}
