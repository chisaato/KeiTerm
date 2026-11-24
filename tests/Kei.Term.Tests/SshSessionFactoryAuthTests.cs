using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Ssh.Abstractions;
using Kei.Term.Ssh.Services;
using Renci.SshNet;
using Xunit;

using Avalonia.Controls;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
namespace Kei.Term.Tests;

// SshSessionFactory 认证方法注册规则测试：
// 纯交互式认证（零密码材料 + interactivePrompt）也必须注册 KeyboardInteractive，
// 否则任务的「交互式」选择永远不会触发服务器 KI 质询。
public class SshSessionFactoryAuthTests
{
    private static ResolvedSessionConfig Config() => new(
        Guid.NewGuid(),
        "test",
        "127.0.0.1",
        22,
        "user",
        null,
        "xterm-256color",
        null,
        null,
        new Dictionary<string, string>());

    // SshNetSession 连接前即已构建的认证方法列表
    private static IReadOnlyList<AuthenticationMethod> GetAuthMethods(SshNetSession session)
        => session.AuthenticationMethods;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitInteractive_PreservesPriorityWithoutPromptingDuringPreparation(bool interactiveFirst)
    {
        var key = new MaterializedAuthMethod(AuthMaterialKind.PrivateKey,
            new Kei.Term.Core.Vault.SecretPayload { PrivateKeyContent = PrivateKeyFormatTests.PlainOpenSsh });
        var interactive = new MaterializedAuthMethod(AuthMaterialKind.Interactive, null);
        var password = new MaterializedAuthMethod(AuthMaterialKind.Password,
            new Kei.Term.Core.Vault.SecretPayload { Password = "pw" });
        var materials = interactiveFirst ? new[] { interactive, key, password } : new[] { key, password, interactive };
        await using var session = (SshNetSession)await new SshSessionFactory().CreateSessionAsync(Config(), materials,
            new SshConnectOptions { InteractivePrompt = _ => throw new InvalidOperationException("Premature prompt") });
        var actual = GetAuthMethods(session);
        Assert.Equal(3, actual.Count);
        Assert.IsType<KeyboardInteractiveAuthenticationMethod>(actual[interactiveFirst ? 0 : 2]);
        Assert.IsType<PrivateKeyAuthenticationMethod>(actual[interactiveFirst ? 1 : 0]);
        Assert.IsType<PasswordAuthenticationMethod>(actual[interactiveFirst ? 2 : 1]);
    }

    [Fact]
    public async Task CreateSession_ZeroMaterialsWithInteractivePrompt_RegistersKeyboardInteractive()
    {
        var factory = new SshSessionFactory();

        var session = (SshNetSession)await factory.CreateSessionAsync(
            Config(),
            Array.Empty<MaterializedAuthMethod>(),
            new SshConnectOptions { InteractivePrompt = _ => Task.FromResult<string?>(null) });

        try
        {
            var methods = GetAuthMethods(session);
            Assert.Contains(methods, m => m is KeyboardInteractiveAuthenticationMethod);
            // 无密码材料时不应注册 PasswordAuthenticationMethod
            Assert.DoesNotContain(methods, m => m is PasswordAuthenticationMethod);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateSession_KeyOnlyWithInteractiveCallback_RegistersOnlyConfiguredKey()
    {
        var factory = new SshSessionFactory();
        var material = new MaterializedAuthMethod(AuthMaterialKind.PrivateKey,
            new Kei.Term.Core.Vault.SecretPayload { PrivateKeyContent = PrivateKeyFormatTests.PlainOpenSsh });
        var session = (SshNetSession)await factory.CreateSessionAsync(Config(), [material],
            new SshConnectOptions { InteractivePrompt = _ => Task.FromResult<string?>("unconfigured password") });

        try
        {
            Assert.IsType<PrivateKeyAuthenticationMethod>(Assert.Single(GetAuthMethods(session)));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreateSession_UnavailableAgentWithInteractiveCallback_DoesNotFallBackToInteractive()
    {
        var factory = new SshSessionFactory();
        var material = new MaterializedAuthMethod(AuthMaterialKind.Agent, null);

        await Assert.ThrowsAsync<ArgumentException>(() => factory.CreateSessionAsync(Config(), [material],
            new SshConnectOptions
            {
                AgentSocketPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                InteractivePrompt = _ => Task.FromResult<string?>("unconfigured password")
            }));
    }

    [Fact]
    public async Task CreateSession_PasswordMaterial_RegistersBothPasswordAndKeyboardInteractive()
    {
        var factory = new SshSessionFactory();
        var materials = new[]
        {
            new MaterializedAuthMethod(AuthMaterialKind.Password, new Kei.Term.Core.Vault.SecretPayload { Password = "pw" })
        };

        var session = (SshNetSession)await factory.CreateSessionAsync(Config(), materials);

        try
        {
            var methods = GetAuthMethods(session);
            Assert.Contains(methods, m => m is PasswordAuthenticationMethod);
            Assert.Contains(methods, m => m is KeyboardInteractiveAuthenticationMethod);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }
}

public class AuthPromptDefaultTests
{
    [Fact]
    public Task PreviousInteractiveSelection_DoesNotOverrideNextPasswordPrompt() => HeadlessAvalonia.RunAsync(() =>
    {
        var previous = new AuthPromptWindow("ops", [], AuthPromptMethod.Interactive);
        Assert.Equal(2, previous.FindControl<ComboBox>("MethodCombo")!.SelectedIndex);
        previous.Close();
        var next = new AuthPromptWindow("ops", [], null);
        Assert.Equal(0, next.FindControl<ComboBox>("MethodCombo")!.SelectedIndex);
        Assert.True(next.FindControl<Grid>("PasswordPanel")!.IsVisible);
        next.Close();
        return Task.CompletedTask;
    });
}
