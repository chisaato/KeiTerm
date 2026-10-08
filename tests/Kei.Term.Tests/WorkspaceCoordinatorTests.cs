using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Workspaces;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.Tests;

public class WorkspaceCoordinatorTests
{
    [Fact]
    public void Move_PreservesTabIdentity_AndSftpState()
    {
        using WorkspaceCoordinator coordinator = new(new WeakReferenceMessenger());
        CloseCounter closes = Listen(coordinator);
        Guid sessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        TerminalTabViewModel kept = OpenTab(coordinator, "kept", sessionId);
        TerminalTabViewModel other = OpenTab(coordinator, "other", sessionId);
        TerminalTabViewModel anchor = OpenTab(coordinator, "anchor", sessionId);
        RecordingFileSystem fileSystem = new();
        RemoteFileManagerViewModel fileManager = AttachSftp(kept, fileSystem);
        RemoteFileItem listed = kept.FileManager!.Items[0];
        FileTransferTaskItemViewModel transfer = kept.FileManager.TransferTasks[0];

        IDocumentDock source = WorkspaceTree.GroupOf(coordinator.Layout, kept);
        coordinator.Factory.SplitToDock(source, WorkspaceTree.DocumentOf(coordinator.Layout, other), DockOperation.Right);
        IDocumentDock destination = WorkspaceTree.GroupOf(coordinator.Layout, other);
        source = WorkspaceTree.GroupOf(coordinator.Layout, kept);

        // 工厂拖放不经过 MoveTab，活动项与全集索引仍必须跟着 Dock 树走。
        coordinator.Factory.MoveDockable(
            source,
            destination,
            WorkspaceTree.DocumentOf(coordinator.Layout, kept),
            WorkspaceTree.DocumentOf(coordinator.Layout, other));
        Assert.Same(kept, coordinator.ActiveTab);
        Assert.Contains(kept, coordinator.AllTabs);
        Assert.Contains(anchor, coordinator.AllTabs);
        Assert.True(WorkspaceTree.Contains(coordinator.Layout, source));

        coordinator.MoveTab(kept, source, 0);

        TerminalWorkspaceDocument moved = WorkspaceTree.DocumentOf(coordinator.Layout, kept);
        Assert.Same(kept, moved.Tab);
        Assert.NotEqual(sessionId.ToString(), moved.Id);
        Assert.NotEqual(sessionId.ToString("N"), moved.Id);
        Assert.NotEqual(WorkspaceTree.DocumentOf(coordinator.Layout, other).Id, moved.Id);
        Assert.Equal(0, WorkspaceTree.IndexOf(source, kept));
        Assert.Same(fileManager, kept.FileManager);
        Assert.True(kept.IsFileManagerVisible);
        Assert.True(kept.IsFileManagerOnLeft);
        Assert.True(fileManager.IsOnLeft);
        Assert.Equal("/var/log", fileManager.CurrentPath);
        Assert.Same(listed, fileManager.Items[0]);
        Assert.Same(transfer, fileManager.TransferTasks[0]);
        Assert.Equal(42, transfer.Progress);
        Assert.False(fileSystem.Disposed);
        Assert.False(kept.IsDisposed);
        Assert.Equal(0, closes.Count);
        Assert.Equal(3, coordinator.AllTabs.Count);
        Assert.Same(kept, coordinator.AllTabs[0]);
        Assert.Same(other, coordinator.AllTabs[1]);
        Assert.Same(anchor, coordinator.AllTabs[2]);
    }

