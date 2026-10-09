using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.App.Views;

public partial class TerminalProfileEditWindow : Window
{
    private readonly IInteractionService _interaction = NullInteractionService.Instance;
    private bool _editingJson;

    public TerminalProfileEditWindow()
    {
        InitializeComponent();
    }

    public TerminalProfileEditWindow(
        TerminalProfileEditViewModel viewModel, IInteractionService interaction,
        bool importJson = false) : this()
    {
        DataContext = viewModel;
        _interaction = interaction;
        if (importJson) Opened += OnImportJsonOpened;
    }

    private async void OnImportJsonOpened(object? sender, EventArgs e)
    {
        Opened -= OnImportJsonOpened;
        await EditJsonAsync(importJson: true);
    }

    private async void OnEditJsonClick(object? sender, RoutedEventArgs e)
        => await EditJsonAsync(importJson: false);

    private async Task EditJsonAsync(bool importJson)
    {
        if (_editingJson || DataContext is not TerminalProfileEditViewModel draft) return;
        _editingJson = true;
        try
        {
            TerminalProfile? edited = await _interaction.EditTerminalThemeJsonAsync(
                importJson ? null : draft.PreviewProfile.DeepCopy());
            if (edited != null && IsVisible) draft.ApplyJsonProfile(edited);
        }
        finally
        {
            _editingJson = false;
        }
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

    private async void OnOpenFontDialogClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalProfileEditViewModel vm)
        {
            return;
        }

        // 改正在编辑的 ResultProfile；配色窗口取消时这份副本被丢掉，不会写回原方案
        var dialogVm = new FontDialogViewModel(vm.ResultProfile);
        await new FontDialogWindow(dialogVm).ShowDialog(this);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalProfileEditViewModel vm)
        {
            vm.Confirm();
            if (vm.IsConfirmed) Close(true);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
