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

// 工具栏可提升现有侧栏，目录菜单则创建独立浏览状态；文件通道仍由所属连接释放。
public partial class RemoteFileManagerViewModel
{
    private readonly RemoteFileManagerViewModel? _resourceOwner;
    private readonly List<RemoteFileManagerViewModel> _directoryTabs = [];

    internal bool IsDirectoryTab => _resourceOwner != null;

    [ObservableProperty]
    private bool _isShellVisible = true;

    [ObservableProperty]
    private bool _isMaximized;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseToolTip))]
    private bool _isWorkspaceTab;

    public string CloseToolTip => Strings.Get(IsWorkspaceTab ? "Menu.File.CloseTab" : "FileManager.CloseSidebar");

    [RelayCommand]
    public void PromoteToTab() => WeakReferenceMessenger.Default.Send(new FileManagerTabRequestedMessage(this));

    private bool CanOpenDirectoryInTab(RemoteFileItem? item)
        => !_disposed && item is { IsDirectory: true } && TerminalDirectoryTracker.IsValidDirectory(item.FullPath);

    [RelayCommand(CanExecute = nameof(CanOpenDirectoryInTab))]
    private void OpenDirectoryInTab(RemoteFileItem? item)
    {
        if (CanOpenDirectoryInTab(item))
            WeakReferenceMessenger.Default.Send(new FileManagerTabRequestedMessage(this, item!.FullPath));
    }

    internal RemoteFileManagerViewModel? CreateDirectoryTab(string directory)
    {
        RemoteFileManagerViewModel owner = _resourceOwner ?? this;
        lock (owner._operationLock)
        {
            if (_disposed || owner._disposed || !TerminalDirectoryTracker.IsValidDirectory(directory)) return null;
            RemoteFileManagerViewModel browser = new(_sessionId, _fileSystem, _fileTracker, _settingsService, _editorRepo, _logger, owner)
            {
                CurrentPath = directory,
                ShowHiddenFiles = ShowHiddenFiles,
            };
            // 先登记再启动 I/O；关闭连接会取消并排空所有独立目录页。
            owner._directoryTabs.Add(browser);
            browser.StartOperation(() => browser.InitializeCoreAsync(directory, browser._lifetimeCts.Token));
            return browser;
        }
    }

    internal void ReleaseDirectoryTab()
    {
        if (_resourceOwner is not { } owner) return;
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
        if (IsWorkspaceTab)
            WeakReferenceMessenger.Default.Send(new FileManagerTabCloseRequestedMessage(this));
        else
            CloseRequested?.Invoke();
    }
}