    [Fact]
    public void Move_LastTabCollapsesSource_WithoutDisconnecting()
    {
        using WorkspaceCoordinator coordinator = new(new WeakReferenceMessenger());
        CloseCounter closes = Listen(coordinator);
        TerminalTabViewModel only = OpenTab(coordinator, "only");
        TerminalTabViewModel anchor = OpenTab(coordinator, "anchor");
        RecordingFileSystem fileSystem = new();
        RemoteFileManagerViewModel fileManager = AttachSftp(only, fileSystem);
        IDocumentDock source = WorkspaceTree.GroupOf(coordinator.Layout, only);
        coordinator.Factory.SplitToDock(source, WorkspaceTree.DocumentOf(coordinator.Layout, anchor), DockOperation.Bottom);
        IDocumentDock destination = WorkspaceTree.GroupOf(coordinator.Layout, anchor);
        source = WorkspaceTree.GroupOf(coordinator.Layout, only);

        coordinator.MoveTab(only, destination, destination.VisibleDockables!.Count);

        Assert.False(WorkspaceTree.Contains(coordinator.Layout, source));
        Assert.Same(only, WorkspaceTree.DocumentOf(coordinator.Layout, only).Tab);
        Assert.Same(fileManager, only.FileManager);
        Assert.False(fileSystem.Disposed);
        Assert.False(only.IsDisposed);
        Assert.Equal(0, closes.Count);
        Assert.Contains(only, coordinator.AllTabs);
        Assert.Contains(anchor, coordinator.AllTabs);
    }

    [Fact]
    public void Reorder_InvalidIndexIsNoOp()
    {
        using WorkspaceCoordinator coordinator = new(new WeakReferenceMessenger());
        TerminalTabViewModel first = OpenTab(coordinator, "first");
        TerminalTabViewModel second = OpenTab(coordinator, "second");
        TerminalTabViewModel third = OpenTab(coordinator, "third");
        IDocumentDock group = WorkspaceTree.GroupOf(coordinator.Layout, first);
        TerminalTabViewModel[] before = WorkspaceTree.Tabs(group);

        coordinator.ReorderTab(second, -1);
        coordinator.ReorderTab(second, 3);
        coordinator.ReorderTab(second, 99);
        coordinator.ReorderTab(second, 1);

        Assert.Equal(before, WorkspaceTree.Tabs(group));
        Assert.Same(third, coordinator.ActiveTab);
        Assert.True(third.IsSelected);
        Assert.False(first.IsSelected);
        Assert.False(second.IsSelected);
        Assert.All(new[] { first, second, third }, tab => Assert.False(tab.IsDisposed));
    }

    [Fact]
    public void Split_LeavesBothGroupsSelected()
    {
        using WorkspaceCoordinator coordinator = new(new WeakReferenceMessenger());
        TerminalTabViewModel left = OpenTab(coordinator, "left");
        TerminalTabViewModel right = OpenTab(coordinator, "right");
        IDocumentDock source = WorkspaceTree.GroupOf(coordinator.Layout, left);

        coordinator.Factory.SplitToDock(source, WorkspaceTree.DocumentOf(coordinator.Layout, right), DockOperation.Right);

        IDocumentDock leftGroup = WorkspaceTree.GroupOf(coordinator.Layout, left);
        IDocumentDock rightGroup = WorkspaceTree.GroupOf(coordinator.Layout, right);
        Assert.NotSame(leftGroup, rightGroup);
        Assert.Same(left, ((TerminalWorkspaceDocument)leftGroup.ActiveDockable!).Tab);
        Assert.Same(right, ((TerminalWorkspaceDocument)rightGroup.ActiveDockable!).Tab);
        Assert.Single(coordinator.AllTabs, tab => tab.IsSelected);
        Assert.True(right.IsSelected);
        Assert.False(left.IsSelected);
        Assert.Same(right, coordinator.ActiveTab);
        Assert.Equal(2, coordinator.AllTabs.Count);
    }

    [Fact]
    public void Split_OnlyTabAgainstOwnGroupIsNoOp()
    {
        using WorkspaceCoordinator coordinator = new(new WeakReferenceMessenger());
        CloseCounter closes = Listen(coordinator);
        TerminalTabViewModel only = OpenTab(coordinator, "only");
        IDocumentDock group = WorkspaceTree.GroupOf(coordinator.Layout, only);
        int docksBefore = WorkspaceTree.Groups(coordinator.Layout).Count;

        coordinator.SplitTab(only, group, DockOperation.Left);
        coordinator.Factory.SplitToDock(group, WorkspaceTree.DocumentOf(coordinator.Layout, only), DockOperation.Right);
        coordinator.Factory.SplitToDock(group, WorkspaceTree.DocumentOf(coordinator.Layout, only), DockOperation.Window);

        Assert.Equal(docksBefore, WorkspaceTree.Groups(coordinator.Layout).Count);
        Assert.Same(group, WorkspaceTree.GroupOf(coordinator.Layout, only));
        Assert.Same(only, ((TerminalWorkspaceDocument)group.ActiveDockable!).Tab);
        Assert.False(only.IsDisposed);
        Assert.Equal(0, closes.Count);
        Assert.True(coordinator.Layout.Windows == null || coordinator.Layout.Windows.Count == 0);
    }

