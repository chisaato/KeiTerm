namespace Kei.Term.App.Workspaces;

using System;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

// Dock 树是唯一分屏拓扑。新组一律拒绝浮动/固定，且不用更高优先级的 allow 覆盖 root。
public sealed class TerminalWorkspaceFactory : Factory
{
    private static readonly DockOperationMask PaneOperations =
        DockOperationMask.Fill
        | DockOperationMask.Left
        | DockOperationMask.Right
        | DockOperationMask.Top
        | DockOperationMask.Bottom;

    public override IRootDock CreateRootDock()
    {
        IRootDock root = base.CreateRootDock();
        Lock(root);
        root.IsCollapsable = false;
        root.CanClose = false;
        root.RootDockCapabilityPolicy = DenyFloatAndPin();
        return root;
    }

    public override IDocumentDock CreateDocumentDock()
    {
        IDocumentDock dock = base.CreateDocumentDock();
        Lock(dock);
        dock.IsCollapsable = true;
        dock.CanClose = false;
        dock.CanCreateDocument = false;
        dock.EnableWindowDrag = false;
        return dock;
    }

    public override IProportionalDock CreateProportionalDock()
    {
        IProportionalDock dock = base.CreateProportionalDock();
        Lock(dock);
        dock.CanClose = false;
        return dock;
    }

    public override IRootDock CreateLayout()
    {
        IDocumentDock documents = CreateDocumentDock();
        documents.Id = "connections";
        documents.Title = "Connections";
        documents.VisibleDockables = CreateList<IDockable>();

        IRootDock root = CreateRootDock();
        root.Id = "workspace-root";
        root.Title = "Workspace";
        root.VisibleDockables = CreateList<IDockable>(documents);
        root.DefaultDockable = documents;
        root.ActiveDockable = documents;
        return root;
    }

    public override void SplitToDock(IDock dock, IDockable dockable, DockOperation operation)
    {
        if (!IsPaneSplit(operation))
        {
            // Window/Fill 不生成浮动窗，也不抛给拖放链路。
            return;
        }

        if (dockable is WorkspaceDocument document)
        {
            if (IsOnlyConnectionAgainstOwnGroup(document, dock))
            {
                return;
            }

            IDocumentDock group = CreateDocumentDock();
            group.Id = "group-" + Guid.NewGuid().ToString("N");
            group.Title = "Connections";
            group.VisibleDockables = CreateList<IDockable>();
            if (document.Owner is IDock)
            {
                // 先移出原组。RemoveDockable 不发连接关闭。
                RemoveDockable(document, collapse: true);
            }

            AddDockable(group, document);
            group.ActiveDockable = document;
            base.SplitToDock(dock, group, operation);
            return;
        }

        base.SplitToDock(dock, dockable, operation);
    }

    internal static bool IsPaneSplit(DockOperation operation)
    {
        return operation is DockOperation.Left or DockOperation.Right or DockOperation.Top or DockOperation.Bottom;
    }

    private static bool IsOnlyConnectionAgainstOwnGroup(WorkspaceDocument document, IDock target)
    {
        if (!ReferenceEquals(document.Owner, target))
        {
            return false;
        }

        return CountConnections(target) <= 1;
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

    private static void Lock(IDockable dockable)
    {
        dockable.CanFloat = false;
        dockable.CanPin = false;
        if (dockable is IDockableDockingRestrictions restrictions)
        {
            restrictions.AllowedDockOperations = PaneOperations;
            restrictions.AllowedDropOperations = PaneOperations;
        }
        if (dockable is IDock dock)
        {
            // 组策略优先级高于 root。这里只能拒绝，不能 allow。
            dock.DockCapabilityPolicy = DenyFloatAndPin();
        }
    }

    private static DockCapabilityPolicy DenyFloatAndPin()
    {
        return new DockCapabilityPolicy
        {
            CanFloat = false,
            CanPin = false
        };
    }
}
