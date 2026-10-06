namespace Kei.Term.App.Workspaces;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;
using Kei.Term.App.ViewModels;

// 活动连接、迁移与关闭路由。Dock 树是唯一拓扑，AllTabs 只是打开实例全集。
public sealed class WorkspaceCoordinator : IDisposable
{
    private readonly IMessenger _messenger;
    private readonly Dictionary<TerminalTabViewModel, TerminalWorkspaceDocument> _documents = new();
    private readonly HashSet<TerminalTabViewModel> _closeRouted = new();
    private bool _suppressActiveSync;
    private bool _disposed;

    public WorkspaceCoordinator()
        : this(WeakReferenceMessenger.Default)
    {
    }

    public WorkspaceCoordinator(IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        _messenger = messenger;
        Factory = new TerminalWorkspaceFactory();
        IRootDock? created = Factory.CreateLayout();
        if (created == null)
        {
            throw new InvalidOperationException("工作区布局创建失败。");
        }

        Layout = created;
        AllTabs = new ObservableCollection<TerminalTabViewModel>();
        Factory.ActiveDockableChanged += OnActiveDockableChanged;
        Factory.DockableClosing += OnDockableClosing;
        Factory.DockableDocked += OnDockableDocked;
        Factory.InitLayout(Layout);
    }

    public TerminalWorkspaceFactory Factory { get; }

    public IRootDock Layout { get; }

    public ObservableCollection<TerminalTabViewModel> AllTabs { get; }

    public IMessenger Messenger => _messenger;

    public TerminalTabViewModel? ActiveTab { get; private set; }

    public void AddTab(TerminalTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_disposed || _documents.ContainsKey(tab))
        {
            return;
        }