    [Fact]
    public void Activate_PreservesOtherGroupSelection()
    {
        using WorkspaceCoordinator coordinator = new(new WeakReferenceMessenger());
        ActiveCounter actives = ListenActive(coordinator);
        TerminalTabViewModel left = OpenTab(coordinator, "left");
        TerminalTabViewModel right = OpenTab(coordinator, "right");
        IDocumentDock source = WorkspaceTree.GroupOf(coordinator.Layout, left);
        coordinator.SplitTab(right, source, DockOperation.Top);
        IDocumentDock leftGroup = WorkspaceTree.GroupOf(coordinator.Layout, left);
        IDocumentDock rightGroup = WorkspaceTree.GroupOf(coordinator.Layout, right);
        int before = actives.Count;

        coordinator.Activate(left);

        Assert.Same(left, ((TerminalWorkspaceDocument)leftGroup.ActiveDockable!).Tab);
        Assert.Same(right, ((TerminalWorkspaceDocument)rightGroup.ActiveDockable!).Tab);
        Assert.Same(left, coordinator.ActiveTab);
        Assert.True(left.IsSelected);
        Assert.False(right.IsSelected);
        Assert.Equal(before + 1, actives.Count);
        Assert.Same(coordinator, actives.LastSource);
        Assert.Same(left, actives.LastTab);

        coordinator.Layout.ActiveDockable = null;
        Assert.Same(left, coordinator.ActiveTab);
        Assert.True(left.IsSelected);
        Assert.Same(right, ((TerminalWorkspaceDocument)rightGroup.ActiveDockable!).Tab);
    }

    [Fact]
    public void CloseRequest_IsRoutedOnce()
    {
        WeakReferenceMessenger messenger = WeakReferenceMessenger.Default;
        CloseSink sink = new();
        WorkspaceCoordinator coordinator = new();
        try
        {
            messenger.Register<CloseSink, WorkspaceCloseRequestedMessage>(sink, (recipient, message) =>
            {
                if (message.Source != coordinator)
                {
                    return;
                }

                recipient.Count++;
                recipient.LastTab = message.Tab;
                TerminalWorkspaceDocument document = WorkspaceTree.DocumentOf(coordinator.Layout, message.Tab);
                coordinator.Factory.CloseDockable(document);
                coordinator.RemoveTab(message.Tab);
            });

            TerminalTabViewModel first = OpenTab(coordinator, "first");
            TerminalTabViewModel second = OpenTab(coordinator, "second");
            TerminalWorkspaceDocument firstDocument = WorkspaceTree.DocumentOf(coordinator.Layout, first);
            coordinator.Factory.CloseDockable(firstDocument);

            Assert.Equal(1, sink.Count);
            Assert.Same(first, sink.LastTab);
            Assert.DoesNotContain(first, coordinator.AllTabs);
            Assert.False(WorkspaceTree.ContainsTab(coordinator.Layout, first));
            Assert.False(first.IsDisposed);
            Assert.Same(second, coordinator.ActiveTab);

            coordinator.Factory.CloseDockable(firstDocument);
            coordinator.RemoveTab(first);
            Assert.Equal(1, sink.Count);

            coordinator.Factory.CloseDockable(WorkspaceTree.DocumentOf(coordinator.Layout, second));
            Assert.Equal(2, sink.Count);
            Assert.Same(second, sink.LastTab);
            Assert.False(second.IsDisposed);
            Assert.Empty(coordinator.AllTabs);
        }
        finally
        {
            messenger.UnregisterAll(sink);
            coordinator.Dispose();
        }
    }

