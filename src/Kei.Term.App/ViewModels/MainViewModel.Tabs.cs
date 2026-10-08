using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Logging;
using Kei.Term.App.Services.Connection;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.ViewModels;

// 真实终端标签的创建、重排和释放。
public partial class MainViewModel
{
    // IConnectionHost：按解析后的配置新建标签（Connecting 态）并选中
    IConnectionTarget IConnectionHost.OpenTab(ResolvedSessionConfig config)
    {
        DismissFloatingSessionManager();
        // 有效配色：会话显式 ID → 全局默认 → 内置默认，始终走同一适配路径
        TerminalProfile effectiveProfile = ResolveEffectiveTerminalProfile(config.TerminalProfileId);
        var tab = new TerminalTabViewModel(
            config.SessionName,
            BuildAppliedFontSnapshot(_settingsService.Current),
            effectiveProfile,
            config.TerminalProfileId,
            _logger,
            scrollbackLines: _settingsService.Current.ScrollbackLines);
        // 构造只记录状态，此处显式注入配色（新标签立即生效）
        tab.ApplyTerminalProfile(effectiveProfile);
        tab.BindConfig(config);
        tab.CloseRequested += OnTabCloseRequested;
        tab.AutoReconnectEnabled = () => _settingsService.Current.AutoReconnectOnDisconnect;
        tab.UnexpectedDisconnectAsync = OnUnexpectedDisconnectAsync;
        tab.ActionRequested += OnTabActionRequested;
        tab.FontZoomRequested += OnTabFontZoomRequested;
        Workspace.AddTab(tab);
        ReplaceLaunchingTab(tab);
        return CreateConnectionTarget(tab);
    }

    // IConnectionHost：跳板链解析按 Id 查会话节点（取自最近一次加载的树缓存）
    SessionNode? IConnectionHost.FindSession(Guid id)
        => _allNodesCache.OfType<SessionNode>().FirstOrDefault(n => n.Id == id);

    /// <summary>
    /// 重排标签顺序，保持当前选中标签不变
    /// </summary>
    public void MoveTab(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Tabs.Count || toIndex < 0 || toIndex >= Tabs.Count)
        {
            return;
        }

        if (fromIndex == toIndex)
        {
            return;
        }

        var selected = SelectedTab;
        TerminalTabViewModel moving = Tabs[fromIndex];
        Tabs.Move(fromIndex, toIndex);
        Workspace.ReorderTab(moving, toIndex);
        if (selected != null)
        {
            SelectedTab = selected;
        }
    }

    private void OnTabCloseRequested(TerminalTabViewModel tab) => _ = CloseTabCommand.ExecuteAsync(tab);

    [RelayCommand]
    private Task CloseTabAsync(TerminalTabViewModel? tab) => Safe.RunAsync(_logger, "关闭标签", () =>
    {
        if (tab == null)
        {
            return Task.CompletedTask;
        }

        tab.CloseRequested -= OnTabCloseRequested;
        tab.ActionRequested -= OnTabActionRequested;
        tab.FontZoomRequested -= OnTabFontZoomRequested;
        CloseOwnedFileManagerTabs(tab);
        TabReleasing?.Invoke(tab);
        // Dock 选择同组相邻项，包含启动页；只显式关闭时释放连接。
        Workspace.RemoveTab(tab);

        // 控件解绑从 UI 线程开始；网络释放由标签内部切到后台，关闭窗口前等待完成。
        return tab.DisposeAsync().AsTask();
    });

}
