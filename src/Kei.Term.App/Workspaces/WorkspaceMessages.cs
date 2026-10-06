namespace Kei.Term.App.Workspaces;

using Kei.Term.App.ViewModels;

// 全局活动连接变化。接收者按 Source 过滤，退出时注销；工具栏失焦不会发空活动项。
public sealed class WorkspaceActiveTabChangedMessage
{
    public WorkspaceActiveTabChangedMessage(WorkspaceCoordinator source, TerminalTabViewModel? tab)
    {
        Source = source;
        Tab = tab;
    }

    public WorkspaceCoordinator Source { get; }

    public TerminalTabViewModel? Tab { get; }
}

// 明确关闭请求。协调器只路由一次，不在此释放连接。
public sealed class WorkspaceCloseRequestedMessage
{
    public WorkspaceCloseRequestedMessage(WorkspaceCoordinator source, TerminalTabViewModel tab)
    {
        Source = source;
        Tab = tab;
    }

    public WorkspaceCoordinator Source { get; }

    public TerminalTabViewModel Tab { get; }
}
