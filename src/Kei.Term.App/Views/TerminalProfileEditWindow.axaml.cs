using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class TerminalProfileEditWindow : Window
{
    public TerminalProfileEditWindow()
    {
        InitializeComponent();
    }

    public TerminalProfileEditWindow(TerminalProfileEditViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private async void OnOpenColorChooserClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalProfileEditViewModel vm || !vm.IsCurrentColorValid)
        {
            return;
        }

        // 调当前已选中的颜色；上面的色块只负责选中，不弹窗
        string originalHex = vm.ActiveHex;

        // 2. 创建调色器 ViewModel，并在数值变动/拖拽时立刻写回 vm.ActiveHex
        var chooserVm = new ColorChooserViewModel(originalHex, liveHex =>
        {
            vm.ActiveHex = liveHex;
        });

        var dialog = new ColorChooserWindow(chooserVm);
        bool? result = await dialog.ShowDialog<bool?>(this);

        // 3. 确定留下当前色；取消或关闭窗口则恢复成打开前的值
        if (result != true || !chooserVm.IsConfirmed)
        {
            vm.ActiveHex = originalHex;
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalProfileEditViewModel vm)
        {
            vm.Confirm();
            Close(true);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
