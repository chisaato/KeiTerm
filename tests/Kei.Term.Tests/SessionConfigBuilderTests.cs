using System;
using System.Collections.Generic;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Xunit;

namespace Kei.Term.Tests;

// 扁平三层解析：会话自身 → 全局设置 → 内建兜底
public class SessionConfigBuilderTests
{
    private static AppSettings CreateSettings() => new()
    {
        DefaultPort = 2200,
        DefaultUsername = "globaluser",
        DefaultIdentityId = Guid.NewGuid()
    };

    [Fact]
    public void Build_SessionOwnValues_TakePrecedence()
    {
        var settings = CreateSettings();
        var node = new SessionNode
        {
            Name = "Web-1",
            Host = "10.0.0.1",
            Port = 2222,
            Username = "ops",
            IdentityId = Guid.NewGuid(),
            TerminalType = "xterm",
            StartupScript = "echo hi"
        };

        var resolved = SessionConfigBuilder.Build(node, settings);

        // 会话自身值全部原样保留
        Assert.Equal(2222, resolved.Port);
        Assert.Equal("ops", resolved.Username);
        Assert.Equal(node.IdentityId, resolved.IdentityId);
        Assert.Equal("xterm", resolved.TerminalType);
        Assert.Equal("echo hi", resolved.StartupScript);
        Assert.Equal("Web-1", resolved.SessionName);
        Assert.Equal(node.Id, resolved.SessionId);
    }

    [Fact]
    public void Build_MissingValues_FallBackToSettings()
    {
        var settings = CreateSettings();
        var node = new SessionNode
        {
            Name = "Bare",
            Host = "10.0.0.2",
            TerminalType = "  " // 空白同样触发回退
        };

        var resolved = SessionConfigBuilder.Build(node, settings);

        // 未配置项回退到全局设置
        Assert.Equal(2200, resolved.Port);
        Assert.Equal("globaluser", resolved.Username);
        Assert.Equal(settings.DefaultIdentityId, resolved.IdentityId);
    }

    [Fact]
    public void Build_SettingsEmpty_FallBackToBuiltins()
    {
        var settings = new AppSettings
        {
            DefaultPort = 22,
            DefaultUsername = "  " // 空白视为未设置
        };
        var node = new SessionNode
        {
            Name = "Fallback",
            Host = "10.0.0.3",
            TerminalType = ""
        };

        var resolved = SessionConfigBuilder.Build(node, settings);

        // 用户名回退当前系统用户，端口用全局默认端口本身，终端类型用内建兜底
        Assert.Equal(Environment.UserName, resolved.Username);
        Assert.Equal(22, resolved.Port);
        Assert.Equal("xterm-256color", resolved.TerminalType);
        Assert.Null(resolved.IdentityId);
    }

    [Fact]
    public void Build_IdentityId_NodeOverridesSettings()
    {
        var settings = CreateSettings();
        var ownIdentityId = Guid.NewGuid();
        var node = new SessionNode
        {
            Name = "Cred",
            Host = "10.0.0.4",
            IdentityId = ownIdentityId
        };

        var resolved = SessionConfigBuilder.Build(node, settings);

        // 会话自身凭据优先于全局默认凭据
        Assert.Equal(ownIdentityId, resolved.IdentityId);
    }

    [Fact]
    public void Build_JumpHostEnvAndTerminalProfile_PassedThroughWithoutFallback()
    {
        var settings = CreateSettings();
        var node = new SessionNode
        {
            Name = "Jump",
            Host = "10.0.0.5",
            JumpHostSessionId = Guid.NewGuid(),
            TerminalProfileId = "Custom-Explicit"
        };
        node.EnvironmentVariables["LANG"] = "zh_CN.UTF-8";

        var resolved = SessionConfigBuilder.Build(node, settings);

        // 跳板机与环境变量原样透传，不参与回退
        Assert.Equal(node.JumpHostSessionId, resolved.JumpHostSessionId);
        Assert.Equal("zh_CN.UTF-8", resolved.EnvironmentVariables["LANG"]);
        // 显式配色 ID 同样透传，三级回退由 App 层解析
        Assert.Equal("Custom-Explicit", resolved.TerminalProfileId);
    }

    [Fact]
    public void Build_Overrides_SessionValueWins_NullInheritsSettings()
    {
        var settings = new AppSettings { TabTitleFollowsRemote = true, CwdFollowMode = CwdFollowMode.Always };
        var inheriting = new SessionNode { Name = "a", Host = "h" };
        var explicitSession = new SessionNode
        {
            Name = "b",
            Host = "h",
            Overrides = new SessionOverrides { FollowRemoteTitle = false, CwdFollow = CwdFollowMode.Off }
        };

        var inherited = SessionConfigBuilder.Build(inheriting, settings);
        var overridden = SessionConfigBuilder.Build(explicitSession, settings);

        Assert.True(inherited.FollowRemoteTitle);
        Assert.Equal(CwdFollowMode.Always, inherited.CwdFollow);
        Assert.False(overridden.FollowRemoteTitle);
        Assert.Equal(CwdFollowMode.Off, overridden.CwdFollow);
    }
}
