using Avalonia.Controls;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

// 快速连接窗口：仅负责收集输入，确认后由 WireDialogs 闭包回调 MainViewModel.ConnectQuickAsync
public partial class QuickConnectWindow : Window
{
    public QuickConnectWindow()
    {
        InitializeComponent();
    }

    public QuickConnectWindow(QuickConnectViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose += Close;
        // 窗口关闭时解绑，避免 VM 复用导致的处理器累积
        Closed += (_, _) => vm.RequestClose -= Close;

        KeyDown += (sender, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }
}
