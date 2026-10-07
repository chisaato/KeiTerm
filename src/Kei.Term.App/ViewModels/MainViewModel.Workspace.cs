using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kei.Term.App.ViewModels;

// 工作区包含未连接的启动页与真实终端；终端生命周期仍由 Tabs 管理。
public partial class MainViewModel
{
    public ObservableCollection<ViewModelBase> WorkspaceTabs { get; } = [];
    public ObservableCollection<NewTabViewModel> NewTabs { get; } = [];
    private readonly AsyncLocal<NewTabViewModel?> _launchingTab = new();
    private bool _movingWorkspaceTab;

    [ObservableProperty] private ViewModelBase? _activeWorkspaceTab;

    partial void OnActiveWorkspaceTabChanged(ViewModelBase? value)
    {
        foreach (NewTabViewModel tab in NewTabs) tab.IsSelected = tab == value;
        SelectedTab = value as TerminalTabViewModel;
    }

    [RelayCommand]
    private void NewTab()
    {
        NewTabViewModel tab = new(this);
        NewTabs.Add(tab);
        WorkspaceTabs.Add(tab);
        HasTabs = true;
        ActiveWorkspaceTab = tab;
    }

    [RelayCommand]
    private void SelectWorkspaceTab(ViewModelBase? tab)
    {
        if (tab != null && WorkspaceTabs.Contains(tab)) ActiveWorkspaceTab = tab;
    }

    [RelayCommand]
    private Task CloseWorkspaceTabAsync(ViewModelBase? tab)
    {
        if (tab is TerminalTabViewModel terminal) return CloseTabCommand.ExecuteAsync(terminal);
        if (tab is NewTabViewModel starter)
        {
            NewTabs.Remove(starter);
            RemoveWorkspaceTab(starter);
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CloseCurrentWorkspaceTabAsync()
    {
        // 确认框优先接收 ⌘W，不能关闭它背后的标签页。
        if (await Interaction.HandleCloseShortcutInQuitConfirmationAsync()) return;
        ViewModelBase? tab = ActiveWorkspaceTab ?? WorkspaceTabs.LastOrDefault();
        if (tab != null) await CloseWorkspaceTabCommand.ExecuteAsync(tab);
        if (WorkspaceTabs.Count == 0) await Interaction.CloseWindowAsync();
    }

    private void RemoveWorkspaceTab(ViewModelBase tab)
    {
        int index = WorkspaceTabs.IndexOf(tab);
        if (index < 0) return;
        bool active = ActiveWorkspaceTab == tab;
        WorkspaceTabs.RemoveAt(index);
        if (active) ActiveWorkspaceTab = WorkspaceTabs.Count == 0 ? null : WorkspaceTabs[Math.Clamp(index - 1, 0, WorkspaceTabs.Count - 1)];
        HasTabs = WorkspaceTabs.Count > 0;
    }

    private void OnTerminalTabsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_movingWorkspaceTab) return;
        if (args.Action == NotifyCollectionChangedAction.Move && args.NewItems?[0] is TerminalTabViewModel moved)
        {
            TerminalTabViewModel? neighbor = args.NewStartingIndex == 0 ? Tabs.Skip(1).FirstOrDefault() : Tabs[args.NewStartingIndex - 1];
            if (neighbor != null)
            {
                WorkspaceTabs.Remove(moved);
                WorkspaceTabs.Insert(WorkspaceTabs.IndexOf(neighbor) + (args.NewStartingIndex == 0 ? 0 : 1), moved);
            }
        }
        else
        {
            if (args.OldItems != null)
                foreach (TerminalTabViewModel tab in args.OldItems) RemoveWorkspaceTab(tab);
            if (args.NewItems != null)
                foreach (TerminalTabViewModel tab in args.NewItems) WorkspaceTabs.Add(tab);
            if (args.Action == NotifyCollectionChangedAction.Reset)
                foreach (TerminalTabViewModel tab in WorkspaceTabs.OfType<TerminalTabViewModel>().ToList()) RemoveWorkspaceTab(tab);
        }
        HasTabs = WorkspaceTabs.Count > 0;
    }

    public void MoveWorkspaceTab(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || toIndex < 0 || fromIndex >= WorkspaceTabs.Count || toIndex >= WorkspaceTabs.Count) return;
        WorkspaceTabs.Move(fromIndex, toIndex);
        _movingWorkspaceTab = true;
        try
        {
            TerminalTabViewModel[] order = WorkspaceTabs.OfType<TerminalTabViewModel>().ToArray();
            for (int index = 0; index < order.Length; index++)
                if (Tabs[index] != order[index]) Tabs.Move(Tabs.IndexOf(order[index]), index);
        }
        finally { _movingWorkspaceTab = false; }
    }

    internal async Task ExecuteFromNewTabAsync(NewTabViewModel tab, IAsyncRelayCommand command)
    {
        NewTabViewModel? previous = _launchingTab.Value;
        _launchingTab.Value = tab;
        try { await command.ExecuteAsync(null); }
        finally { _launchingTab.Value = previous; }
    }

    private async Task RunConnectionFromWorkspaceAsync(Func<Task> connect)
    {
        NewTabViewModel? previous = _launchingTab.Value;
        _launchingTab.Value ??= ActiveWorkspaceTab as NewTabViewModel;
        try { await connect(); }
        finally { _launchingTab.Value = previous; }
    }

    private void ReplaceLaunchingTab(TerminalTabViewModel terminal)
    {
        NewTabViewModel? starter = _launchingTab.Value;
        if (starter == null || !WorkspaceTabs.Contains(starter)) return;
        int index = WorkspaceTabs.IndexOf(starter);
        WorkspaceTabs.Remove(terminal);
        WorkspaceTabs[index] = terminal;
        NewTabs.Remove(starter);
        ActiveWorkspaceTab = terminal;
        _launchingTab.Value = null;
    }
}
