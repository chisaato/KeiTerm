using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Workspaces;

namespace Kei.Term.Tests;

public class TabReorderingTests
{
    [Fact]
    public void MoveTab_ReordersTabsCorrectly_AndPreservesSelectedTab()
    {
        using WorkspaceCoordinator coordinator = new(new WeakReferenceMessenger());
        TerminalTabViewModel tab1 = new("Tab 1", "monospace", 14.0);
        TerminalTabViewModel tab2 = new("Tab 2", "monospace", 14.0);
        TerminalTabViewModel tab3 = new("Tab 3", "monospace", 14.0);
        coordinator.AddTab(tab1);
        coordinator.AddTab(tab2);
        coordinator.AddTab(tab3);
        coordinator.Activate(tab1);
        IDocumentDock group = WorkspaceTree.GroupOf(coordinator.Layout, tab1);

        coordinator.ReorderTab(tab1, 2);

        Assert.Equal(new[] { tab2, tab3, tab1 }, WorkspaceTree.Tabs(group));
        Assert.Same(tab1, coordinator.ActiveTab);
        Assert.True(tab1.IsSelected);
        Assert.False(tab2.IsSelected);
        Assert.False(tab3.IsSelected);
        Assert.Same(tab1, ((TerminalWorkspaceDocument)group.ActiveDockable!).Tab);
        Assert.False(tab1.IsDisposed);
        Assert.False(tab2.IsDisposed);
        Assert.False(tab3.IsDisposed);
    }
}
