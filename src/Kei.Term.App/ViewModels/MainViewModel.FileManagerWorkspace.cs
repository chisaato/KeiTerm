using System;
using System.Linq;
using Kei.Term.App.Workspaces;

namespace Kei.Term.App.ViewModels;

public partial class MainViewModel
{
    // 通用文档释放通知只用于在移除 Dock 文档前清理其视图缓存。
    public event Action<ViewModelBase>? WorkspaceItemReleasing;

    public bool IsTerminalWorkspaceActive => ActiveWorkspaceTab is TerminalTabViewModel { IsShellVisible: true };

    private void OnFileManagerShellRequested(object recipient, FileManagerShellRequestedMessage message) => _uiDispatch(() =>
    {
        if (_disposed != 0) return;
        TerminalTabViewModel? owner = Tabs.FirstOrDefault(tab => ReferenceEquals(tab.FileManager, message.FileManager));
        if (owner == null || owner.IsDisposed) return;
        // 恢复原连接的文档和终端缓冲；独立文件标签请求时回到它所属的终端标签。
        owner.ActiveDocument = ConnectionDocumentKind.Shell;
        owner.IsShellVisible = true;
        Workspace.Activate(owner);
        owner.Terminal.Focus();
    });

    private void OnFileManagerTabRequested(object recipient, FileManagerTabRequestedMessage message) => _uiDispatch(() =>
    {
        if (_disposed != 0) return;
        TerminalTabViewModel? owner = Tabs.FirstOrDefault(tab => ReferenceEquals(tab.FileManager, message.FileManager));
        if (owner == null || owner.IsDisposed) return;
        if (FindFileManagerTab(message.FileManager) is { } existing)
        {
            Workspace.Activate(existing);
            return;
        }

        // 按请求来源选组，不把另一分屏中的文件管理器加入当前活动组。
        Workspace.Activate(owner);
        owner.IsFileManagerVisible = false;
        Workspace.AddTab(new FileManagerTabViewModel(owner, message.FileManager));
    });

    private void OnFileManagerTabCloseRequested(object recipient, FileManagerTabCloseRequestedMessage message) => _uiDispatch(() =>
    {
        if (_disposed == 0 && FindFileManagerTab(message.FileManager) is { } tab) CloseFileManagerTab(tab);
    });

    private FileManagerTabViewModel? FindFileManagerTab(RemoteFileManagerViewModel fileManager)
        => WorkspaceTabs.OfType<FileManagerTabViewModel>().FirstOrDefault(tab => ReferenceEquals(tab.FileManager, fileManager));

    private void CloseFileManagerTab(FileManagerTabViewModel tab)
    {
        WorkspaceItemReleasing?.Invoke(tab);
        Workspace.RemoveTab(tab);
    }

    private void CloseOwnedFileManagerTabs(TerminalTabViewModel owner)
    {
        foreach (FileManagerTabViewModel tab in WorkspaceTabs.OfType<FileManagerTabViewModel>().Where(tab => ReferenceEquals(tab.Owner, owner)).ToArray())
            CloseFileManagerTab(tab);
    }
}