    [Fact]
    public void Remove_RepeatedCallIsNoOp()
    {
        using WorkspaceCoordinator coordinator = new(new WeakReferenceMessenger());
        CloseCounter closes = Listen(coordinator);
        TerminalTabViewModel tab = OpenTab(coordinator, "once");
        RecordingFileSystem fileSystem = new();
        AttachSftp(tab, fileSystem);

        coordinator.RemoveTab(tab);
        coordinator.RemoveTab(tab);

        Assert.Empty(coordinator.AllTabs);
        Assert.False(WorkspaceTree.ContainsTab(coordinator.Layout, tab));
        Assert.False(tab.IsDisposed);
        Assert.False(fileSystem.Disposed);
        Assert.Equal(0, closes.Count);
        Assert.Null(coordinator.ActiveTab);
    }

    [Fact]
    public void RemovingActiveTab_SelectsSameGroupNeighbour_OrRemainingGroup()
    {
        using WorkspaceCoordinator coordinator = new(new WeakReferenceMessenger());
        TerminalTabViewModel first = OpenTab(coordinator, "first");
        TerminalTabViewModel second = OpenTab(coordinator, "second");
        coordinator.Activate(first);
        IDocumentDock group = WorkspaceTree.GroupOf(coordinator.Layout, first);

        coordinator.RemoveTab(first);

        Assert.Same(second, coordinator.ActiveTab);
        Assert.True(second.IsSelected);
        Assert.False(first.IsSelected);
        Assert.Same(second, ((TerminalWorkspaceDocument)group.ActiveDockable!).Tab);
        Assert.False(first.IsDisposed);
        Assert.Contains(second, coordinator.AllTabs);

        TerminalTabViewModel other = OpenTab(coordinator, "other");
        coordinator.SplitTab(other, group, DockOperation.Right);
        IDocumentDock remaining = WorkspaceTree.GroupOf(coordinator.Layout, other);
        IDocumentDock closing = WorkspaceTree.GroupOf(coordinator.Layout, second);
        coordinator.Activate(second);

        coordinator.RemoveTab(second);

        Assert.False(WorkspaceTree.Contains(coordinator.Layout, closing));
        Assert.Same(other, coordinator.ActiveTab);
        Assert.True(other.IsSelected);
        Assert.Same(other, ((TerminalWorkspaceDocument)remaining.ActiveDockable!).Tab);
        Assert.False(second.IsDisposed);
        Assert.DoesNotContain(second, coordinator.AllTabs);
    }

    [Fact]
    public void FloatPolicy_DoesNotLetHigherPriorityAllowOverrideRoot()
    {
        using WorkspaceCoordinator coordinator = new(new WeakReferenceMessenger());
        TerminalTabViewModel first = OpenTab(coordinator, "first");
        TerminalTabViewModel second = OpenTab(coordinator, "second");
        IDocumentDock source = WorkspaceTree.GroupOf(coordinator.Layout, first);
        coordinator.Factory.SplitToDock(source, WorkspaceTree.DocumentOf(coordinator.Layout, second), DockOperation.Left);

        Assert.False(coordinator.Layout.RootDockCapabilityPolicy?.CanFloat);
        Assert.False(coordinator.Layout.RootDockCapabilityPolicy?.CanPin);
        Assert.NotEqual(true, coordinator.Layout.DockCapabilityPolicy?.CanFloat);
        AssertFloatBlocked(coordinator.Layout, coordinator.Layout);
        foreach (IDocumentDock group in WorkspaceTree.Groups(coordinator.Layout))
        {
            Assert.False(group.CanFloat);
            Assert.False(group.CanPin);
            Assert.False(group.DockCapabilityPolicy?.CanFloat);
            Assert.False(group.DockCapabilityPolicy?.CanPin);
            Assert.NotEqual(true, group.DockCapabilityOverrides?.CanFloat);
            Assert.NotEqual(true, group.DockCapabilityOverrides?.CanPin);
            IDockableDockingRestrictions restrictions = Assert.IsAssignableFrom<IDockableDockingRestrictions>(group);
            Assert.False(restrictions.AllowedDockOperations.Allows(DockOperation.Window));
            AssertFloatBlocked(group, group);
        }

        TerminalWorkspaceDocument document = WorkspaceTree.DocumentOf(coordinator.Layout, first);
        document.CanFloat = true;
        DockCapabilityEvaluation evaluation = DockCapabilityResolver.Evaluate(document, DockCapability.Float);
        Assert.False(evaluation.EffectiveValue);
        Assert.NotEqual(DockCapabilityValueSource.DockableOverride, evaluation.EffectiveSource);
        Assert.NotEqual(true, document.DockCapabilityOverrides?.CanFloat);
        Assert.False(document.AllowedDockOperations.Allows(DockOperation.Window));
        coordinator.Factory.FloatDockable(document);
        Assert.True(coordinator.Layout.Windows == null || coordinator.Layout.Windows.Count == 0);
        Assert.Same(document, WorkspaceTree.DocumentOf(coordinator.Layout, first));
    }

