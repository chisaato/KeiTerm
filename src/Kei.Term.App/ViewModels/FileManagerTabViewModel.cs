using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Helpers;
using Kei.Term.App.Workspaces;

namespace Kei.Term.App.ViewModels;

// 文档只承载现有文件管理器，不创建或释放 SSH/SFTP 连接。
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
    public string Title => Strings.Format(TitleKey, Owner.TabName);
    public string ToolTip => $"{Owner.ToolTip}{Environment.NewLine}{FileManager.CurrentPath}";
    public IRelayCommand RequestCloseCommand => FileManager.CloseCommand;

    [ObservableProperty] private bool _isSelected;

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TerminalTabViewModel.TabName)) OnPropertyChanged(nameof(Title));
        if (args.PropertyName == nameof(TerminalTabViewModel.ToolTip)) OnPropertyChanged(nameof(ToolTip));
        // 主动断开或重连卸下旧通道时，不保留一个指向已释放文件系统的文档。
        if (args.PropertyName == nameof(TerminalTabViewModel.FileManager) && !ReferenceEquals(Owner.FileManager, FileManager))
            WeakReferenceMessenger.Default.Send(new FileManagerTabCloseRequestedMessage(FileManager));
    }

    private void OnFileManagerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(RemoteFileManagerViewModel.CurrentPath)) OnPropertyChanged(nameof(ToolTip));
    }

    internal void Detach()
    {
        if (_detached) return;
        _detached = true;
        Owner.PropertyChanged -= OnOwnerPropertyChanged;
        FileManager.PropertyChanged -= OnFileManagerPropertyChanged;
        FileManager.IsWorkspaceTab = false;
    }
}
