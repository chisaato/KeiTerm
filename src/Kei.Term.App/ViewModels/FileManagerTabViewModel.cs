using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Helpers;
using Kei.Term.App.Workspaces;

namespace Kei.Term.App.ViewModels;

// 文件标签的工作区壳。提升出去的是侧栏那份文件视图；目录标签是另一份浏览状态。
// 两者都不是 WorkspaceTab 这个泛称，也不在这里创建或释放连接的文件通道。
public partial class FileManagerTabViewModel : ViewModelBase
{
    private const string TitleKey = "FileManager.Tab.Title";
    private bool _detached;

    public FileManagerTabViewModel(TerminalTabViewModel owner, RemoteFileManagerViewModel fileManager)
    {
        Owner = owner;
        FileManager = fileManager;
        owner.PropertyChanged += OnOwnerPropertyChanged;
        fileManager.PropertyChanged += OnFileManagerPropertyChanged;
    }

    public TerminalTabViewModel Owner { get; }
    public RemoteFileManagerViewModel FileManager { get; }
    public string Title => Owner.FormatFileTabTitle(FileManager.CurrentPath);
    public string ToolTip => $"{Strings.Format(TitleKey, Owner.TabName)}{Environment.NewLine}{FileManager.CurrentPath}{Environment.NewLine}{Owner.ToolTip}";
    public IRelayCommand RequestCloseCommand => FileManager.CloseCommand;

    [ObservableProperty] private bool _isSelected;

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TerminalTabViewModel.TitleContext)) OnPropertyChanged(nameof(Title));
        if (args.PropertyName == nameof(TerminalTabViewModel.ToolTip)) OnPropertyChanged(nameof(ToolTip));
        // 主动断开或重连卸下旧通道时，不保留一个指向已释放文件系统的文档。
        if (args.PropertyName == nameof(TerminalTabViewModel.FileManager) && !ReferenceEquals(Owner.FileManager, FileManager))
            WeakReferenceMessenger.Default.Send(new FileTabCloseRequestedMessage(FileManager));
    }

    private void OnFileManagerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(RemoteFileManagerViewModel.CurrentPath))
        {
            OnPropertyChanged(nameof(ToolTip));
            OnPropertyChanged(nameof(Title));
        }
    }

    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        Owner.PropertyChanged -= OnOwnerPropertyChanged;
        FileManager.PropertyChanged -= OnFileManagerPropertyChanged;
        FileManager.IsFileTab = false;
        FileManager.IsShellVisible = Owner.IsShellVisible;
    }
}
