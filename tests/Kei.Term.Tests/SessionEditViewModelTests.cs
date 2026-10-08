using System;
using System.Linq;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class SessionEditViewModelTests
{
    [Fact]
    public void SessionEditViewModel_AppliesTimeoutOverrideToModel()
    {
        var settings = new AppSettings { ConnectTimeoutSeconds = 60 };
        var existing = new SessionNode
        {
            Name = "Server",
            Host = "1.2.3.4",
            Overrides = new SessionOverrides { ConnectTimeoutSeconds = 90 }
        };

        var vm = new SessionEditViewModel(existing, null, [], settings);

        Assert.Equal(90, vm.SelectedConnectTimeout?.Value);

        // 修改为 120s
        vm.SelectedConnectTimeout = vm.ConnectTimeoutOptions.First(o => o.Value == 120);

        var updated = vm.ApplyToModel(existing);
        Assert.Equal(120, updated.Overrides.ConnectTimeoutSeconds);

        // 修改为继承全局
        vm.SelectedConnectTimeout = vm.ConnectTimeoutOptions.First(o => o.Value == null);
        updated = vm.ApplyToModel(existing);
        Assert.Null(updated.Overrides.ConnectTimeoutSeconds);
    }

    [Fact]
    public void NewSession_FirewallDefaultsToNone_AndDoesNotListSessions()
    {
        var bastion = new SessionNode { Name = "bastion", Host = "10.0.0.1", Username = "root", Port = 22 };
        var vm = new SessionEditViewModel(null, null, [], jumpCandidates: [bastion]);

        Assert.Equal("无", vm.SelectedFirewall?.DisplayName);
        Assert.Null(vm.SelectedFirewall?.Id);
        Assert.DoesNotContain(vm.FirewallOptions, option => option.Id == bastion.Id);
        Assert.Null(vm.ApplyToModel().JumpHostSessionId);
        Assert.Null(vm.ApplyToModel().ProxyProfileId);
    }

    [Fact]
    public void ExcludedButPresentCurrentJump_IsKeptOnOpen_WithoutListingOtherSessions()
    {
        var self = new SessionNode { Name = "self", Host = "self.example" };
        var excluded = new SessionNode
        {
            Name = "loopback",
            Host = "loop.example",
            Username = "admin",
            Port = 2200,
            JumpHostSessionId = self.Id
        };
        self.JumpHostSessionId = excluded.Id;
        var other = new SessionNode { Name = "other", Host = "other.example" };

        var vm = new SessionEditViewModel(self, null, [], jumpCandidates: [self, excluded, other]);

        Assert.Equal(excluded.Id, vm.SelectedFirewall?.Id);
        Assert.Equal("loopback", vm.SelectedFirewall?.DisplayName);
        Assert.Equal(FirewallChoiceKind.ChosenSession, vm.SelectedFirewall?.Kind);
        Assert.DoesNotContain(vm.FirewallOptions, option => option.Id == other.Id);
        Assert.DoesNotContain(vm.FirewallOptions, option => option.Id == self.Id);
        Assert.Equal(excluded.Id, vm.ApplyToModel(self).JumpHostSessionId);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xterm-256color")]
    public void TerminalType_EditingPreservesInheritanceAndExplicitTypes(string? existingType)
    {
        AppSettings settings = new() { DefaultTerminalType = "screen-256color" };
        SessionNode? existing = existingType == null ? null : new SessionNode { Host = "server", TerminalType = existingType };
        SessionEditViewModel model = new(existing, null, [], settings);
        SessionNode saved = model.ApplyToModel(existing);

        Assert.Equal(existingType ?? string.Empty, saved.TerminalType);
        Assert.Equal(string.IsNullOrEmpty(existingType) ? "screen-256color" : existingType,
            Kei.Term.Core.Services.SessionConfigBuilder.Build(saved, settings).TerminalType);

        // 清空一个显式值后，保存仍保留空白，供连接时解析当前全局值。
        model.TerminalType = "  ";
        saved = model.ApplyToModel(saved);
        Assert.Empty(saved.TerminalType);
        settings.DefaultTerminalType = "vt100";
        Assert.Equal("vt100", Kei.Term.Core.Services.SessionConfigBuilder.Build(saved, settings).TerminalType);
    }
}
