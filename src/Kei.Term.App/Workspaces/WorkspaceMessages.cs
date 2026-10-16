namespace Kei.Term.App.Workspaces;

using Kei.Term.App.ViewModels;

public sealed record WorkspaceActiveItemChangedMessage(WorkspaceCoordinator Source, ViewModelBase? Item);

public sealed record WorkspaceItemCloseRequestedMessage(WorkspaceCoordinator Source, ViewModelBase Item);

// 提升：把侧栏里的这份文件视图移成文件标签。关闭文件标签后视图回到侧栏，不另建文件通道。
public sealed record FileViewPromoteRequestedMessage(RemoteFileManagerViewModel FileView);

// 目录标签：从远程目录新开一份文件视图。浏览位置独立，文件通道仍由连接创建的那份文件视图释放。
public sealed record DirectoryTabRequestedMessage(RemoteFileManagerViewModel Source, string Directory);

// 关闭文件标签。提升出去的视图回到侧栏；目录标签只释放自己的浏览状态。
public sealed record FileTabCloseRequestedMessage(RemoteFileManagerViewModel FileView);

public sealed record FileManagerShellRequestedMessage(RemoteFileManagerViewModel FileManager);

public sealed record FileManagerMaximizeRequestedMessage(RemoteFileManagerViewModel FileManager);

public sealed record ConnectionDocumentsChangedMessage(TerminalTabViewModel Tab, bool CloseEmptyTab = false);

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
