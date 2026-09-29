using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class KnownHostsManagerWindow : Window
{
    public KnownHostsManagerWindow()
    {
        InitializeComponent();
    }

    public KnownHostsManagerWindow(KnownHostsManagerViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.PickExportPathAsync = PickExportPathAsync;
    }

    private async Task<string?> PickExportPathAsync()
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Get("KnownHosts.Export"),
            SuggestedFileName = "known_hosts",
            ShowOverwritePrompt = true
        });
        return file?.TryGetLocalPath();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
