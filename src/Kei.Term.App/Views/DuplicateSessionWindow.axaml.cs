using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class DuplicateSessionWindow : Window
{
    public DuplicateSessionWindow()
    {
        InitializeComponent();
    }

    public DuplicateSessionWindow(DuplicateSessionViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose += Close;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                vm.CancelCommand.Execute(null);
            }
            else if (e.Key == Key.Enter)
            {
                // 若输入合法且按 Enter，直接触发确认
                if (vm.ConfirmCommand.CanExecute(null))
                {
                    e.Handled = true;
                    vm.ConfirmCommand.Execute(null);
                }
            }
        };

        // 窗口打开后聚焦名称输入框并全选
        Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                TextBox? nameBox = this.FindControl<TextBox>("NameBox");
                if (nameBox != null)
                {
                    nameBox.Focus();
                    nameBox.SelectAll();
                }
            });
        };
    }
}
