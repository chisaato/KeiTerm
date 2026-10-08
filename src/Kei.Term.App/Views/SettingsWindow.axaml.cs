using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Models;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.Views;

public partial class SettingsWindow : Window
{
    private bool _isDischargingClose;
    private bool _isCancelling;

    public SettingsWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, CommandPaletteShortcutRecorder_KeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public SettingsWindow(SettingsViewModel vm) : this()
    {
        DataContext = vm;
        vm.SetInteraction(new SettingsWindowInteractionService(this, vm));
        // 每次打开都从当前设置重读，丢弃上次未保存的改动
        vm.Reload();

        // 统一成功出口（Save/Cancel成功后触发）
        Action? onRequestClose = null;
        onRequestClose = () =>
        {
            _isDischargingClose = true;
            Close();
        };
        vm.RequestClose += onRequestClose;

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

        vm.OpenTerminalProfileEditDialogAsync = async (sourceProfile, fontSnapshot) =>
        {
            var editVm = new TerminalProfileEditViewModel(sourceProfile, fontSnapshot);
            var dialog = new TerminalProfileEditWindow(editVm);
            var result = await dialog.ShowDialog<bool>(this);
            return result && editVm.IsConfirmed ? editVm.ResultProfile : null;
        };

        // 接入真正可见且可复制的错误提示区域
        vm.ShowNotificationAsync = (title, message) =>
        {
            var textBlock = this.FindControl<SelectableTextBlock>("NotificationTextBlock");
            if (textBlock != null)
            {
                textBlock.Text = string.IsNullOrWhiteSpace(title) ? message : $"[{title}] {message}";
                textBlock.IsVisible = true;
            }
            return Task.CompletedTask;
        };

        if (vm.ProxyPage is { } proxyPage)
        {
            _ = proxyPage.ReloadAsync();
        }

        // 窗口关闭时解绑，避免 VM 复用导致的处理器累积
        Closed += (_, _) =>
        {
            vm.SetInteraction(Kei.Term.App.Services.NullInteractionService.Instance);
            if (onRequestClose != null)
            {
                vm.RequestClose -= onRequestClose;
            }
        };

        // Esc 按键安全关闭（触发取消回滚）
        KeyDown += (sender, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                _ = TriggerCancelAndCloseAsync(vm);
            }
        };
    }

    // 拦截任何非正常保存/放行的关闭操作（包括右上角 X / Alt+F4），必须等回滚完成后方能关闭
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_isDischargingClose)
        {
            return;
        }

        // 拦截系统关闭
        e.Cancel = true;

        if (DataContext is SettingsViewModel vm)
        {
            if (vm.IsBusy)
            {
                // 保存/持久化 busy 期间禁止关闭，保持 e.Cancel = true
                return;
            }

            await TriggerCancelAndCloseAsync(vm);
        }
    }

    private async void OnOpenFontDialogClick(object? sender, RoutedEventArgs e)
    {
        // 按钮落在外观页的 DataContext 上，不是窗口的 SettingsViewModel
        if (sender is not Control { DataContext: AppearanceSettingsPage page } || page.SelectedTerminalProfile is not { } profile)
        {
            return;
        }

        // 改正在编辑的终端方案。设置窗口取消会用已提交副本覆盖，不会落盘。
        var dialogVm = new FontDialogViewModel(profile);
        await new FontDialogWindow(dialogVm).ShowDialog(this);
    }

    private async Task TriggerCancelAndCloseAsync(SettingsViewModel vm)
    {
        if (_isCancelling || vm.IsBusy)
        {
            return;
        }

        _isCancelling = true;
        try
        {
            // 执行取消：回滚内存方案/默认选择并广播
            // 只有取消执行成功，由 VM 触发 RequestClose 才能设置 _isDischargingClose 并真正关闭
            await vm.CancelCommand.ExecuteAsync(null);
        }
        finally
        {
            // 仅重置防重入状态，绝不在此无条件放行或强制 Close()
            _isCancelling = false;
        }
    }
}
