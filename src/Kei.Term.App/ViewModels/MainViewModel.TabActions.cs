using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Helpers;
using Kei.Term.App.Logging;
using Kei.Term.App.Services.Connection;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.App.Workspaces;
using Dock.Model.Controls;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.ViewModels;

// 标签右键菜单动作：重连 / 克隆 / 重命名 / 批量关闭
public partial class MainViewModel
{
    private TerminalTabConnectionTarget CreateConnectionTarget(TerminalTabViewModel tab)
        => new(tab, _settingsService, _editorRepo, _loggerFactory);

    private void OnTabActionRequested(TerminalTabViewModel tab, TabAction action)
    {
        Task work = action switch
        {
            TabAction.Reconnect => ReconnectTabAsync(tab),
            TabAction.Clone => CloneTabAsync(tab),
            TabAction.Rename => RenameTabAsync(tab),
            TabAction.CloseOthers => CloseWorkspaceTabsAsync(WorkspaceTabs.Where(t => t != tab).ToList()),
            TabAction.CloseToRight => CloseWorkspaceTabsAsync(TabsToRight(tab)),
            _ => Task.CompletedTask
        };
        _ = Safe.RunAsync(_logger, $"标签动作 {action}", () => work);
    }

    private System.Collections.Generic.IReadOnlyList<ViewModelBase> TabsToRight(TerminalTabViewModel tab)
    {
        if (Workspace.FindDocument(tab) is not { Owner: IDocumentDock { VisibleDockables: { } items } } document)
            return [];
        return items.Skip(items.IndexOf(document) + 1).OfType<WorkspaceDocument>().Select(item => item.Item).ToArray();
    }

    // 已保存的会话按最新会话配置重新解析（期间可能改过主机/身份）；快速连接等无节点的标签沿用原配置并重新询问认证
    private (ResolvedSessionConfig Config, bool UseIdentity)? ResolveForReopen(TerminalTabViewModel tab)
    {
        if (tab.Config is not { } previous)
        {
            return null;
        }

        SessionNode? node = ((IConnectionHost)this).FindSession(previous.SessionId);
        return node != null
            ? (SessionConfigBuilder.Build(node, _settingsService.Current), true)
            : (previous, false);
    }

    // 读循环意外退出。开关关闭、或这次连接依赖当场输入时，策略直接不排程。
    private Task OnUnexpectedDisconnectAsync(TerminalTabViewModel tab, int attempt, CancellationToken ct)
    {
        if (tab.IsDisposed || ResolveForReopen(tab) is not { } target)
        {
            return Task.CompletedTask;
        }

        var facts = new ReconnectFacts(
            _settingsService.Current.AutoReconnectOnDisconnect,
            UserDisconnected: false,
            AuthCancelled: false,
            HostKeyRejected: false,
            InteractiveRequired: tab.RequiresInteractiveAuthentication,
            attempt);
        return _connections.ScheduleReconnectAsync(
            new ConnectionRequest(target.Config, target.UseIdentity, ReuseTarget: CreateConnectionTarget(tab)),
            this,
            facts,
            ct);
    }

    // 原地重连：沿用同一标签与其终端历史
    public async Task ReconnectTabAsync(TerminalTabViewModel tab)
    {
        if (tab.IsDisposed || ResolveForReopen(tab) is not { } target)
        {
            return;
        }

        tab.CancelReconnect();
        _logger.LogInformation("标签原地重连 标题={Title}", tab.Title);
        SelectedTab = tab;
        await _connections.ConnectAsync(
            new ConnectionRequest(target.Config, target.UseIdentity, ReuseTarget: CreateConnectionTarget(tab)),
            this);
    }

    // 克隆：以同一会话在新标签中再开一个连接
    private async Task CloneTabAsync(TerminalTabViewModel tab)
    {
        if (ResolveForReopen(tab) is not { } target)
        {
            return;
        }

        await _connections.ConnectAsync(new ConnectionRequest(target.Config, target.UseIdentity), this);
    }

    private async Task RenameTabAsync(TerminalTabViewModel tab)
    {
        string? name = await Interaction.PromptTextAsync(
            Strings.Get("Main.Tab.Rename.Title"),
            Strings.Get("Main.Tab.Rename.Label"),
            tab.TabName);
        if (!string.IsNullOrWhiteSpace(name))
        {
            tab.Rename(name);
        }
    }

    private async Task CloseWorkspaceTabsAsync(System.Collections.Generic.IReadOnlyList<ViewModelBase> tabs)
    {
        foreach (ViewModelBase tab in tabs)
        {
            await CloseWorkspaceTabCommand.ExecuteAsync(tab);
        }
    }
}
