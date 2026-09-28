using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Ssh.Abstractions;
using Xunit;

namespace Kei.Term.Tests;

public class ComposeModeTests
{
    private class DummyTreeRepository : ITreeRepository
    {
        public Task<IReadOnlyList<TreeNodeBase>> GetAllNodesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TreeNodeBase>>(Array.Empty<TreeNodeBase>());
        public Task<TreeNodeBase?> GetNodeByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<TreeNodeBase?>(null);
        public Task SaveNodeAsync(TreeNodeBase node, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteNodeAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveNodeAsync(Guid nodeId, Guid? newParentId, int sortOrder, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateFolderExpandedAsync(Guid folderId, bool isExpanded, CancellationToken ct = default) => Task.CompletedTask;
    }

    private class DummyIdentityRepository : IIdentityRepository
    {
        public Task<IReadOnlyList<Identity>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Identity>>(Array.Empty<Identity>());
        public Task<Identity?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Identity?>(null);
        public Task SaveAsync(Identity identity, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private class DummyVaultManager : IVaultManager
    {
        public bool IsUnlocked => true;
        public bool IsPlainMode => true;
        public Task SetMasterPasswordAsync(string masterPassword, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryAutoUnlockAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task UnlockAsync(string masterPassword, bool rememberOnThisDevice, CancellationToken ct = default) => Task.CompletedTask;
        public void Lock() { }
    }

    private class DummyVaultSecretStore : IVaultSecretStore
    {
        public Task<Dictionary<string, SecretPayload>> GetSecretsAsync(Guid identityId, CancellationToken ct = default) => Task.FromResult(new Dictionary<string, SecretPayload>());
        public Task SaveSecretsAsync(Guid identityId, Dictionary<string, SecretPayload> secrets, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteSecretsAsync(Guid identityId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private class DummySshSessionFactory : ISshSessionFactory
    {
        public Task<ISshSession> CreateSessionAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            TimeSpan? connectTimeout = null,
            Func<string, Task<string?>>? interactivePrompt = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<Kei.Term.Core.Abstractions.IRemoteFileSystem> CreateFileSystemAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            ISshSession? activeSession = null,
            TimeSpan? connectTimeout = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public void ComposeMode_DefaultIsSingleLine()
    {
        ComposeMode mode = ComposeMode.SingleLine;
        Assert.Equal(ComposeMode.SingleLine, mode);
        Assert.Equal(0, (int)ComposeMode.SingleLine);
        Assert.Equal(1, (int)ComposeMode.MultiLine);
    }

    [Fact]
    public void MainViewModel_ComposeMode_SwitchesCorrectly()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"keiterm_test_compose_{Path.GetRandomFileName()}.json");
        try
        {
            var settingsService = new JsonSettingsService(tempPath);
            var vm = new MainViewModel(
                new DummyTreeRepository(),
                new DummyIdentityRepository(),
                new DummyVaultManager(),
                new DummyVaultSecretStore(),
                settingsService,
                new DummySshSessionFactory());

            // 初始状态为单行模式
            Assert.Equal(ComposeMode.SingleLine, vm.ComposeMode);
            Assert.True(vm.IsSingleLineCompose);
            Assert.False(vm.IsMultiLineCompose);
            Assert.False(string.IsNullOrEmpty(vm.ComposePlaceholder));

            // 切换到多行模式
            vm.ComposeMode = ComposeMode.MultiLine;
            Assert.Equal(ComposeMode.MultiLine, vm.ComposeMode);
            Assert.False(vm.IsSingleLineCompose);
            Assert.True(vm.IsMultiLineCompose);
            Assert.Contains("Ctrl+Enter", vm.ComposePlaceholder);

            // 切换回单行模式
            vm.IsSingleLineCompose = true;
            Assert.Equal(ComposeMode.SingleLine, vm.ComposeMode);
            Assert.True(vm.IsSingleLineCompose);
            Assert.False(vm.IsMultiLineCompose);

            // 通过 SetComposeModeCommand 切换
            vm.SetComposeModeCommand.Execute(ComposeMode.MultiLine);
            Assert.Equal(ComposeMode.MultiLine, vm.ComposeMode);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
