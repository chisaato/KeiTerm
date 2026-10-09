namespace Kei.Term.App.Views;

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;

public partial class RemoteFileManagerView : UserControl
{
    private readonly Func<Task<string?>> _pickExecutable;
    private RemoteFileManagerViewModel? _dialogOwner;

    public RemoteFileManagerView()
    {
        _pickExecutable = PickExecutableFileAsync;
        InitializeComponent();
        InitializeActivityLayout();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SetupPickExecutableDialog();
        BindFileActivities();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        SetupPickExecutableDialog();
        BindFileActivities();
    }

    private void SetupPickExecutableDialog()
    {
        ReleasePickExecutableDialog();
        if (DataContext is RemoteFileManagerViewModel vm)
        {
            _dialogOwner = vm;
            vm.PickExecutableFileDialogAsync = _pickExecutable;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        ReleasePickExecutableDialog();
        ReleaseFileActivities();
        base.OnDetachedFromVisualTree(args);
    }

    private void ReleasePickExecutableDialog()
    {
        // 侧栏和文档交替承载同一个 VM，旧视图不得清掉新视图刚登记的选择器。
        if (_dialogOwner != null && ReferenceEquals(_dialogOwner.PickExecutableFileDialogAsync, _pickExecutable))
            _dialogOwner.PickExecutableFileDialogAsync = null;
        _dialogOwner = null;
    }

    private async Task<string?> PickExecutableFileAsync()
    {
        TopLevel? topLevel = TopLevel.GetTopLevel(this) ?? TopLevel.GetTopLevel(FileListBox);
        if (topLevel?.StorageProvider == null
            && Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            topLevel = desktop.MainWindow;
        if (topLevel?.StorageProvider == null) return null;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择可执行程序",
            AllowMultiple = false
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private void OnListBoxDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is RemoteFileManagerViewModel vm && vm.SelectedItem != null)
        {
            _ = vm.EnterOrOpenFileAsync(vm.SelectedItem);
        }
    }

    private void OnDirectoryTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is RemoteFileManagerViewModel vm && vm.SelectedDirectoryNode != null)
        {
            vm.CurrentPath = vm.SelectedDirectoryNode.FullPath;
            _ = vm.RefreshDirectoryAsync();
        }
    }

    private void OnListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            // 右键时若命中列表项：
            // 如果右击的项未被选中，则将其设为唯一点选；如果已被选中（多选集合中），则保留多选状态
            if (e.Source is Visual visual)
            {
                var listBoxItem = visual.FindAncestorOfType<ListBoxItem>();
                if (listBoxItem?.DataContext is RemoteFileItem item && DataContext is RemoteFileManagerViewModel vm)
                {
                    if (FileListBox.SelectedItems != null && !FileListBox.SelectedItems.Contains(item))
                    {
                        vm.SelectedItem = item;
                    }
                }
            }
        }
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not RemoteFileManagerViewModel vm || vm.SelectedItem == null)
        {
            e.Cancel = true;
            return;
        }

        // 同步 FileListBox.SelectedItems 到 vm.SelectedItems
        vm.SelectedItems.Clear();
        if (FileListBox.SelectedItems != null)
        {
            foreach (var sel in FileListBox.SelectedItems.OfType<RemoteFileItem>())
            {
                vm.SelectedItems.Add(sel);
            }
        }
        if (vm.SelectedItems.Count == 0 && vm.SelectedItem != null)
        {
            vm.SelectedItems.Add(vm.SelectedItem);
        }

        var openWithSubMenu = this.FindControl<MenuItem>("OpenWithSubMenu");
        PopulateOpenWithSubMenu(openWithSubMenu, vm);

        // 后台刷新，避免阻塞 ContextMenu 打开过程导致锁死
        _ = vm.LoadAvailableEditorsAsync();
    }

    private async void OnCopyRemotePath(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RemoteFileManagerViewModel vm
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        // 使用远端提供的完整路径，避免本地平台的路径转换；多选时每行一条。
        string[] paths = FileListBox.SelectedItems?.OfType<RemoteFileItem>()
            .Select(item => item.FullPath).Where(path => !string.IsNullOrEmpty(path)).ToArray() ?? [];
        if (paths.Length == 0 && vm.SelectedItem is { } selected)
            paths = [selected.FullPath];
        if (paths.Length == 0) return;

        try
        {
            await clipboard.SetTextAsync(string.Join("\n", paths));
        }
        catch (Exception ex)
        {
            vm.TransferStatusMessage = string.Format(Strings.Get("FileManager.CopyRemotePathFailed"), ex.Message);
        }
    }

    private static void PopulateOpenWithSubMenu(MenuItem? subMenu, RemoteFileManagerViewModel vm)
    {
        if (subMenu == null || vm.SelectedItem == null) return;

        subMenu.IsVisible = !vm.SelectedItem.IsDirectory;
        subMenu.Items.Clear();

        if (!vm.SelectedItem.IsDirectory)
        {
            if (vm.AvailableEditors.Count == 0)
            {
                var emptyItem = new MenuItem
                {
                    Header = "(未配置外部编辑器)",
                    IsEnabled = false
                };
                subMenu.Items.Add(emptyItem);
            }
            else
            {
                foreach (var editor in vm.AvailableEditors)
                {
                    var editorItem = new MenuItem
                    {
                        Header = editor.IsDefault ? $"{editor.Name} (默认)" : editor.Name,
                        Command = vm.OpenWithEditorCommand,
                        CommandParameter = editor
                    };
                    subMenu.Items.Add(editorItem);
                }
            }
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer?.TryGetFiles() != null)
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is RemoteFileManagerViewModel vm && e.DataTransfer != null)
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files != null)
            {
                var paths = files.Select(f => f.Path.LocalPath).Where(p => !string.IsNullOrEmpty(p)).ToArray();
                if (paths.Length > 0)
                {
                    _ = vm.UploadLocalFilesAsync(paths!);
                }
            }
        }
    }
}