    [Fact]
    public void Dispose_UnsubscribesWithoutReleasingConnection()
    {
        WorkspaceCoordinator coordinator = new(new WeakReferenceMessenger());
        CloseCounter closes = Listen(coordinator);
        TerminalTabViewModel tab = OpenTab(coordinator, "live");
        RecordingFileSystem fileSystem = new();
        AttachSftp(tab, fileSystem);
        TerminalWorkspaceDocument document = WorkspaceTree.DocumentOf(coordinator.Layout, tab);

        coordinator.Dispose();
        coordinator.Dispose();
        coordinator.Factory.CloseDockable(document);
        coordinator.Activate(tab);

        Assert.Equal(0, closes.Count);
        Assert.False(tab.IsDisposed);
        Assert.False(fileSystem.Disposed);
        Assert.NotNull(tab.FileManager);
        Assert.Equal("/var/log", tab.FileManager.CurrentPath);
        Assert.True(tab.IsFileManagerVisible);
    }

    private static void AssertFloatBlocked(IDockable dockable, IDock? context)
    {
        DockCapabilityEvaluation evaluation = DockCapabilityResolver.Evaluate(dockable, DockCapability.Float, context);
        Assert.False(evaluation.EffectiveValue);
        Assert.NotEqual(true, dockable.DockCapabilityOverrides?.CanFloat);
    }

    private static TerminalTabViewModel OpenTab(WorkspaceCoordinator coordinator, string title, Guid? sessionId = null)
    {
        TerminalTabViewModel tab = new(title, "monospace", 14.0);
        if (sessionId != null)
        {
            tab.BindConfig(new ResolvedSessionConfig(
                sessionId.Value,
                title,
                "h",
                22,
                "u",
                null,
                "xterm-256color",
                null,
                null,
                new Dictionary<string, string>()));
        }

        coordinator.AddTab(tab);
        return tab;
    }

    private static RemoteFileManagerViewModel AttachSftp(TerminalTabViewModel tab, RecordingFileSystem fileSystem)
    {
        RemoteFileManagerViewModel fileManager = new(Guid.NewGuid(), fileSystem, new IdleFileTracker());
        fileManager.CurrentPath = "/var/log";
        fileManager.IsOnLeft = true;
        fileManager.IsTransferring = true;
        fileManager.TransferProgress = 42;
        fileManager.Items.Add(new RemoteFileItem("syslog", "/var/log/syslog", false, 12, DateTimeOffset.UnixEpoch, "-rw-r--r--"));
        FileTransferTaskItemViewModel transfer = new("syslog", "/tmp/syslog", "/var/log/syslog", FileTransferDirection.Download)
        {
            Progress = 42,
            State = FileTransferState.Transferring
        };
        fileManager.TransferTasks.Add(transfer);
        tab.FileManager = fileManager;
        tab.IsFileManagerVisible = true;
        tab.IsFileManagerOnLeft = true;
        return fileManager;
    }

    private static CloseCounter Listen(WorkspaceCoordinator coordinator)
    {
        CloseCounter counter = new(coordinator);
        coordinator.Messenger.Register<CloseCounter, WorkspaceCloseRequestedMessage>(counter, (recipient, message) =>
        {
            if (message.Source == recipient.Source)
            {
                recipient.Count++;
            }
        });
        return counter;
    }

    private static ActiveCounter ListenActive(WorkspaceCoordinator coordinator)
    {
        ActiveCounter counter = new(coordinator);
        coordinator.Messenger.Register<ActiveCounter, WorkspaceActiveTabChangedMessage>(counter, (recipient, message) =>
        {
            if (message.Source != recipient.Source)
            {
                return;
            }

            recipient.Count++;
            recipient.LastSource = message.Source;
            recipient.LastTab = message.Tab;
        });
        return counter;
    }

