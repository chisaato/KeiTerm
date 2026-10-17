using Avalonia.Controls;
using Avalonia.Interactivity;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;

namespace Kei.Term.App.Views.Settings;

public partial class TerminalAppearanceSettingsPageView : UserControl
{
    public TerminalAppearanceSettingsPageView()
    {
        InitializeComponent();
    }

    private async void OnOpenFontDialogClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: AppearanceSettingsPage page } ||
            page.SelectedTerminalProfile is not { } profile || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        // 改正在编辑的终端方案。设置窗口取消会用已提交副本覆盖，不会落盘。
        var dialogVm = new FontDialogViewModel(profile);
        await new FontDialogWindow(dialogVm).ShowDialog(owner);
    }
}
