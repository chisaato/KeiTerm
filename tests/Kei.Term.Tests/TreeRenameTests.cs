using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views.Controls;
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

    [Fact]
    public Task SessionRename_UpdatesBoundTreeItemViewText_InUi() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        var repo = new RecordingTree();
        var vm = Create(repo);
        var session = new SessionNode { Name = "web", Host = "web.example", Username = "ops" };
        vm.SelectedTreeNode = session;

        var itemView = new SessionTreeItemView { DataContext = session };
        var window = new Window { Width = 300, Height = 100, Content = itemView };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            var textBlock = itemView.GetVisualDescendants()
                .OfType<TextBlock>()
                .First(tb => tb.Classes.Contains("name"));
            Assert.Equal("web", textBlock.Text);

            // 第一次进入重命名编辑态
            vm.RenameSelectedNodeCommand.Execute(null);
            Assert.Equal("web", vm.TreeRenameText);

            // 提交修改为新名字
            await vm.CommitTreeRenameAsync("api-server");
            HeadlessAvalonia.Pump();

            // 验证内存模型
            Assert.Equal("api-server", session.Name);

            // 验证真实界面绑定链路：界面上的文本必须同步更新为新名字
            Assert.Equal("api-server", textBlock.Text);

            // 再次改名时，编辑框读取到的也必须是已改的新名字
            vm.RenameSelectedNodeCommand.Execute(null);
            Assert.Equal("api-server", vm.TreeRenameText);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task FolderRename_UpdatesBoundTreeItemViewText_InUi() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        var repo = new RecordingTree();
        var vm = Create(repo);
        var folder = new FolderNode { Name = "old-folder" };
        vm.SelectedTreeNode = folder;

        var itemView = new SessionTreeItemView { DataContext = folder };
        var window = new Window { Width = 300, Height = 100, Content = itemView };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            var textBlock = itemView.GetVisualDescendants()
                .OfType<TextBlock>()
                .First(tb => tb.Classes.Contains("name"));
            Assert.Equal("old-folder", textBlock.Text);

            vm.RenameSelectedNodeCommand.Execute(null);
            Assert.Equal("old-folder", vm.TreeRenameText);

            await vm.CommitTreeRenameAsync("new-folder");
            HeadlessAvalonia.Pump();

            Assert.Equal("new-folder", folder.Name);
            Assert.Equal("new-folder", textBlock.Text);

            vm.RenameSelectedNodeCommand.Execute(null);
            Assert.Equal("new-folder", vm.TreeRenameText);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TreeNodeBase_NameChange_RaisesPropertyChanged()
    {
        var session = new SessionNode { Name = "old" };
        string? changedProp = null;
        session.PropertyChanged += (_, e) => changedProp = e.PropertyName;

        session.Name = "new";

        Assert.Equal(nameof(TreeNodeBase.Name), changedProp);
    }

    [Fact]
    public async Task SessionRename_PropagatesToOpenTabTitle_WhenNotManuallyRenamed()
    {
        var repo = new RecordingTree();
        var vm = Create(repo);
        try
        {
            var session = new SessionNode { Name = "web", Host = "web.example", Username = "ops" };
            vm.SelectedTreeNode = session;

            var config = new ResolvedSessionConfig(
                session.Id, session.Name, session.Host, 22, session.Username, null, "xterm-256color", null, null,
                new Dictionary<string, string>());
            ((IConnectionHost)vm).OpenTab(config);
            var tab = vm.Tabs.First();
            Assert.Equal("web", tab.Title);

            await vm.CommitTreeRenameAsync("api-server");

            Assert.Equal("api-server", tab.Title);
            Assert.Equal("api-server", tab.Config?.SessionName);
        }
        finally
        {
            foreach (var tab in vm.Tabs.ToList()) await tab.DisposeAsync();
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionRename_PropagatesToMultipleTabs_OfSameConnection()
    {
        var repo = new RecordingTree();
        var vm = Create(repo);
        try
        {
            var session = new SessionNode { Name = "web", Host = "web.example", Username = "ops" };
            vm.SelectedTreeNode = session;

            var config = new ResolvedSessionConfig(
                session.Id, session.Name, session.Host, 22, session.Username, null, "xterm-256color", null, null,
                new Dictionary<string, string>());

            // 同一会话打开两个标签
            ((IConnectionHost)vm).OpenTab(config);
            ((IConnectionHost)vm).OpenTab(config);

            Assert.Equal(2, vm.Tabs.Count);
            Assert.Equal("web", vm.Tabs[0].Title);
            Assert.Equal("web", vm.Tabs[1].Title);

            await vm.CommitTreeRenameAsync("api-cluster");

            Assert.Equal("api-cluster", vm.Tabs[0].Title);
            Assert.Equal("api-cluster", vm.Tabs[1].Title);
        }
        finally
        {
            foreach (var tab in vm.Tabs.ToList()) await tab.DisposeAsync();
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionRename_DoesNotOverrideManualTabTitle()
    {
        var repo = new RecordingTree();
        var vm = Create(repo);
        try
        {
            var session = new SessionNode { Name = "web", Host = "web.example", Username = "ops" };
            vm.SelectedTreeNode = session;

            var config = new ResolvedSessionConfig(
                session.Id, session.Name, session.Host, 22, session.Username, null, "xterm-256color", null, null,
                new Dictionary<string, string>());

            ((IConnectionHost)vm).OpenTab(config);
            var tab = vm.Tabs.First();

            // 用户在标签上右键手动重命名
            tab.Rename("ops-custom-terminal");
            Assert.Equal("ops-custom-terminal", tab.Title);

            // 连接管理器改名
            await vm.CommitTreeRenameAsync("web-renamed");

            // 手动自定义标题保持不变，不被覆盖；底层 Config.SessionName 仍同步更新
            Assert.Equal("ops-custom-terminal", tab.Title);
            Assert.Equal("web-renamed", tab.Config?.SessionName);
        }
        finally
        {
            foreach (var tab in vm.Tabs.ToList()) await tab.DisposeAsync();
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionRename_PreservesRemoteTitleFollow_WithNewSessionName()
    {
        var repo = new RecordingTree();
        var vm = Create(repo);
        try
        {
            var session = new SessionNode { Name = "prod-db", Host = "10.0.0.5", Username = "ops" };
            vm.SelectedTreeNode = session;

            var config = new ResolvedSessionConfig(
                session.Id, session.Name, session.Host, 22, session.Username, null, "xterm-256color", null, null,
                new Dictionary<string, string>(), FollowRemoteTitle: true);

            ((IConnectionHost)vm).OpenTab(config);
            var tab = vm.Tabs.First();

            // 远端设置了动态标题
            tab.ApplyRemoteTitle("top - 12:00:00");
            Assert.Equal("prod-db · top - 12:00:00", tab.Title);

            // 连接管理器改名
            await vm.CommitTreeRenameAsync("primary-db");

            // 前缀连接名自动更新，远端动态内容完好保留并继续跟随
            Assert.Equal("primary-db · top - 12:00:00", tab.Title);
        }
        finally
        {
            foreach (var tab in vm.Tabs.ToList()) await tab.DisposeAsync();
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionRename_RerendersTitleTemplate_WithNewLocalName()
    {
        var repo = new RecordingTree();
        var vm = Create(repo);
        try
        {
            var session = new SessionNode { Name = "web-prod", Host = "192.168.1.10", Username = "deploy" };
            vm.SelectedTreeNode = session;

            var config = new ResolvedSessionConfig(
                session.Id, session.Name, session.Host, 22, session.Username, null, "xterm-256color", null, null,
                new Dictionary<string, string>(), TabTitleTemplate: "{local_name} | {local_user}@{local_host}");

            ((IConnectionHost)vm).OpenTab(config);
            var tab = vm.Tabs.First();
            Assert.Equal("web-prod | deploy@192.168.1.10", tab.Title);

            // 连接管理器改名
            await vm.CommitTreeRenameAsync("web-canary");

            // 标题模板中使用 {local_name} 的部分自动按新名字重新渲染
            Assert.Equal("web-canary | deploy@192.168.1.10", tab.Title);
        }
        finally
        {
            foreach (var tab in vm.Tabs.ToList()) await tab.DisposeAsync();
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReloadTreeAsync_RebindsToNewSessionInstance_UpdatesSubsequentRenames()
    {
        var repo = new RecordingTree();
        var sessionId = Guid.NewGuid();
        var sessionV1 = new SessionNode { Id = sessionId, Name = "db-v1", Host = "10.0.0.1", Username = "root" };
        repo.Nodes.Add(sessionV1);

        var vm = Create(repo);
        try
        {
            await vm.ReloadTreeAsync();

            var config = new ResolvedSessionConfig(
                sessionId, "db-v1", "10.0.0.1", 22, "root", null, "xterm-256color", null, null,
                new Dictionary<string, string>());

            ((IConnectionHost)vm).OpenTab(config);
            var tab = vm.Tabs.First();
            Assert.Equal("db-v1", tab.Title);

            // 模拟仓储重载：数据库返回全新的节点实例（名称已在库中变更为 db-v2）
            var sessionV2 = new SessionNode { Id = sessionId, Name = "db-v2", Host = "10.0.0.1", Username = "root" };
            repo.Nodes.Clear();
            repo.Nodes.Add(sessionV2);

            await vm.ReloadTreeAsync();

            // 重新挂接后，标签自动同步为新实例上的名称 db-v2
            Assert.Equal("db-v2", tab.Title);

            // 旧实例发生改变不再影响已重绑的标签
            sessionV1.Name = "db-stale-ignored";
            Assert.Equal("db-v2", tab.Title);

            // 新实例重命名能正确驱动标签标题更新
            sessionV2.Name = "db-v3";
            Assert.Equal("db-v3", tab.Title);
        }
        finally
        {
            foreach (var tab in vm.Tabs.ToList()) await tab.DisposeAsync();
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReloadTreeAsync_PreservesManualTabTitle_AndUpdatesConfigAndToolTip()
    {
        var repo = new RecordingTree();
        var sessionId = Guid.NewGuid();
        var sessionV1 = new SessionNode { Id = sessionId, Name = "srv-v1", Host = "10.0.0.2", Username = "admin" };
        repo.Nodes.Add(sessionV1);

        var vm = Create(repo);
        try
        {
            await vm.ReloadTreeAsync();

            var config = new ResolvedSessionConfig(
                sessionId, "srv-v1", "10.0.0.2", 22, "admin", null, "xterm-256color", null, null,
                new Dictionary<string, string>());

            ((IConnectionHost)vm).OpenTab(config);
            var tab = vm.Tabs.First();

            // 手动改名
            tab.Rename("my-pinned-tab");
            Assert.Equal("my-pinned-tab", tab.Title);

            // 树重载返回新节点实例
            var sessionV2 = new SessionNode { Id = sessionId, Name = "srv-v2", Host = "10.0.0.2", Username = "admin" };
            repo.Nodes.Clear();
            repo.Nodes.Add(sessionV2);

            await vm.ReloadTreeAsync();

            // 手动标题被保护
            Assert.Equal("my-pinned-tab", tab.Title);
            Assert.Equal("srv-v2", tab.Config?.SessionName);
            Assert.Contains("admin@10.0.0.2:22", tab.ToolTip);
        }
        finally
        {
            foreach (var tab in vm.Tabs.ToList()) await tab.DisposeAsync();
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReloadTreeAsync_UnbindsSession_WhenSessionRemoved()
    {
        var repo = new RecordingTree();
        var sessionId = Guid.NewGuid();
        var session = new SessionNode { Id = sessionId, Name = "temp-srv", Host = "10.0.0.3", Username = "user" };
        repo.Nodes.Add(session);

        var vm = Create(repo);
        try
        {
            await vm.ReloadTreeAsync();

            var config = new ResolvedSessionConfig(
                sessionId, "temp-srv", "10.0.0.3", 22, "user", null, "xterm-256color", null, null,
                new Dictionary<string, string>());

            ((IConnectionHost)vm).OpenTab(config);
            var tab = vm.Tabs.First();
            Assert.Equal("temp-srv", tab.Title);

            // 会话在库中被删除
            repo.Nodes.Clear();
            await vm.ReloadTreeAsync();

            // 原节点名称改变不再影响无对应源节点的标签
            session.Name = "temp-srv-renamed-after-delete";
            Assert.Equal("temp-srv", tab.Title);
        }
        finally
        {
            foreach (var tab in vm.Tabs.ToList()) await tab.DisposeAsync();
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task TabDispose_UnsubscribesFromSessionNode_PreventsLeak()
    {
        var repo = new RecordingTree();
        var vm = Create(repo);
        try
        {
            var session = new SessionNode { Name = "web", Host = "web.example", Username = "ops" };
            vm.SelectedTreeNode = session;

            var config = new ResolvedSessionConfig(
                session.Id, session.Name, session.Host, 22, session.Username, null, "xterm-256color", null, null,
                new Dictionary<string, string>());

            ((IConnectionHost)vm).OpenTab(config);
            var tab = vm.Tabs.First();

            // 释放前：会话节点改名可驱动标签名
            session.Name = "web-live";
            Assert.Equal("web-live", tab.Title);

            // 释放标签
            await tab.DisposeAsync();

            // 释放后：会话节点再次重命名，不再影响已释放的 tab
            session.Name = "web-after-dispose";
            Assert.Equal("web-live", tab.TabName);

            // 已释放标签重新调用 BindSessionNode 不会重新挂接
            tab.BindSessionNode(session);
            session.Name = "web-never-subscribed";
            Assert.Equal("web-live", tab.TabName);
        }
        finally
        {
            await vm.DisposeAsync();
        }
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
        public List<TreeNodeBase> Nodes { get; } = [];
        public List<TreeNodeBase> Saved { get; } = [];
        public bool Fail { get; set; }

        public Task<IReadOnlyList<TreeNodeBase>> GetAllNodesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TreeNodeBase>>(Nodes.ToList());

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
        public Task RemoveMasterPasswordAsync(CancellationToken ct = default) => Task.CompletedTask;
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
