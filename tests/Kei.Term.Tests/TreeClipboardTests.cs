using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Storage;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

public class TreeClipboardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CutPaste_PreservesSessionAndNestedFolderForwards(bool wholeFolder)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        TreeNodeBase selected = wholeFolder ? fixture.SourceFolder : fixture.Sessions[1];
        Guid[] sessionIds = wholeFolder ? fixture.Sessions.Select(session => session.Id).ToArray() : [selected.Id];
        List<PortForward> originalForwards = [];
        foreach (Guid id in sessionIds) originalForwards.AddRange(await fixture.Forwards.GetBySessionAsync(id));
        fixture.Model.SelectedTreeNode = selected;
        fixture.Model.FilterText = "alpha";

        await fixture.Model.CutNodeCommand.ExecuteAsync(null);

        Assert.Null(await fixture.Tree.GetNodeByIdAsync(selected.Id));
        foreach (Guid id in sessionIds) Assert.Empty(await fixture.Forwards.GetBySessionAsync(id));
        fixture.Model.SelectedTreeNode = fixture.TargetFolder;
        await fixture.Model.PasteNodeCommand.ExecuteAsync(null);

        Assert.Equal(fixture.TargetFolder.Id, (await fixture.Tree.GetNodeByIdAsync(selected.Id))!.ParentId);
        foreach (PortForward expected in originalForwards)
        {
            PortForward restored = Assert.Single(await fixture.Forwards.GetBySessionAsync(expected.SessionId), forward => forward.Id == expected.Id);
            AssertForwardMatches(expected, restored);
        }
        foreach (Guid id in sessionIds) Assert.NotNull(await fixture.Tree.GetNodeByIdAsync(id));
        Assert.False(fixture.Model.PasteNodeCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("source")]
    public async Task FilteredCopyAndPaste_IncludeHiddenDescendantsAndIndependentForwards(string filter)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Model.FilterText = filter;
        fixture.Model.SelectedTreeNode = fixture.Model.DebugAllNodesCache.Single(node => node.Id == fixture.SourceFolder.Id);

        await fixture.Model.CopyNodeCommand.ExecuteAsync(null);

        IReadOnlyList<TreeNodeBase> afterCopy = await fixture.Tree.GetAllNodesAsync();
        FolderNode firstCopy = Assert.Single(afterCopy.OfType<FolderNode>(), folder => folder.ParentId == null && folder.Id != fixture.SourceFolder.Id && folder.Id != fixture.TargetFolder.Id);
        await AssertCompleteCopyAsync(fixture, firstCopy);

        // 再次改变显示树，不能改变已经捕获的剪贴板内容。
        fixture.Model.FilterText = "beta";
        fixture.Model.SelectedTreeNode = fixture.TargetFolder;
        await fixture.Model.PasteNodeCommand.ExecuteAsync(null);
        FolderNode pasted = Assert.Single((await fixture.Tree.GetAllNodesAsync()).OfType<FolderNode>(), folder => folder.ParentId == fixture.TargetFolder.Id);
        await AssertCompleteCopyAsync(fixture, pasted);
        Assert.False(fixture.Model.PasteNodeCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedForwardRestore_RemovesPartialSubtree_AndRetainsClipboardForRetry()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Model.SelectedTreeNode = fixture.SourceFolder;
        await fixture.Model.CutNodeCommand.ExecuteAsync(null);
        fixture.ForwardAccess.FailOnSecondSave = true;
        fixture.Model.SelectedTreeNode = fixture.TargetFolder;

        await fixture.Model.PasteNodeCommand.ExecuteAsync(null);

        Assert.Null(await fixture.Tree.GetNodeByIdAsync(fixture.SourceFolder.Id));
        foreach (SessionNode session in fixture.Sessions) Assert.Empty(await fixture.Forwards.GetBySessionAsync(session.Id));
        Assert.True(fixture.Model.PasteNodeCommand.CanExecute(null));

        fixture.ForwardAccess.FailOnSecondSave = false;
        await fixture.Model.PasteNodeCommand.ExecuteAsync(null);

        Assert.Equal(fixture.TargetFolder.Id, (await fixture.Tree.GetNodeByIdAsync(fixture.SourceFolder.Id))!.ParentId);
        foreach (SessionNode session in fixture.Sessions) Assert.Single(await fixture.Forwards.GetBySessionAsync(session.Id));
        Assert.False(fixture.Model.PasteNodeCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedForwardSnapshot_DoesNotDeleteSource()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.ForwardAccess.FailRead = true;
        fixture.Model.SelectedTreeNode = fixture.SourceFolder;

        await fixture.Model.CutNodeCommand.ExecuteAsync(null);

        Assert.NotNull(await fixture.Tree.GetNodeByIdAsync(fixture.SourceFolder.Id));
        foreach (SessionNode session in fixture.Sessions) Assert.Single(await fixture.Forwards.GetBySessionAsync(session.Id));
        Assert.False(fixture.Model.PasteNodeCommand.CanExecute(null));
    }

    private static async Task AssertCompleteCopyAsync(Fixture fixture, FolderNode copy)
    {
        IReadOnlyList<TreeNodeBase> all = await fixture.Tree.GetAllNodesAsync();
        IReadOnlyList<Guid> ids = TreeTraversal.DescendantSessionIds(copy.Id, all);
        Assert.Equal(fixture.Sessions.Length, ids.Count);
        foreach (SessionNode original in fixture.Sessions)
        {
            SessionNode cloned = Assert.Single(all.OfType<SessionNode>(), session => ids.Contains(session.Id) && session.Name == original.Name);
            Assert.NotEqual(original.Id, cloned.Id);
            Assert.Equal(original.Host, cloned.Host);
            PortForward originalForward = Assert.Single(await fixture.Forwards.GetBySessionAsync(original.Id));
            PortForward copiedForward = Assert.Single(await fixture.Forwards.GetBySessionAsync(cloned.Id));
            Assert.NotEqual(originalForward.Id, copiedForward.Id);
            AssertForwardMatches(originalForward, copiedForward);
        }
    }

    private static void AssertForwardMatches(PortForward expected, PortForward actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.BindAddress, actual.BindAddress);
        Assert.Equal(expected.ListenPort, actual.ListenPort);
        Assert.Equal(expected.DestinationHost, actual.DestinationHost);
        Assert.Equal(expected.DestinationPort, actual.DestinationPort);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Directory { get; init; }
        public required SqliteTreeRepository Tree { get; init; }
        public required SqlitePortForwardRepository Forwards { get; init; }
        public required FailingForwards ForwardAccess { get; init; }
        public required MainViewModel Model { get; init; }
        public required FolderNode SourceFolder { get; init; }
        public required FolderNode TargetFolder { get; init; }
        public required SessionNode[] Sessions { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "keiterm_clipboard_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            SqliteConnectionFactory database = new($"Data Source={Path.Combine(directory, "clipboard.db")}");
            SqliteTreeRepository tree = new(database);
            await tree.InitializeAsync();
            FolderNode source = new() { Name = "source" };
            FolderNode nested = new() { Name = "nested", ParentId = source.Id };
            FolderNode target = new() { Name = "target" };
            SessionNode[] sessions =
            [
                new() { Name = "alpha", Host = "alpha.test", ParentId = source.Id },
                new() { Name = "beta", Host = "beta.test", ParentId = nested.Id }
            ];
            await tree.SaveNodesAsync([source, nested, target, .. sessions]);
            SqlitePortForwardRepository forwards = new(database);
            foreach (SessionNode session in sessions)
                await forwards.SaveAsync(new PortForward
                {
                    SessionId = session.Id, Name = session.Name + " database", Mode = PortForwardMode.Local,
                    BindAddress = "127.0.0.1", ListenPort = 15432, DestinationHost = session.Host, DestinationPort = 5432
                });
            FailingForwards access = new(forwards);
            InternalVaultManager vault = new(database);
            MainViewModel model = new(tree, new SqliteIdentityRepository(database), vault, vault,
                new JsonSettingsService(Path.Combine(directory, "settings.json")), new SshSessionFactory(),
                uiDispatch: action => action(), portForwards: access);
            await model.ReloadTreeAsync();
            return new Fixture
            {
                Directory = directory, Tree = tree, Forwards = forwards, ForwardAccess = access, Model = model,
                SourceFolder = source, TargetFolder = target, Sessions = sessions
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Model.DisposeAsync();
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class FailingForwards(IPortForwardRepository inner) : IPortForwardRepository
    {
        private int _saveCount;
        public bool FailRead { get; set; }
        public bool FailOnSecondSave { get; set; }
        public Task<IReadOnlyList<PortForward>> GetBySessionAsync(Guid id, CancellationToken ct = default)
            => FailRead ? throw new IOException("cannot capture forwards") : inner.GetBySessionAsync(id, ct);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
        public Task SaveAsync(PortForward forward, CancellationToken ct = default)
        {
            if (FailOnSecondSave && ++_saveCount == 2) throw new IOException("cannot restore second forward");
            return inner.SaveAsync(forward, ct);
        }
    }
}
