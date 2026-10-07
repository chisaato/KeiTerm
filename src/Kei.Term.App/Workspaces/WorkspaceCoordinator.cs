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
    private readonly Dictionary<ViewModelBase, WorkspaceDocument> _documents = new();
    private readonly HashSet<ViewModelBase> _closeRouted = new();
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

    public ViewModelBase? ActiveItem { get; private set; }

    public TerminalTabViewModel? ActiveTab => ActiveItem as TerminalTabViewModel;

    public ObservableCollection<ViewModelBase> AllItems { get; } = [];

    public WorkspaceDocument? FindDocument(ViewModelBase item)
        => _documents.GetValueOrDefault(item);

    private static void SetSelected(ViewModelBase item, bool selected)
    {
        if (item is TerminalTabViewModel terminal) terminal.IsSelected = selected;
        else if (item is NewTabViewModel starter) starter.IsSelected = selected;
    }

    // 连接对话框期间可能切到另一组，仍须替换发起连接的启动页原位置。
    public void ReplaceTab(NewTabViewModel starter, TerminalTabViewModel terminal)
    {
        if (!_documents.TryGetValue(starter, out WorkspaceDocument? source)
            || source.Owner is not IDocumentDock group || !_documents.ContainsKey(terminal)) return;
        int position = IndexOf(group, source);
        int listIndex = AllItems.IndexOf(starter);
        MoveTab(terminal, group, position);
        RemoveTab(starter);
        ReorderTab(terminal, position);
        AllItems.Move(AllItems.IndexOf(terminal), listIndex);
        Activate(terminal);
    }

    public void AddTab(ViewModelBase tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_disposed || _documents.ContainsKey(tab))
        {
            return;
        }

        IDocumentDock group = ActiveGroup() ?? CreateGroup();
        WorkspaceDocument document = tab switch
        {
            TerminalTabViewModel terminal => new TerminalWorkspaceDocument(terminal),
            NewTabViewModel starter => new NewTabWorkspaceDocument(starter),
            _ => throw new ArgumentException("Unsupported workspace tab.", nameof(tab))
        };
        _documents.Add(tab, document);
        if (tab is TerminalTabViewModel terminalTab) AllTabs.Add(terminalTab);
        AllItems.Add(tab);
        Factory.AddDockable(group, document);
        group.ActiveDockable = document;
        SetActiveItem(tab);
    }

    public void Activate(ViewModelBase tab)
    {
        if (_disposed || tab == null || !_documents.TryGetValue(tab, out WorkspaceDocument? document))
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

        SetActiveItem(tab);
    }

    public void MoveTab(ViewModelBase tab, IDocumentDock destination, int index)
    {
        if (_disposed || tab == null || destination == null)
        {
            return;
        }

        if (!_documents.TryGetValue(tab, out WorkspaceDocument? document))
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

    public void SplitTab(ViewModelBase tab, IDocumentDock target, DockOperation direction)
    {
        if (_disposed || tab == null || target == null || !TerminalWorkspaceFactory.IsPaneSplit(direction))
        {
            return;
        }

        if (!_documents.TryGetValue(tab, out WorkspaceDocument? document) || !Contains(Layout, target))
        {
            return;
        }

        Factory.SplitToDock(target, document, direction);
    }

    public void RemoveTab(ViewModelBase tab)
    {
        if (_disposed || tab == null || !_documents.Remove(tab, out WorkspaceDocument? document))
        {
            return;
        }

        if (tab is TerminalTabViewModel terminalTab) AllTabs.Remove(terminalTab);
        AllItems.Remove(tab);
        bool wasActive = ReferenceEquals(ActiveItem, tab);
        IDocumentDock? group = document.Owner as IDocumentDock;
        int index = IndexOf(group, document);
        document.Detach();
        if (document.Owner is IDock)
        {
            // 只改拓扑。不走 CloseDockable，避免迁移/重复移除再发关闭消息。
            Factory.RemoveDockable(document, collapse: true);
        }

        SetSelected(tab, false);

        if (!wasActive)
        {
            return;
        }

        ViewModelBase? next = Neighbour(group, index) ?? FirstRemaining();
        SetActiveItem(next);
    }

    public void ReorderTab(ViewModelBase tab, int index)
    {
        if (_disposed || tab == null || !_documents.TryGetValue(tab, out WorkspaceDocument? document))
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
        foreach (WorkspaceDocument document in _documents.Values)
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
        if (e.Dockable is WorkspaceDocument document && _documents.ContainsKey(document.Item))
        {
            SetActiveItem(document.Item);
        }
    }

    private void OnDockableDocked(object? sender, DockableDockedEventArgs e)
    {
        if (_suppressActiveSync || _disposed)
        {
            return;
        }

        if (e.Dockable is WorkspaceDocument document && _documents.ContainsKey(document.Item))
        {
            SetActiveItem(document.Item);
            return;
        }

        if (e.Dockable is IDocumentDock group
            && group.ActiveDockable is WorkspaceDocument active
            && _documents.ContainsKey(active.Item))
        {
            SetActiveItem(active.Item);
        }
    }

    private void OnDockableClosing(object? sender, DockableClosingEventArgs e)
    {
        if (e.Dockable is not WorkspaceDocument document)
        {
            return;
        }

        // 取消 Dock 自己的关闭，改由应用关闭命令调用 RemoveTab。空组收拢不走这条路径。
        e.Cancel = true;
        if (_disposed || !_documents.ContainsKey(document.Item) || !_closeRouted.Add(document.Item))
        {
            return;
        }

        try
        {
            if (document.Item is TerminalTabViewModel terminal)
                _messenger.Send(new WorkspaceCloseRequestedMessage(this, terminal));
            _messenger.Send(new WorkspaceItemCloseRequestedMessage(this, document.Item));
        }
        finally
        {
            _closeRouted.Remove(document.Item);
        }
    }

    private void SetActiveItem(ViewModelBase? tab)
    {
        if (_disposed)
        {
            return;
        }

        if (tab != null && !_documents.ContainsKey(tab))
        {
            return;
        }

        if (ReferenceEquals(ActiveItem, tab))
        {
            if (tab != null) SetSelected(tab, true);

            return;
        }

        ViewModelBase? previous = ActiveItem;
        ActiveItem = tab;
        foreach (ViewModelBase open in AllItems)
        {
            SetSelected(open, ReferenceEquals(open, tab));
        }

        if (previous != null && !ReferenceEquals(previous, tab))
        {
            SetSelected(previous, false);
        }

        _messenger.Send(new WorkspaceActiveTabChangedMessage(this, ActiveTab));
        _messenger.Send(new WorkspaceActiveItemChangedMessage(this, tab));
    }

    private void ReorderWithin(IDocumentDock dock, WorkspaceDocument document, int index, int count)
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
        if (ActiveItem != null
            && _documents.TryGetValue(ActiveItem, out WorkspaceDocument? document)
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
        // 最后一组关闭后 root 的活动项已被清空；只添加分组不会让内容重新呈现。
        Layout.DefaultDockable = group;
        Layout.ActiveDockable = group;
        return group;
    }

    private ViewModelBase? Neighbour(IDocumentDock? group, int removedIndex)
    {
        if (group == null || !Contains(Layout, group) || group.VisibleDockables == null || group.VisibleDockables.Count == 0)
        {
            return null;
        }

        if (group.ActiveDockable is WorkspaceDocument active && _documents.ContainsKey(active.Item))
        {
            return active.Item;
        }

        int fallback = removedIndex > 0 ? removedIndex - 1 : 0;
        if (fallback >= group.VisibleDockables.Count)
        {
            fallback = group.VisibleDockables.Count - 1;
        }

        if (group.VisibleDockables[fallback] is WorkspaceDocument document && _documents.ContainsKey(document.Item))
        {
            return document.Item;
        }

        return null;
    }

    private ViewModelBase? FirstRemaining()
    {
        ViewModelBase? found = null;
        Walk(Layout, node =>
        {
            if (found != null)
            {
                return;
            }

            if (node is IDocumentDock group
                && group.ActiveDockable is WorkspaceDocument active
                && _documents.ContainsKey(active.Item))
            {
                found = active.Item;
                return;
            }

            if (node is WorkspaceDocument document && _documents.ContainsKey(document.Item))
            {
                found = document.Item;
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
            if (item is WorkspaceDocument)
            {
                count++;
            }
        }

        return count;
    }

    private static int IndexOf(IDock? dock, WorkspaceDocument document)
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
