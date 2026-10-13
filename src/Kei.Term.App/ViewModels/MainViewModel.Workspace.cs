using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Kei.Term.App.Workspaces;
using Dock.Model.Controls;

namespace Kei.Term.App.ViewModels;

// 工作区包含未连接的启动页与真实终端；终端生命周期仍由 Tabs 管理。
public partial class MainViewModel
{
    public ObservableCollection<ViewModelBase> WorkspaceTabs => Workspace.AllItems;
    public ObservableCollection<NewTabViewModel> NewTabs { get; } = [];
    private NewTabViewModel? _welcomePage;
    public NewTabViewModel WelcomePage => _welcomePage ??= new(this, isWelcome: true);
    private readonly AsyncLocal<NewTabViewModel?> _launchingTab = new();
    private bool _syncingFromWorkspace;

    [ObservableProperty] private ViewModelBase? _activeWorkspaceTab;

    partial void OnActiveWorkspaceTabChanged(ViewModelBase? value)
    {
        foreach (NewTabViewModel tab in NewTabs) tab.IsSelected = tab == value;
        SelectedTab = value switch
        {
            TerminalTabViewModel terminal => terminal,
            FileManagerTabViewModel files => files.Owner,
            _ => null
        };
        OnPropertyChanged(nameof(IsTerminalWorkspaceActive));
        OpenTerminalFindCommand.NotifyCanExecuteChanged();
        ToggleComposeBarCommand.NotifyCanExecuteChanged();
        SendComposeCommand.NotifyCanExecuteChanged();
        if (!_syncingFromWorkspace && value != null) Workspace.Activate(value);
    }

    [RelayCommand]
    private void NewTab()
    {
        NewTabViewModel tab = new(this);
        NewTabs.Add(tab);
        Workspace.AddTab(tab);
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
        if (tab is FileManagerTabViewModel files) CloseFileManagerTab(files);
        if (tab is NewTabViewModel starter)
        {
            NewTabs.Remove(starter);
            Workspace.RemoveTab(starter);
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CloseCurrentWorkspaceTabAsync()
    {
        // 确认框优先接收 ⌘W，不能关闭它背后的标签页。
        if (await Interaction.HandleCloseShortcutInQuitConfirmationAsync()) return;
        ViewModelBase? tab = ActiveWorkspaceTab ?? WorkspaceTabs.LastOrDefault();
        if (tab != null)
        {
            if (tab is TerminalTabViewModel terminal && terminal.CloseActiveDocument()) return;
            // 本次只关闭标签；最后一页关闭后由空工作区显示欢迎页。
            await CloseWorkspaceTabCommand.ExecuteAsync(tab);
            return;
        }

        await Interaction.CloseWindowAsync();
    }

    private void OnConnectionDocumentsChanged(object recipient, ConnectionDocumentsChangedMessage message) => _uiDispatch(() =>
    {
        if (_disposed != 0 || !Tabs.Contains(message.Tab)) return;
        if (ReferenceEquals(ActiveWorkspaceTab, message.Tab))
        {
            OnPropertyChanged(nameof(IsTerminalWorkspaceActive));
            OpenTerminalFindCommand.NotifyCanExecuteChanged();
            ToggleComposeBarCommand.NotifyCanExecuteChanged();
            SendComposeCommand.NotifyCanExecuteChanged();
        }
        if (message.CloseEmptyTab && !message.Tab.HasOpenDocuments)
            _ = CloseTabCommand.ExecuteAsync(message.Tab);
    });

    private void OnWorkspaceItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => HasTabs = WorkspaceTabs.Count > 0;

    private void OnWorkspaceActiveTabChanged(object recipient, WorkspaceActiveItemChangedMessage message)
    {
        if (!ReferenceEquals(message.Source, Workspace) || _disposed != 0) return;
        _syncingFromWorkspace = true;
        try { ActiveWorkspaceTab = message.Item; }
        finally { _syncingFromWorkspace = false; }
    }

    private void OnWorkspaceCloseRequested(object recipient, WorkspaceItemCloseRequestedMessage message)
    {
        if (!ReferenceEquals(message.Source, Workspace) || _disposed != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed == 0) _ = CloseWorkspaceTabCommand.ExecuteAsync(message.Item);
        });
    }

    public void MoveWorkspaceTab(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || toIndex < 0 || fromIndex >= WorkspaceTabs.Count || toIndex >= WorkspaceTabs.Count) return;
        ViewModelBase item = WorkspaceTabs[fromIndex];
        ViewModelBase target = WorkspaceTabs[toIndex];
        ViewModelBase? active = ActiveWorkspaceTab;
        if (Workspace.FindDocument(target) is { Owner: IDocumentDock group } document)
        {
            Workspace.MoveTab(item, group, group.VisibleDockables!.IndexOf(document));
        }
        WorkspaceTabs.Move(fromIndex, toIndex);
        TerminalTabViewModel[] order = WorkspaceTabs.OfType<TerminalTabViewModel>().ToArray();
        for (int index = 0; index < order.Length; index++)
            if (Tabs[index] != order[index]) Tabs.Move(Tabs.IndexOf(order[index]), index);
        if (active != null) Workspace.Activate(active);
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
        Workspace.ReplaceTab(starter, terminal);
        NewTabs.Remove(starter);
        ActiveWorkspaceTab = terminal;
        _launchingTab.Value = null;
    }
}
