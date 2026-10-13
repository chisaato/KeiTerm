using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Ssh.Abstractions;
using Xunit;

namespace Kei.Term.Tests;

// 自然排序：比较器本身、树同级排序规约、仓库加载顺序与视图层建树必须一致
public class TreeNaturalOrderingTests
{
    // ---- NaturalStringComparer ----

    [Theory]
    [InlineData("pve-3", "pve-11")]
    [InlineData("pve-11", "pve-31")]
    [InlineData("file2", "file10")]
    [InlineData("node-9", "node-10")]
    public void Comparer_OrdersNumericSegmentsByValue(string smaller, string larger)
    {
        Assert.True(NaturalStringComparer.Instance.Compare(smaller, larger) < 0);
        Assert.True(NaturalStringComparer.Instance.Compare(larger, smaller) > 0);
    }

    [Fact]
    public void Comparer_OrdersFullSequence_Naturally()
    {
        // 字典序会排成 ...-11、-12、-3、-31，自然排序应把 -3 提到最前
        string[] input = ["vat-sq-pve-11", "vat-sq-pve-3", "vat-sq-pve-31", "vat-sq-pve-12", "vat-sq-pve-2"];

        string[] sorted = input.OrderBy(s => s, NaturalStringComparer.Instance).ToArray();

        Assert.Equal(
            new[] { "vat-sq-pve-2", "vat-sq-pve-3", "vat-sq-pve-11", "vat-sq-pve-12", "vat-sq-pve-31" },
            sorted);
    }

    [Fact]
    public void Comparer_IsCaseInsensitive()
    {
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("Node-2", "node-2"));
        Assert.True(NaturalStringComparer.Instance.Compare("Node2", "node10") < 0);
    }

    [Fact]
    public void Comparer_TreatsLeadingZerosAsSameNumber()
    {
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("host01", "host1"));
        Assert.True(NaturalStringComparer.Instance.Compare("host007", "host10") < 0);
    }

    [Fact]
    public void Comparer_HandlesNullsAndPrefixes()
    {
        Assert.True(NaturalStringComparer.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a", null) > 0);
        Assert.Equal(0, NaturalStringComparer.Instance.Compare(null, null));
        Assert.True(NaturalStringComparer.Instance.Compare("abc", "abcd") < 0);
    }

    // ---- TreeNodeOrdering ----

    [Fact]
    public void Ordering_PutsFoldersBeforeSessions()
    {
        var folder = new FolderNode { Name = "zzz-folder" };
        var session = new SessionNode { Name = "aaa-session", Host = "h" };

        // 类型优先于名称：字母序靠后的文件夹仍排在字母序靠前的会话之前
        Assert.True(TreeNodeOrdering.Compare(folder, session) < 0);
        Assert.True(TreeNodeOrdering.Compare(session, folder) > 0);
    }

    [Fact]
    public void Ordering_SortsNamesNaturallyWithinSameTypeAndOrder()
    {
        var nodes = new List<TreeNodeBase>
        {
            new SessionNode { Name = "pve-11", Host = "h" },
            new SessionNode { Name = "pve-3", Host = "h" },
            new SessionNode { Name = "pve-31", Host = "h" },
        };

        TreeNodeOrdering.Sort(nodes);

        Assert.Equal(new[] { "pve-3", "pve-11", "pve-31" }, nodes.Select(n => n.Name));
    }

    [Fact]
    public void Ordering_SortOrderWinsOverName()
    {
        var first = new SessionNode { Name = "zzz", SortOrder = 0, Host = "h" };
        var second = new SessionNode { Name = "aaa", SortOrder = 1, Host = "h" };
        var nodes = new List<TreeNodeBase> { second, first };

        TreeNodeOrdering.Sort(nodes);

        Assert.Equal(new[] { "zzz", "aaa" }, nodes.Select(n => n.Name));
    }

    [Fact]
    public void Ordering_AsciiFirstMode_BoostsAsciiBeforeCjk()
    {
        var ascii = new FolderNode { Name = "zzz" };
        var cjk = new FolderNode { Name = "佛山" };

        Assert.True(TreeNodeOrdering.Compare(ascii, cjk, asciiFirst: true) < 0);
        Assert.True(TreeNodeOrdering.Compare(cjk, ascii, asciiFirst: true) > 0);

        var nodes = new List<TreeNodeBase> { cjk, ascii };
        TreeNodeOrdering.Sort(nodes, asciiFirst: true);
        Assert.Equal(new[] { "zzz", "佛山" }, nodes.Select(n => n.Name));
    }

    // ---- 仓库加载：顺序与视图层一致 ----

    [Fact]
    public async Task Repository_LoadsNodesInNaturalOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"keiterm_natural_{Guid.NewGuid():N}.db");
        try
        {
            var repo = new SqliteTreeRepository($"Data Source={path}");
            await repo.InitializeAsync();

            // 故意乱序插入，验证加载后按 pve-3 < pve-11 < pve-31 重排
            await repo.SaveNodeAsync(new SessionNode { Name = "pve-11", Host = "h" });
            await repo.SaveNodeAsync(new SessionNode { Name = "pve-3", Host = "h" });
            await repo.SaveNodeAsync(new SessionNode { Name = "pve-31", Host = "h" });

            IReadOnlyList<TreeNodeBase> loaded = await repo.GetAllNodesAsync();

            Assert.Equal(new[] { "pve-3", "pve-11", "pve-31" }, loaded.Select(n => n.Name));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // ---- 视图层建树（新增/重命名/导入后重新定位最终都汇到这里）----

    [Fact]
    public async Task ReloadTree_BuildsSiblingsInNaturalOrder()
    {
        var repo = new StaticTreeRepository(
            new FolderNode { Name = "prod" },
            new SessionNode { Name = "pve-11", Host = "h" },
            new SessionNode { Name = "pve-3", Host = "h" },
            new SessionNode { Name = "pve-31", Host = "h" });

        MainViewModel vm = Create(repo);
        await vm.ReloadTreeAsync();

        var root = Assert.IsType<VirtualRootNode>(Assert.Single(vm.TreeNodes));

        // 目录优先，会话按自然顺序而非字典序
        Assert.Equal(new[] { "prod", "pve-3", "pve-11", "pve-31" }, root.Children.Select(n => n.Name));
    }

    private static MainViewModel Create(ITreeRepository repo)
    {
        string path = Path.Combine(Path.GetTempPath(), $"keiterm_natural_vm_{Guid.NewGuid():N}.json");
        return new MainViewModel(
            repo,
            new EmptyIdentities(),
            new PlainVault(),
            new PlainVault(),
            new JsonSettingsService(path),
            new UnusedFactory());
    }

    private sealed class StaticTreeRepository : ITreeRepository
    {
        private readonly List<TreeNodeBase> _nodes;

        public StaticTreeRepository(params TreeNodeBase[] nodes) => _nodes = [.. nodes];

        public Task<IReadOnlyList<TreeNodeBase>> GetAllNodesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TreeNodeBase>>(_nodes);

        public Task<TreeNodeBase?> GetNodeByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<TreeNodeBase?>(_nodes.FirstOrDefault(n => n.Id == id));

        public Task SaveNodeAsync(TreeNodeBase node, CancellationToken ct = default) => Task.CompletedTask;
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
