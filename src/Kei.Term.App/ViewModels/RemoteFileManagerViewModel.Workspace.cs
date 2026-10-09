using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Helpers;
using Kei.Term.App.Workspaces;

namespace Kei.Term.App.ViewModels;

// 标签和侧栏共享同一个文件管理实例，文件通道始终由所属终端连接释放。
public partial class RemoteFileManagerViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseToolTip))]
    private bool _isWorkspaceTab;

    public string CloseToolTip => Strings.Get(IsWorkspaceTab ? "Menu.File.CloseTab" : "FileManager.CloseSidebar");

    [RelayCommand]
    public void PromoteToTab() => WeakReferenceMessenger.Default.Send(new FileManagerTabRequestedMessage(this));

    [RelayCommand]
    public void ShowShell() => WeakReferenceMessenger.Default.Send(new FileManagerShellRequestedMessage(this));

    [RelayCommand]
    public void Close()
    {
        if (IsWorkspaceTab)
            WeakReferenceMessenger.Default.Send(new FileManagerTabCloseRequestedMessage(this));
        else
            CloseRequested?.Invoke();
    }
}
