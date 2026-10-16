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
        TerminalTabViewModel? owner = FindFileManagerOwner(message.FileManager);
        if (owner == null || owner.IsDisposed) return;
        // 恢复原连接的终端视图；文件标签请求显示终端时回到所属连接标签。
        owner.ActiveDocument = ConnectionDocumentKind.Shell;
        owner.IsShellVisible = true;
        if (owner.IsFileManagerMaximized) owner.IsFileManagerVisible = false;
        Workspace.Activate(owner);
        owner.Terminal.Focus();
    });

    private void OnFileManagerMaximizeRequested(object recipient, FileManagerMaximizeRequestedMessage message) => _uiDispatch(() =>
    {
        if (_disposed != 0 || message.FileManager.IsFileTab) return;
        TerminalTabViewModel? owner = FindFileManagerOwner(message.FileManager);
        if (owner == null || owner.IsDisposed) return;
        owner.ToggleFileManagerMaximized();
        Workspace.Activate(owner);
    });

    private void OnFileViewPromoteRequested(object recipient, FileViewPromoteRequestedMessage message) => _uiDispatch(() =>
    {
        if (_disposed != 0) return;
        TerminalTabViewModel? owner = FindFileManagerOwner(message.FileView);
        if (owner == null || owner.IsDisposed) return;
        if (FindFileManagerTab(message.FileView) is { } existing)
        {
            Workspace.Activate(existing);
            return;
        }

        // 提升的是侧栏里已有的文件视图，不新建通道。按来源连接选组。
        Workspace.Activate(owner);
        owner.IsFileManagerMaximized = false;
        owner.IsShellVisible = true;
        owner.IsFileManagerVisible = false;
        Workspace.AddTab(new FileManagerTabViewModel(owner, message.FileView));
    });

    private void OnDirectoryTabRequested(object recipient, DirectoryTabRequestedMessage message) => _uiDispatch(() =>
    {
        if (_disposed != 0) return;
        TerminalTabViewModel? owner = FindFileManagerOwner(message.Source);
        if (owner == null || owner.IsDisposed) return;
        if (message.Source.CreateDirectoryTab(message.Directory) is not { } browser) return;
        // 目录标签是新的浏览位置，不移动来源页。进入来源连接所在的分组。
        Workspace.Activate(FindFileManagerTab(message.Source) as ViewModelBase ?? owner);
        Workspace.AddTab(new FileManagerTabViewModel(owner, browser));
    });

    private void OnFileTabCloseRequested(object recipient, FileTabCloseRequestedMessage message) => _uiDispatch(() =>
    {
        if (_disposed == 0 && FindFileManagerTab(message.FileView) is { } tab) CloseFileManagerTab(tab);
    });

    private FileManagerTabViewModel? FindFileManagerTab(RemoteFileManagerViewModel fileManager)
        => WorkspaceTabs.OfType<FileManagerTabViewModel>().FirstOrDefault(tab => ReferenceEquals(tab.FileManager, fileManager));

    private TerminalTabViewModel? FindFileManagerOwner(RemoteFileManagerViewModel fileManager)
        => FindFileManagerTab(fileManager)?.Owner
            ?? Tabs.FirstOrDefault(tab => ReferenceEquals(tab.FileManager, fileManager));

    private void CloseFileManagerTab(FileManagerTabViewModel tab)
    {
        WorkspaceItemReleasing?.Invoke(tab);
        Workspace.RemoveTab(tab);
        tab.FileManager.ReleaseDirectoryTab();
    }

    private void CloseOwnedFileManagerTabs(TerminalTabViewModel owner)
    {
        foreach (FileManagerTabViewModel tab in WorkspaceTabs.OfType<FileManagerTabViewModel>().Where(tab => ReferenceEquals(tab.Owner, owner)).ToArray())
            CloseFileManagerTab(tab);
    }
}
