using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kei.Term.App.ViewModels;

public partial class RemoteFileManagerViewModel
{
    // 文件管理在侧栏与工作区间重建视图时，仍保留用户的目录树布局。
    [ObservableProperty]
    private bool _isDirectoryTreeVisible = true;

    [ObservableProperty]
    private double _directoryTreeWidth = 160;

    [RelayCommand]
    private void ToggleDirectoryTree() => IsDirectoryTreeVisible = !IsDirectoryTreeVisible;
}
