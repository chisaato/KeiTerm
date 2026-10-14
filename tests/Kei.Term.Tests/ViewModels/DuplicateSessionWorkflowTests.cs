using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Security;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;
using Xunit;

namespace Kei.Term.Tests.ViewModels;

public class DuplicateSessionWorkflowTests
{
    public sealed class TestInteraction : IInteractionService
    {
        public DuplicateSessionResult? NextResult { get; set; }
        public bool PromptCalled { get; private set; }

        public Task<DuplicateSessionResult?> PromptDuplicateSessionAsync(SessionNode source, IReadOnlyList<TreeNodeBase> siblings)
        {
            PromptCalled = true;
            return Task.FromResult(NextResult);
        }

        public Task<AuthPromptResult?> PromptAuthAsync(string prefillUsername, IReadOnlyList<VaultKeyOption> vaultKeys, AuthPromptMethod? defaultMethod) => NullInteractionService.Instance.PromptAuthAsync(prefillUsername, vaultKeys, defaultMethod);
        public Task<string?> PromptKeyboardInteractiveAsync(string prompt) => NullInteractionService.Instance.PromptKeyboardInteractiveAsync(prompt);
        public Task<string?> PromptMasterPasswordAsync(string? error) => NullInteractionService.Instance.PromptMasterPasswordAsync(error);
        public Task<PassphrasePromptResult?> PromptPassphraseAsync(FilePrivateKeyMethod method) => NullInteractionService.Instance.PromptPassphraseAsync(method);
        public Task<HostKeyDecision> PromptHostKeyAsync(HostKeyEvaluation evaluation) => NullInteractionService.Instance.PromptHostKeyAsync(evaluation);
        public Task<SessionNode?> EditSessionAsync(SessionNode? existing, Guid? parentId, IReadOnlyList<Identity> identities) => NullInteractionService.Instance.EditSessionAsync(existing, parentId, identities);
        public Task<FolderNode?> EditFolderAsync(FolderNode? existing, Guid? parentId) => NullInteractionService.Instance.EditFolderAsync(existing, parentId);
        public Task<TerminalProfile?> EditTerminalProfileAsync(TerminalProfile? source, TerminalFontSnapshot font) => NullInteractionService.Instance.EditTerminalProfileAsync(source, font);
        public Task<string?> PickSecureCrtFolderAsync() => NullInteractionService.Instance.PickSecureCrtFolderAsync();
        public Task<string?> PickKonsoleSchemeFileAsync() => NullInteractionService.Instance.PickKonsoleSchemeFileAsync();
        public Task OpenIdentityManagerAsync() => NullInteractionService.Instance.OpenIdentityManagerAsync();
        public Task OpenKnownHostsAsync() => NullInteractionService.Instance.OpenKnownHostsAsync();
        public Task OpenSettingsAsync() => NullInteractionService.Instance.OpenSettingsAsync();
        public Task OpenQuickConnectAsync() => NullInteractionService.Instance.OpenQuickConnectAsync();
        public Task<bool> ConfirmDeleteAsync(string name) => NullInteractionService.Instance.ConfirmDeleteAsync(name);
        public Task NotifyAsync(string title, string message) => NullInteractionService.Instance.NotifyAsync(title, message);
        public Task<string?> PromptTextAsync(string title, string label, string? initialText) => NullInteractionService.Instance.PromptTextAsync(title, label, initialText);
        public Task OpenBatchEditAsync(Kei.Term.App.ViewModels.BatchEdit.BatchSessionEditViewModel viewModel) => NullInteractionService.Instance.OpenBatchEditAsync(viewModel);
    }

    [Fact]
    public async Task DuplicateSession_WhenConfirmed_CopiesAllConfigurationsAndAppliesOverrides()
    {
        string dir = Path.Combine(Path.GetTempPath(), "keiterm_dup_wf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = new SqliteConnectionFactory($"Data Source={Path.Combine(dir, "test.db")}");
            await SchemaMigrator.MigrateAsync(db);
            var tree = new SqliteTreeRepository(db);
            var identities = new SqliteIdentityRepository(db);
            var forwards = new SqlitePortForwardRepository(db);
            var vault = VaultTestDb.CreateVault(db);

            var source = new SessionNode
            {
                Name = "router-gdgz",
                Host = "192.168.5.1",
                Port = 22,
                Username = "admin",
                Protocol = "ssh",
                StartupScript = "uptime"
            };
            await tree.SaveNodeAsync(source);

            var interaction = new TestInteraction
            {
                NextResult = new DuplicateSessionResult("router-gdgz (custom)", "192.168.5.2", 2222)
            };

            var model = new MainViewModel(tree, identities, vault, vault, new FixedSettingsService(), new SshSessionFactory(),
                uiDispatch: a => a(), portForwards: forwards);
            model.Interaction = interaction;

            await model.ReloadTreeAsync();
            model.SelectedTreeNode = source;

            await model.DuplicateSelectedSessionCommand.ExecuteAsync(null);

            Assert.True(interaction.PromptCalled);
            Assert.NotNull(model.SelectedTreeNode);
            var copy = Assert.IsType<SessionNode>(model.SelectedTreeNode);
            Assert.NotEqual(source.Id, copy.Id);
            Assert.Equal("router-gdgz (custom)", copy.Name);
            Assert.Equal("192.168.5.2", copy.Host);
            Assert.Equal(2222, copy.Port);
            Assert.Equal("admin", copy.Username);
            Assert.Equal("uptime", copy.StartupScript);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateSession_WhenCancelled_DoesNotCreateNode()
    {
        string dir = Path.Combine(Path.GetTempPath(), "keiterm_dup_cancel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = new SqliteConnectionFactory($"Data Source={Path.Combine(dir, "test.db")}");
            await SchemaMigrator.MigrateAsync(db);
            var tree = new SqliteTreeRepository(db);
            var identities = new SqliteIdentityRepository(db);
            var forwards = new SqlitePortForwardRepository(db);
            var vault = VaultTestDb.CreateVault(db);

            var source = new SessionNode
            {
                Name = "debian-build",
                Host = "10.0.0.1",
                Port = 22
            };
            await tree.SaveNodeAsync(source);

            var interaction = new TestInteraction
            {
                NextResult = null // 用户取消
            };

            var model = new MainViewModel(tree, identities, vault, vault, new FixedSettingsService(), new SshSessionFactory(),
                uiDispatch: a => a(), portForwards: forwards);
            model.Interaction = interaction;

            await model.ReloadTreeAsync();
            model.SelectedTreeNode = source;

            await model.DuplicateSelectedSessionCommand.ExecuteAsync(null);

            Assert.True(interaction.PromptCalled);
            Assert.Single(await tree.GetAllNodesAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
