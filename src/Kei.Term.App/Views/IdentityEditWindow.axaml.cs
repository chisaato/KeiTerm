using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class IdentityEditWindow : Window
{
    public IdentityEditWindow()
    {
        InitializeComponent();
    }

    public IdentityEditWindow(IdentityEditViewModel vm) : this()
    {
        DataContext = vm;

        // 注入私钥文件选择器（失败可手输路径）
        vm.FilePicker = PickFileAsync;

        vm.RequestClose += Close;
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

    // 调用系统文件选择器；取消/不可用返回 null
    private async Task<string?> PickFileAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Get("Common.SelectPrivateKeyFile"),
                AllowMultiple = false,
            });

            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch
        {
            return null;
        }
    }
}
