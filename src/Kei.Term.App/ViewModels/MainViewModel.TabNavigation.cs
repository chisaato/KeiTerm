using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Kei.Term.App.Workspaces;

namespace Kei.Term.App.ViewModels;

public partial class MainViewModel
{
    private ViewModelBase[] GetNavigationTabs()
    {
        // Dock 拖拽不会同步 AllItems 的顺序；以当前分组实际显示的标签顺序为准。
        if (ActiveWorkspaceTab is { } active
            && Workspace.FindDocument(active) is { Owner: IDocumentDock { VisibleDockables: { } items } })
            return items.OfType<WorkspaceDocument>().Select(document => document.Item).ToArray();
        return WorkspaceTabs.ToArray();
    }

    [RelayCommand]
    private void SelectWorkspaceTabByIndex(int index)
    {
        ViewModelBase[] tabs = GetNavigationTabs();
        if (index >= 0 && index < tabs.Length) SelectWorkspaceTab(tabs[index]);
    }

    [RelayCommand]
    private void MoveWorkspaceTabSelection(int direction)
    {
        ViewModelBase[] tabs = GetNavigationTabs();
        if (tabs.Length == 0) return;
        int current = Array.IndexOf(tabs, ActiveWorkspaceTab);
        int next = current < 0 ? (direction < 0 ? tabs.Length - 1 : 0)
            : (current + Math.Sign(direction) + tabs.Length) % tabs.Length;
        SelectWorkspaceTab(tabs[next]);
    }
}
