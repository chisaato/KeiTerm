using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Ssh.Abstractions;
using Kei.Term.Ssh.Services;
using Renci.SshNet;
using Xunit;

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
    public async Task CreateSession_ZeroMaterialsWithoutPrompt_ThrowsNoAuthMethod()
    {
        var factory = new SshSessionFactory();

        // 零材料且无交互回调时不会注册任何认证方法，SSH.NET 直接拒绝构造
        await Assert.ThrowsAsync<ArgumentException>(() =>
            factory.CreateSessionAsync(Config(), Array.Empty<MaterializedAuthMethod>()));
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
