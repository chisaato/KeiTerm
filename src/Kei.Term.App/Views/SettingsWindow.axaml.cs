using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsViewModel vm) : this()
    {
        DataContext = vm;
        // 每次打开都从当前设置重读，丢弃上次未保存的改动
        vm.Reload();
        vm.RequestClose += Close;

        // 挂接文件选择器
        vm.SaveBundleFileDialogAsync = async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return null;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Get("Settings.Appearance.ProfileBackupSection"),
                SuggestedFileName = $"KeiTerm-Profiles-{DateTime.Now:yyyyMMdd}.json",
                DefaultExtension = "json",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("JSON Files (*.json)") { Patterns = new[] { "*.json" } },
                    new FilePickerFileType("All Files (*.*)") { Patterns = new[] { "*.*" } }
                }
            });

            return file?.Path.LocalPath;
        };

        vm.OpenBundleFileDialogAsync = async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return null;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Get("Settings.Appearance.ProfileBackupSection"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("JSON Files (*.json)") { Patterns = new[] { "*.json" } },
                    new FilePickerFileType("All Files (*.*)") { Patterns = new[] { "*.*" } }
                }
            });

            return files.Count > 0 ? files[0].Path.LocalPath : null;
        };

        vm.OpenKonsoleFileDialogAsync = async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return null;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Get("Menu.File.ImportKonsole"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Konsole Color Scheme (*.colorscheme)") { Patterns = new[] { "*.colorscheme" } },
                    new FilePickerFileType("All Files (*.*)") { Patterns = new[] { "*.*" } }
                }
            });

            return files.Count > 0 ? files[0].Path.LocalPath : null;
        };

        vm.OpenTerminalProfileEditDialogAsync = async (sourceProfile) =>
        {
            var editVm = new TerminalProfileEditViewModel(sourceProfile);
            var dialog = new TerminalProfileEditWindow(editVm);
            var result = await dialog.ShowDialog<bool>(this);
            return result && editVm.IsConfirmed ? editVm.ResultProfile : null;
        };

        vm.ShowNotificationAsync = async (title, message) =>
        {
            // 如果 MainWindow 可见，亦可通过日志或弹窗留痕
            await Task.CompletedTask;
        };

        // 窗口关闭时解绑，避免 VM 复用导致的处理器累积
        Closed += (_, _) => vm.RequestClose -= Close;

        // Esc 按键安全关闭（触发取消回滚）
        KeyDown += (sender, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                _ = vm.CancelCommand.ExecuteAsync(null);
            }
        };
    }
}
