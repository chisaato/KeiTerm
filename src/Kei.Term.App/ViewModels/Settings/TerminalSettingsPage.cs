using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;

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

    // 标签标题跟随远端（OSC 0/2）标题的全局默认；会话可覆盖
    [ObservableProperty]
    private bool _tabTitleFollowsRemote = true;

    // 文件侧栏跟随终端目录的全局默认；会话可覆盖
    public IReadOnlyList<CwdFollowOption> CwdFollowModes => SessionBehaviorOptions.CwdFollowModes;

    [ObservableProperty]
    private CwdFollowOption? _selectedCwdFollow = SessionBehaviorOptions.CwdFollowModes[0];

    public void SetCwdFollow(CwdFollowMode mode)
        => SelectedCwdFollow = CwdFollowModes.FirstOrDefault(o => o.Mode == mode) ?? CwdFollowModes[0];
}
