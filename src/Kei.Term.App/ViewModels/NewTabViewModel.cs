using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;

namespace Kei.Term.App.ViewModels;

// 启动页是真正的工作区标签，尚未创建终端或发起连接。
public partial class NewTabViewModel : ViewModelBase
{
    public NewTabViewModel(MainViewModel owner, bool isWelcome = false)
    {
        IsWelcome = isWelcome;
        QuickConnectCommand = new AsyncRelayCommand(() => owner.ExecuteFromNewTabAsync(this, owner.QuickConnectCommand));
        ConnectSavedSessionCommand = new AsyncRelayCommand(() => owner.ExecuteFromNewTabAsync(this, owner.ConnectSavedSessionCommand));
        CreateSessionCommand = new AsyncRelayCommand(() => owner.ExecuteFromNewTabAsync(this, owner.CreateSessionCommand));
        RequestCloseCommand = new AsyncRelayCommand(() => owner.CloseWorkspaceTabCommand.ExecuteAsync(this));
    }

    public string Title => Strings.Get("Menu.File.NewTab");
    public string ToolTip => Title;
    public bool IsWelcome { get; }
    public string NewSessionShortcutLabel => AppShortcuts.Label(AppShortcuts.NewSession);
    public string QuickConnectShortcutLabel => AppShortcuts.Label(AppShortcuts.QuickConnect);
    public string ConnectSavedSessionShortcutLabel => AppShortcuts.Label(AppShortcuts.ConnectSavedSession);
    public IAsyncRelayCommand QuickConnectCommand { get; }
    public IAsyncRelayCommand ConnectSavedSessionCommand { get; }
    public IAsyncRelayCommand CreateSessionCommand { get; }
    public IAsyncRelayCommand RequestCloseCommand { get; }
    [ObservableProperty] private bool _isSelected;
}