    private sealed class CloseCounter : IDisposable
    {
        public CloseCounter(WorkspaceCoordinator source)
        {
            Source = source;
        }

        public WorkspaceCoordinator Source { get; }

        public int Count { get; set; }

        public void Dispose() => Source.Messenger.UnregisterAll(this);
    }

    private sealed class ActiveCounter : IDisposable
    {
        public ActiveCounter(WorkspaceCoordinator source)
        {
            Source = source;
        }

        public WorkspaceCoordinator Source { get; }

        public int Count { get; set; }

        public WorkspaceCoordinator? LastSource { get; set; }

        public TerminalTabViewModel? LastTab { get; set; }

        public void Dispose() => Source.Messenger.UnregisterAll(this);
    }

    private sealed class CloseSink
    {
        public int Count { get; set; }

        public TerminalTabViewModel? LastTab { get; set; }
    }

    private sealed class RecordingFileSystem : IRemoteFileSystem
    {
        public bool Disposed { get; private set; }

        public bool IsConnected => true;

        public string WorkingDirectory => "/home/kei";

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RemoteFileItem>>(Array.Empty<RemoteFileItem>());

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(Stream.Null);

        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(Stream.Null);

        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
            => Task.FromResult<RemoteFileItem?>(null);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IdleFileTracker : ILocalFileTracker
    {
        public event EventHandler<LocalFileChangedEventArgs>? FileChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<string>? FileUntracked
        {
            add { }
            remove { }
        }

        public string GetLocalCachePath(Guid sessionId, string remotePath) => remotePath;

        public Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UnregisterTrackedFileAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class WorkspaceTree
{
    public static List<IDocumentDock> Groups(IDockable root)
    {
        List<IDocumentDock> groups = [];
        Walk(root, node =>
        {
            if (node is IDocumentDock group)
            {
                groups.Add(group);
            }
        });
        return groups;
    }

    public static TerminalWorkspaceDocument DocumentOf(IDockable root, TerminalTabViewModel tab)
    {
        TerminalWorkspaceDocument? found = null;
        Walk(root, node =>
        {
            if (node is TerminalWorkspaceDocument document && ReferenceEquals(document.Tab, tab))
            {
                found = document;
            }
        });
        Assert.NotNull(found);
        return found;
    }

    public static IDocumentDock GroupOf(IDockable root, TerminalTabViewModel tab)
    {
        TerminalWorkspaceDocument document = DocumentOf(root, tab);
        IDocumentDock? group = document.Owner as IDocumentDock;
        Assert.NotNull(group);
        return group;
    }

    public static int IndexOf(IDocumentDock group, TerminalTabViewModel tab)
    {
        IList<IDockable>? visible = group.VisibleDockables;
        Assert.NotNull(visible);
        for (int index = 0; index < visible.Count; index++)
        {
            if (visible[index] is TerminalWorkspaceDocument document && ReferenceEquals(document.Tab, tab))
            {
                return index;
            }
        }

        return -1;
    }

    public static TerminalTabViewModel[] Tabs(IDocumentDock group)
    {
        Assert.NotNull(group.VisibleDockables);
        return group.VisibleDockables.OfType<TerminalWorkspaceDocument>().Select(document => document.Tab).ToArray();
    }

    public static bool Contains(IDockable root, IDockable target)
    {
        bool found = false;
        Walk(root, node =>
        {
            if (ReferenceEquals(node, target))
            {
                found = true;
            }
        });
        return found;
    }

    public static bool ContainsTab(IDockable root, TerminalTabViewModel tab)
    {
        bool found = false;
        Walk(root, node =>
        {
            if (node is TerminalWorkspaceDocument document && ReferenceEquals(document.Tab, tab))
            {
                found = true;
            }
        });
        return found;
    }

    private static void Walk(IDockable node, Action<IDockable> visit)
    {
        visit(node);
        if (node is not IDock dock || dock.VisibleDockables == null)
        {
            return;
        }

        foreach (IDockable child in dock.VisibleDockables)
        {
            Walk(child, visit);
        }
    }
}
