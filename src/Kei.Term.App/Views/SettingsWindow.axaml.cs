using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;

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
            proxyPage.EditProxyAsync = existing => EditProxyAsync(vm, existing);
            _ = proxyPage.ReloadAsync();
        }

        // 窗口关闭时解绑，避免 VM 复用导致的处理器累积
        Closed += (_, _) =>
        {
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

    private async Task<ProxyProfile?> EditProxyAsync(SettingsViewModel settings, ProxyProfile? existing)
    {
        var editVm = new ProxyEditViewModel(existing);
        editVm.PickSessionAsync = () => PickSessionForProxyAsync(settings);
        await new ProxyEditWindow(editVm).ShowDialog(this);
        return editVm.IsConfirmed ? editVm.Build() : null;
    }

    private async Task<SessionNode?> PickSessionForProxyAsync(SettingsViewModel settings)
    {
        IReadOnlyList<TreeNodeBase> roots = settings.SessionTreeSnapshot?.Invoke() ?? [];
        IReadOnlyList<SessionNode> sessions = FlattenSessions(roots);
        var picker = new SessionPickerViewModel(roots, Guid.Empty, sessions);
        SessionNode? picked = await new SessionPickerWindow(picker).ShowDialog<SessionNode?>(this);
        return picked;
    }

    private static List<SessionNode> FlattenSessions(IEnumerable<TreeNodeBase> nodes)
    {
        var list = new List<SessionNode>();
        foreach (TreeNodeBase node in nodes)
        {
            if (node is SessionNode session)
            {
                list.Add(session);
            }

            if (node is FolderNode folder)
            {
                list.AddRange(FlattenSessions(folder.Children));
            }
            else if (node is VirtualRootNode root)
            {
                list.AddRange(FlattenSessions(root.Children));
            }
        }

        return list;
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
