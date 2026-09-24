using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.ViewModels;

// 快速连接窗口 VM：仅收集输入与校验，保存/连接善后由 MainViewModel.ConnectQuickAsync 完成
public partial class QuickConnectViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _host = string.Empty;

    // 端口默认取全局设置
    [ObservableProperty]
    private int _port = 22;

    // 用户名预填：全局默认用户名，空白再回退当前系统用户
    [ObservableProperty]
    private string _username = string.Empty;

    // 密码可选，仅本次连接使用，不落库
    [ObservableProperty]
    private string _password = string.Empty;

    // 是否保存为会话（默认不勾）
    [ObservableProperty]
    private bool _saveAsSession;

    // 主机必填校验失败提示
    [ObservableProperty]
    private string? _errorMessage;

    public bool IsConfirmed { get; private set; }

    public event Action? RequestClose;

    public QuickConnectViewModel(AppSettings settings)
    {
        Port = settings.DefaultPort;
        Username = !string.IsNullOrWhiteSpace(settings.DefaultUsername)
            ? settings.DefaultUsername
            : Environment.UserName;
    }

    // 点连接：主机必填；通过则关闭窗口，善后由主窗口回调
    [RelayCommand]
    private void Connect()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            ErrorMessage = Strings.Get("QuickConnect.Error.HostRequired");
            return;
        }

        IsConfirmed = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        RequestClose?.Invoke();
    }
}
