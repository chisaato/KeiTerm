using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Ssh.Abstractions;
using Xunit;

namespace Kei.Term.Tests;

// F2 原地改名：空白不写库，非空只改 Name，虚拟根不可用。按键接线不在无头窗口里测。
public class TreeRenameTests
{
    [Fact]
    public async Task BlankName_DoesNotSave_AndKeepsOriginal()
    {
        var repo = new RecordingTree();
        var vm = Create(repo);
        var session = new SessionNode { Name = "web", Host = "web.example", Username = "ops" };
        vm.SelectedTreeNode = session;

        await vm.CommitTreeRenameAsync("   ");

        Assert.Equal("web", session.Name);
        Assert.Equal("web.example", session.Host);
        Assert.Equal("ops", session.Username);
        Assert.Empty(repo.Saved);
    }

    [Fact]
    public async Task NonEmptyName_WritesNodeName_AndSaves()
    {
        var repo = new RecordingTree();
        var vm = Create(repo);
        var folder = new FolderNode { Name = "old" };
        vm.SelectedTreeNode = folder;

        await vm.CommitTreeRenameAsync("  prod  ");

        Assert.Equal("prod", folder.Name);
        Assert.Same(folder, Assert.Single(repo.Saved));
    }

    [Fact]
    public async Task SessionRename_DoesNotTouchHostOrJump()
    {
        var repo = new RecordingTree();
        var vm = Create(repo);
        var jump = Guid.NewGuid();
        var session = new SessionNode
        {
            Name = "web",
            Host = "web.example",
            Username = "ops",
            JumpHostSessionId = jump
        };
        vm.SelectedTreeNode = session;

        await vm.CommitTreeRenameAsync("api");

        Assert.Equal("api", session.Name);
        Assert.Equal("web.example", session.Host);
        Assert.Equal("ops", session.Username);
        Assert.Equal(jump, session.JumpHostSessionId);
    }

    [Fact]
    public void VirtualRoot_RenameCommand_IsDisabled()
    {
        var vm = Create(new RecordingTree());
        vm.SelectedTreeNode = new VirtualRootNode();
        Assert.False(vm.RenameSelectedNodeCommand.CanExecute(null));

        vm.SelectedTreeNode = new SessionNode { Name = "web", Host = "h" };
        Assert.True(vm.RenameSelectedNodeCommand.CanExecute(null));

        vm.SelectedTreeNode = new FolderNode { Name = "folder" };
        Assert.True(vm.RenameSelectedNodeCommand.CanExecute(null));

        vm.SelectedTreeNode = null;
        Assert.False(vm.RenameSelectedNodeCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveFailure_RestoresPreviousName()
    {
        var repo = new RecordingTree { Fail = true };
        var vm = Create(repo);
        var session = new SessionNode { Name = "web", Host = "h" };
        vm.SelectedTreeNode = session;

        await vm.CommitTreeRenameAsync("gone");

        Assert.Equal("web", session.Name);
        Assert.Empty(repo.Saved);
    }

    private static MainViewModel Create(RecordingTree repo)
    {
        string path = Path.Combine(Path.GetTempPath(), $"keiterm_rename_{Guid.NewGuid():N}.json");
        return new MainViewModel(
            repo,
            new EmptyIdentities(),
            new PlainVault(),
            new PlainVault(),
            new JsonSettingsService(path),
            new UnusedFactory());
    }

    private sealed class RecordingTree : ITreeRepository
    {
        public List<TreeNodeBase> Saved { get; } = [];
        public bool Fail { get; set; }

        public Task<IReadOnlyList<TreeNodeBase>> GetAllNodesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TreeNodeBase>>([]);

        public Task<TreeNodeBase?> GetNodeByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<TreeNodeBase?>(null);

        public Task SaveNodeAsync(TreeNodeBase node, CancellationToken ct = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("save failed");
            }

            Saved.Add(node);
            return Task.CompletedTask;
        }

        public Task DeleteNodeAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveNodeAsync(Guid nodeId, Guid? newParentId, int sortOrder, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateFolderExpandedAsync(Guid folderId, bool isExpanded, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyIdentities : IIdentityRepository
    {
        public Task<IReadOnlyList<Identity>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Identity>>([]);
        public Task<Identity?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Identity?>(null);
        public Task SaveAsync(Identity identity, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class PlainVault : IVaultManager, IVaultSecretStore
    {
        public bool IsUnlocked => true;
        public bool IsPlainMode => true;
        public Task SetMasterPasswordAsync(string masterPassword, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryAutoUnlockAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task UnlockAsync(string masterPassword, bool rememberOnThisDevice, CancellationToken ct = default) => Task.CompletedTask;
        public void Lock() { }
        public Task<Dictionary<string, SecretPayload>> GetSecretsAsync(Guid identityId, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, SecretPayload>());
        public Task SaveSecretsAsync(Guid identityId, Dictionary<string, SecretPayload> secrets, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteSecretsAsync(Guid identityId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class UnusedFactory : ISshSessionFactory
    {
        public Task<ISshSession> CreateSessionAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IRemoteFileSystem> CreateFileSystemAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            ISshSession? activeSession = null,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
