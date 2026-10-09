using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class TerminalThemeJsonImportWindow : Window
{
    public TerminalThemeJsonImportWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => JsonEditor.Focus();
    }

    public TerminalThemeJsonImportWindow(TerminalThemeJsonImportViewModel viewModel) : this()
        => DataContext = viewModel;

    private async void OnCopyPromptClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalThemeJsonImportViewModel viewModel) return;

        try
        {
            if (Clipboard is not { } clipboard)
            {
                viewModel.ReportPromptCopied(false);
                return;
            }

            await clipboard.SetTextAsync(viewModel.CreateLlmPrompt());
            viewModel.ReportPromptCopied(true);
        }
        catch (Exception)
        {
            // 剪贴板不可用时留在编辑器内，显示可见反馈供重试。
            viewModel.ReportPromptCopied(false);
        }
    }

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalThemeJsonImportViewModel viewModel) return;
        viewModel.Confirm();
        if (viewModel.Result != null) Close(viewModel.Result);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalThemeJsonImportViewModel viewModel) viewModel.Cancel();
        Close(null);
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancelClick(sender, e);
        }
        else if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            if (DataContext is TerminalThemeJsonImportViewModel { CanImport: true }) OnImportClick(sender, e);
        }
    }
}
