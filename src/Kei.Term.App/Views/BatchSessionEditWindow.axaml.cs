using Avalonia.Controls;
using Kei.Term.App.ViewModels.BatchEdit;

namespace Kei.Term.App.Views;

public partial class BatchSessionEditWindow : Window
{
    public BatchSessionEditWindow()
    {
        InitializeComponent();
    }

    public BatchSessionEditWindow(BatchSessionEditViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose += Close;
    }

    // 「按文件夹勾选」是一次性动作：勾选后清空下拉，便于再次选择同一文件夹
    private void OnFolderPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: BatchFolderOption folder } combo && DataContext is BatchSessionEditViewModel vm)
        {
            vm.SelectFolderCommand.Execute(folder);
            combo.SelectedItem = null;
        }
    }
}