        IDocumentDock group = ActiveGroup() ?? CreateGroup();
        TerminalWorkspaceDocument document = new(tab);
        _documents.Add(tab, document);
        AllTabs.Add(tab);
        Factory.AddDockable(group, document);
        group.ActiveDockable = document;
        SetActiveTab(tab);
    }

    public void Activate(TerminalTabViewModel tab)
    {
        if (_disposed || tab == null || !_documents.TryGetValue(tab, out TerminalWorkspaceDocument? document))
        {
            return;
        }

        if (document.Owner is not IDock owner)
        {
            return;
        }

        if (!ReferenceEquals(owner.ActiveDockable, document))
        {
            owner.ActiveDockable = document;
        }

        SetActiveTab(tab);
    }

    public void MoveTab(TerminalTabViewModel tab, IDocumentDock destination, int index)
    {
        if (_disposed || tab == null || destination == null)
        {
            return;
        }

        if (!_documents.TryGetValue(tab, out TerminalWorkspaceDocument? document))
        {
            return;
        }

        if (document.Owner is not IDock source || !Contains(Layout, destination))
        {
            return;
        }

        int count = CountConnections(destination);
        if (ReferenceEquals(source, destination))
        {
            ReorderWithin(destination, document, index, count);
            return;
        }

        if (index < 0 || index > count)
        {
            return;
        }

        if (count == 0)
        {
            Factory.MoveDockable(source, destination, document, null);
            return;
        }

        if (index == 0)
        {
            // 跨组 MoveDockable 会插到目标项之后，不能直接表示 0。先放入再组内前移。
            IDockable? first = DockableAt(destination, 0);
            if (first == null)
            {
                return;
            }

            Factory.MoveDockable(source, destination, document, first);
            if (destination.VisibleDockables is { Count: > 0 } visible && !ReferenceEquals(visible[0], document))
            {
                Factory.MoveDockable(destination, document, visible[0]);
            }

            return;
        }

        IDockable? anchor = DockableAt(destination, index - 1);
        if (anchor == null)
        {
            return;
        }

        Factory.MoveDockable(source, destination, document, anchor);
    }

    public void SplitTab(TerminalTabViewModel tab, IDocumentDock target, DockOperation direction)
    {
        if (_disposed || tab == null || target == null || !TerminalWorkspaceFactory.IsPaneSplit(direction))
        {
            return;
        }

        if (!_documents.TryGetValue(tab, out TerminalWorkspaceDocument? document) || !Contains(Layout, target))
        {
            return;
        }

        Factory.SplitToDock(target, document, direction);
    }

    public void RemoveTab(TerminalTabViewModel tab)
    {
        if (_disposed || tab == null || !_documents.Remove(tab, out TerminalWorkspaceDocument? document))
        {
            return;
        }

        AllTabs.Remove(tab);
        bool wasActive = ReferenceEquals(ActiveTab, tab);
        IDocumentDock? group = document.Owner as IDocumentDock;
        int index = IndexOf(group, document);
        document.Detach();
        if (document.Owner is IDock)
        {
            // 只改拓扑。不走 CloseDockable，避免迁移/重复移除再发关闭消息。
            Factory.RemoveDockable(document, collapse: true);
        }

        if (tab.IsSelected)
        {
            tab.IsSelected = false;
        }

        if (!wasActive)
        {
            return;
        }

        TerminalTabViewModel? next = Neighbour(group, index) ?? FirstRemaining();
        SetActiveTab(next);
    }

    public void ReorderTab(TerminalTabViewModel tab, int index)
    {
        if (_disposed || tab == null || !_documents.TryGetValue(tab, out TerminalWorkspaceDocument? document))
        {
            return;
        }

        if (document.Owner is not IDocumentDock group)
        {
            return;
        }

        int count = CountConnections(group);
        if (index < 0 || index >= count)
        {
            return;
        }

        int current = IndexOf(group, document);
        if (current < 0 || current == index)
        {
            return;
        }

        IDockable? target = DockableAt(group, index);
        if (target == null)
        {
            return;
        }

        IDockable? previousGroupActive = group.ActiveDockable;
        _suppressActiveSync = true;
        try
        {
            Factory.MoveDockable(group, document, target);
            if (previousGroupActive != null && group.VisibleDockables?.Contains(previousGroupActive) == true)
            {
                group.ActiveDockable = previousGroupActive;
            }
        }
        finally
        {
            _suppressActiveSync = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Factory.ActiveDockableChanged -= OnActiveDockableChanged;
        Factory.DockableClosing -= OnDockableClosing;
        Factory.DockableDocked -= OnDockableDocked;
        foreach (TerminalWorkspaceDocument document in _documents.Values)
        {
            document.Detach();
        }
    }

    private void OnActiveDockableChanged(object? sender, ActiveDockableChangedEventArgs e)
    {
        if (_suppressActiveSync || _disposed)
        {
            return;
        }

        // 非连接节点（含 null）不清空活动项，避免焦点进入共享工具栏后丢失目标。
        if (e.Dockable is TerminalWorkspaceDocument document && _documents.ContainsKey(document.Tab))
        {
            SetActiveTab(document.Tab);
        }
    }

    private void OnDockableDocked(object? sender, DockableDockedEventArgs e)
    {
        if (_suppressActiveSync || _disposed)
        {
            return;
        }

        if (e.Dockable is TerminalWorkspaceDocument document && _documents.ContainsKey(document.Tab))
        {
            SetActiveTab(document.Tab);
            return;
        }

        if (e.Dockable is IDocumentDock group
            && group.ActiveDockable is TerminalWorkspaceDocument active
            && _documents.ContainsKey(active.Tab))
        {
            SetActiveTab(active.Tab);
        }
    }

    private void OnDockableClosing(object? sender, DockableClosingEventArgs e)
    {
        if (e.Dockable is not TerminalWorkspaceDocument document)
        {
            return;
        }

        // 取消 Dock 自己的关闭，改由应用关闭命令调用 RemoveTab。空组收拢不走这条路径。
        e.Cancel = true;
        if (_disposed || !_documents.ContainsKey(document.Tab) || !_closeRouted.Add(document.Tab))
        {
            return;
        }

        try
        {
            _messenger.Send(new WorkspaceCloseRequestedMessage(this, document.Tab));
        }
        finally
        {
            _closeRouted.Remove(document.Tab);
        }
    }

    private void SetActiveTab(TerminalTabViewModel? tab)
    {
        if (_disposed)
        {
            return;
        }

        if (tab != null && !_documents.ContainsKey(tab))
        {
            return;
        }

        if (ReferenceEquals(ActiveTab, tab))
        {
            if (tab != null && !tab.IsSelected)
            {
                tab.IsSelected = true;
            }

            return;
        }

        TerminalTabViewModel? previous = ActiveTab;
        ActiveTab = tab;
        foreach (TerminalTabViewModel open in AllTabs)
        {
            open.IsSelected = ReferenceEquals(open, tab);
        }

        if (previous != null && !ReferenceEquals(previous, tab))
        {
            previous.IsSelected = false;
        }

        _messenger.Send(new WorkspaceActiveTabChangedMessage(this, tab));
    }

    private void ReorderWithin(IDocumentDock dock, TerminalWorkspaceDocument document, int index, int count)
    {
        if (index < 0 || index >= count)
        {
            return;
        }

        int current = IndexOf(dock, document);
        if (current < 0 || current == index)
        {
            return;
        }

        IDockable? target = DockableAt(dock, index);
        if (target == null)
        {
            return;
        }

        Factory.MoveDockable(dock, document, target);
    }

    private IDocumentDock? ActiveGroup()
    {
        if (ActiveTab != null
            && _documents.TryGetValue(ActiveTab, out TerminalWorkspaceDocument? document)
            && document.Owner is IDocumentDock owner
            && Contains(Layout, owner))
        {
            return owner;
        }

        IDocumentDock? found = null;
        Walk(Layout, node =>
        {
            if (found == null && node is IDocumentDock group)
            {
                found = group;
            }
        });
        return found;
    }

    private IDocumentDock CreateGroup()
    {
        IDocumentDock group = Factory.CreateDocumentDock();
        group.Id = "group-" + Guid.NewGuid().ToString("N");
        group.Title = "Connections";
        group.VisibleDockables = Factory.CreateList<IDockable>();
        Factory.AddDockable(Layout, group);
        return group;
    }

    private TerminalTabViewModel? Neighbour(IDocumentDock? group, int removedIndex)
    {
        if (group == null || !Contains(Layout, group) || group.VisibleDockables == null || group.VisibleDockables.Count == 0)
        {
            return null;
        }

        if (group.ActiveDockable is TerminalWorkspaceDocument active && _documents.ContainsKey(active.Tab))
        {
            return active.Tab;
        }

        int fallback = removedIndex > 0 ? removedIndex - 1 : 0;
        if (fallback >= group.VisibleDockables.Count)
        {
            fallback = group.VisibleDockables.Count - 1;
        }

        if (group.VisibleDockables[fallback] is TerminalWorkspaceDocument document && _documents.ContainsKey(document.Tab))
        {
            return document.Tab;
        }

        return null;
    }

    private TerminalTabViewModel? FirstRemaining()
    {
        TerminalTabViewModel? found = null;
        Walk(Layout, node =>
        {
            if (found != null)
            {
                return;
            }

            if (node is IDocumentDock group
                && group.ActiveDockable is TerminalWorkspaceDocument active
                && _documents.ContainsKey(active.Tab))
            {
                found = active.Tab;
                return;
            }

            if (node is TerminalWorkspaceDocument document && _documents.ContainsKey(document.Tab))
            {
                found = document.Tab;
            }
        });
        return found;
    }

    private static int CountConnections(IDock dock)
    {
        if (dock.VisibleDockables == null)
        {
            return 0;
        }

        int count = 0;
        foreach (IDockable item in dock.VisibleDockables)
        {
            if (item is TerminalWorkspaceDocument)
            {
                count++;
            }
        }

        return count;
    }

    private static int IndexOf(IDock? dock, TerminalWorkspaceDocument document)
    {
        if (dock?.VisibleDockables == null)
        {
            return -1;
        }

        return dock.VisibleDockables.IndexOf(document);
    }

    private static IDockable? DockableAt(IDock dock, int index)
    {
        if (dock.VisibleDockables == null || index < 0 || index >= dock.VisibleDockables.Count)
        {
            return null;
        }

        return dock.VisibleDockables[index];
    }

    private static bool Contains(IDockable root, IDockable target)
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

    private static void Walk(IDockable node, Action<IDockable> visit)
    {
        visit(node);
        if (node is not IDock dock || dock.VisibleDockables == null)
        {
            return;
        }

        foreach (IDockable child in dock.VisibleDockables.ToArray())
        {
            Walk(child, visit);
        }
    }
}
