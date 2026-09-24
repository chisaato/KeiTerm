using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.ViewModels.Settings;

// 「终端」分类页：回滚缓冲与终端仿真
public partial class TerminalSettingsPage : ViewModelBase
{
    // 可向上回滚查看的最大行数
    [ObservableProperty]
    private int _scrollbackLines = 5000;

    // 新会话上报的 TERM 值
    [ObservableProperty]
    private string _defaultTerminalType = "xterm-256color";
}
