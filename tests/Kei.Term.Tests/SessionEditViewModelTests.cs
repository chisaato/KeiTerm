using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;
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

    [Fact]
    public void SessionEditViewModel_MapsAndAppliesTransferCapabilitiesTriState()
    {
        AppSettings settings = new();
        settings.FileTransfer.VerifyHashEnabled = true;
        settings.FileTransfer.ArchiveDirectoriesEnabled = false;

        SessionNode existing = new()
        {
            Host = "test.server",
            Overrides = new SessionOverrides
            {
                VerifyHashEnabled = false,
                ArchiveDirectoriesEnabled = true,
            }
        };

        SessionEditViewModel vm = new(existing, null, [], settings);
        Assert.False(vm.SelectedVerifyHash?.Value);
        Assert.True(vm.SelectedArchiveDirectories?.Value);

        // 改为继承全局
        vm.SelectedVerifyHash = vm.VerifyHashOptions.First(opt => opt.Value == null);
        vm.SelectedArchiveDirectories = vm.ArchiveDirectoriesOptions.First(opt => opt.Value == null);

        SessionNode saved = vm.ApplyToModel(existing);
        Assert.Null(saved.Overrides.VerifyHashEnabled);
        Assert.Null(saved.Overrides.ArchiveDirectoriesEnabled);

        // 改为显式开 / 关
        vm.SelectedVerifyHash = vm.VerifyHashOptions.First(opt => opt.Value == true);
        vm.SelectedArchiveDirectories = vm.ArchiveDirectoriesOptions.First(opt => opt.Value == false);

        saved = vm.ApplyToModel(existing);
        Assert.True(saved.Overrides.VerifyHashEnabled);
        Assert.False(saved.Overrides.ArchiveDirectoriesEnabled);
    }

    [Fact]
    public async Task SessionEditViewModel_ProbeResultDisplay_AndClearCallsRepo()
    {
        Guid sessionId = Guid.NewGuid();
        SessionNode existing = new()
        {
            Id = sessionId,
            Host = "test.server",
        };
        FakeMetadataRepo metadata = new();
        TransferProbeResult probe = new(
            Kei.Term.Core.Transfers.RemotePlatform.Linux,
            [Kei.Term.Core.Transfers.TransferHashAlgorithm.Sha256],
            [Kei.Term.Core.Transfers.TransferArchiveFormat.Gzip],
            new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc));
        await metadata.SetTransferProbeAsync(sessionId, probe);

        SessionEditViewModel vm = new(
            existing,
            null,
            [],
            new AppSettings(),
            jumpCandidates: null,
            proxies: null,
            sessionTree: null,
            portForwards: null,
            metadataRepo: metadata);

        await vm.LoadProbeResultAsync();
        Assert.True(vm.HasProbeResult);
        Assert.Contains("Linux", vm.ProbePlatformText);
        Assert.Contains("Sha256", vm.ProbeHashesText);
        Assert.Contains("Gzip", vm.ProbeArchivesText);
        Assert.True(vm.CanClearProbe);

        await vm.ClearProbeCommand.ExecuteAsync(null);
        Assert.Equal(1, metadata.ClearCalls);
        Assert.False(vm.HasProbeResult);
        Assert.Equal("尚未探测", vm.ProbeStatusText);
    }

    [Fact]
    public void SessionEditViewModel_NewSessionDisablesClearProbe()
    {
        FakeMetadataRepo metadata = new();
        SessionEditViewModel vm = new(
            null,
            null,
            [],
            new AppSettings(),
            jumpCandidates: null,
            proxies: null,
            sessionTree: null,
            portForwards: null,
            metadataRepo: metadata);

        Assert.True(vm.IsNewSession);
        Assert.False(vm.CanClearProbe);
        Assert.False(vm.HasProbeResult);
    }

    private sealed class FakeMetadataRepo : Kei.Term.Core.Storage.ISessionMetadataRepository
    {
        private readonly Dictionary<Guid, TransferProbeResult> _store = [];
        public int ClearCalls { get; private set; }

        public Task<TransferProbeResult?> GetTransferProbeAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            _store.TryGetValue(sessionId, out TransferProbeResult? res);
            return Task.FromResult(res);
        }

        public Task<bool> SetTransferProbeAsync(Guid sessionId, TransferProbeResult result, CancellationToken cancellationToken = default)
        {
            _store[sessionId] = result;
            return Task.FromResult(true);
        }

        public Task ClearTransferProbeAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            ClearCalls++;
            _store.Remove(sessionId);
            return Task.CompletedTask;
        }
    }
}
