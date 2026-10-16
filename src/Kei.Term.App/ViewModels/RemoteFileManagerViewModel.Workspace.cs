using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Helpers;
using Kei.Term.App.Workspaces;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.App.ViewModels;

// 提升移动的是侧栏里已有的文件视图；目录标签是另一份浏览状态。
// 文件通道只由连接创建的那份文件视图持有，目录标签不另建、也不提前释放。
public partial class RemoteFileManagerViewModel
{
    // 非空表示本实例是目录标签，指向连接创建的那份文件视图。
    private readonly RemoteFileManagerViewModel? _owningFileView;
    private readonly List<RemoteFileManagerViewModel> _directoryTabs = [];

    internal bool IsDirectoryTab => _owningFileView != null;

    [ObservableProperty]
    private bool _isShellVisible = true;

    [ObservableProperty]
    private bool _isMaximized;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseToolTip))]
    private bool _isFileTab;

    public string CloseToolTip => Strings.Get(IsFileTab ? "Menu.File.CloseTab" : "FileManager.CloseSidebar");

    [RelayCommand]
    public void PromoteToTab() => WeakReferenceMessenger.Default.Send(new FileViewPromoteRequestedMessage(this));

    private bool CanOpenDirectoryInTab(RemoteFileItem? item)
        => !_disposed && item is { IsDirectory: true } && TerminalDirectoryTracker.IsValidDirectory(item.FullPath);

    [RelayCommand(CanExecute = nameof(CanOpenDirectoryInTab))]
    private void OpenDirectoryInTab(RemoteFileItem? item)
    {
        if (CanOpenDirectoryInTab(item))
            WeakReferenceMessenger.Default.Send(new DirectoryTabRequestedMessage(this, item!.FullPath));
    }

    public RemoteFileManagerViewModel? CreateDirectoryTab(string directory)
    {
        RemoteFileManagerViewModel owner = _owningFileView ?? this;
        lock (owner._operationLock)
        {
            if (_disposed || owner._disposed || !TerminalDirectoryTracker.IsValidDirectory(directory)) return null;
            RemoteFileManagerViewModel browser = new(_sessionId, _fileSystem, _fileTracker, _settingsService, _editorRepo, _logger, owner, owner._board, owner._interaction)
            {
                CurrentPath = directory,
                ShowHiddenFiles = ShowHiddenFiles,
            };
            // 先登记再启动 I/O。关闭连接时由所属文件视图取消并排空目录标签。
            owner._directoryTabs.Add(browser);
            browser.StartOperation(() => browser.InitializeCoreAsync(directory, browser._lifetimeCts.Token));
            return browser;
        }
    }

    internal void ReleaseDirectoryTab()
    {
        if (_owningFileView is not { } owner) return;
        owner.StartOperation(async () =>
        {
            try { await DisposeAsync(); }
            finally
            {
                lock (owner._operationLock) owner._directoryTabs.Remove(this);
            }
        });
    }

    private Task DisposeDirectoryTabsAsync()
        => Task.WhenAll(_directoryTabs.ConvertAll(browser => browser.DisposeAsync().AsTask()));

    [RelayCommand]
    public void ShowShell() => WeakReferenceMessenger.Default.Send(new FileManagerShellRequestedMessage(this));

    [RelayCommand]
    public void Maximize() => WeakReferenceMessenger.Default.Send(new FileManagerMaximizeRequestedMessage(this));

    [RelayCommand]
    public void Close()
    {
        if (IsFileTab)
            WeakReferenceMessenger.Default.Send(new FileTabCloseRequestedMessage(this));
        else
            CloseRequested?.Invoke();
    }
}
