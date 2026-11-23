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
    private bool _isSuppressingSelectionSync;
    private readonly TypeAheadNavigator _fileListTypeAhead = new();
    private readonly TypeAheadNavigator _treeTypeAhead = new();

    public RemoteFileManagerView()
    {
        _pickExecutable = PickExecutableFileAsync;
        InitializeComponent();
        InitializeActivityLayout();
        InitializeTransferTable();
        InitializeDirectoryTreeLayout();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        FileListBox.AddHandler(InputElement.TextInputEvent, OnFileListTextInput, RoutingStrategies.Tunnel);
        FileListBox.AddHandler(InputElement.KeyDownEvent, OnFileListKeyDown, RoutingStrategies.Tunnel);
        DirectoryTreeView.AddHandler(InputElement.TextInputEvent, OnDirectoryTreeTextInput, RoutingStrategies.Tunnel);
        DirectoryTreeView.AddHandler(InputElement.KeyDownEvent, OnDirectoryTreeKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SetupPickExecutableDialog();
        BindFileActivities();
        BindDirectoryTreeLayout();
        BindNavigationMonitoring();
        RestoreSelectionFromViewModel();
    }

    private void RestoreSelectionFromViewModel()
    {
        if (DataContext is not RemoteFileManagerViewModel vm) return;
        if (FileListBox.SelectedItems == null) return;

        RemoteFileItem[] snapshot = vm.SelectedItems.Count > 0
            ? vm.SelectedItems.ToArray()
            : (vm.SelectedItem is { } selected ? [selected] : []);

        if (FileListBox.SelectedItems.OfType<RemoteFileItem>().SequenceEqual(snapshot))
        {
            if (snapshot.Length > 0 && !vm.SelectedItems.SequenceEqual(snapshot))
            {
                vm.SelectedItems.Clear();
                foreach (var item in snapshot)
                {
                    vm.SelectedItems.Add(item);
                }
            }
            return;
        }

        _isSuppressingSelectionSync = true;
        try
        {
            FileListBox.SelectedItems.Clear();
            foreach (var item in snapshot)
            {
                FileListBox.SelectedItems.Add(item);
            }

            if (snapshot.Length > 0 && !vm.SelectedItems.SequenceEqual(snapshot))
            {
                vm.SelectedItems.Clear();
                foreach (var item in snapshot)
                {
                    vm.SelectedItems.Add(item);
                }
            }
        }
        finally
        {
            _isSuppressingSelectionSync = false;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        SetupPickExecutableDialog();
        BindFileActivities();
        BindDirectoryTreeLayout();
        BindNavigationMonitoring();

        if (DataContext is RemoteFileManagerViewModel)
        {
            RestoreSelectionFromViewModel();
        }
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
        ReleaseDirectoryTreeLayout();
        ReleaseNavigationMonitoring();
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
            if (DataContext is RemoteFileManagerViewModel vm)
            {
                ListBoxItem? listBoxItem = (e.Source as Visual) as ListBoxItem
                    ?? (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
                if (listBoxItem?.DataContext is RemoteFileItem item)
                {
                    if (FileListBox.SelectedItems != null && !FileListBox.SelectedItems.Contains(item))
                    {
                        FileListBox.SelectedItems.Clear();
                        vm.SelectedItem = item;
                    }
                }
                else
                {
                    FileListBox.SelectedItems?.Clear();
                    vm.SelectedItems.Clear();
                    vm.SelectedItem = null;
                }
            }
        }
    }

    private RemoteFileManagerViewModel? _navigationOwner;

    private void BindNavigationMonitoring()
    {
        ReleaseNavigationMonitoring();
        if (DataContext is RemoteFileManagerViewModel vm)
        {
            _navigationOwner = vm;
            vm.PropertyChanged += OnNavigationViewModelPropertyChanged;
        }
    }

    private void ReleaseNavigationMonitoring()
    {
        if (_navigationOwner != null)
        {
            _navigationOwner.PropertyChanged -= OnNavigationViewModelPropertyChanged;
            _navigationOwner = null;
        }
    }

    private void OnNavigationViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteFileManagerViewModel.CurrentPath))
        {
            _isSuppressingSelectionSync = true;
            try
            {
                FileListBox.SelectedItems?.Clear();
            }
            finally
            {
                _isSuppressingSelectionSync = false;
            }

            if (DataContext is RemoteFileManagerViewModel vm)
            {
                vm.SelectedItems.Clear();
                vm.SelectedItem = null;
            }
        }
    }

    private void OnFileListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isSuppressingSelectionSync) return;
        SyncSelectedItemsToViewModel();
    }

    private void SyncSelectedItemsToViewModel()
    {
        if (_isSuppressingSelectionSync) return;
        if (DataContext is not RemoteFileManagerViewModel vm)
        {
            return;
        }

        var listBoxSelected = FileListBox.SelectedItems?.OfType<RemoteFileItem>().ToList() ?? [];

        // 仅在集合产生差异时同步，避免不必要的集合通知与循环
        if (!vm.SelectedItems.SequenceEqual(listBoxSelected))
        {
            vm.SelectedItems.Clear();
            foreach (var item in listBoxSelected)
            {
                vm.SelectedItems.Add(item);
            }
        }

        // SelectedItem 采用 OneWay 绑定，在 guard 保护后的 SelectionChanged 里统一单向同步 vm.SelectedItem
        RemoteFileItem? currentSelectedItem = FileListBox.SelectedItem as RemoteFileItem
            ?? (listBoxSelected.Count > 0 ? listBoxSelected[0] : null);
        if (!ReferenceEquals(vm.SelectedItem, currentSelectedItem))
        {
            vm.SelectedItem = currentSelectedItem;
        }
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not RemoteFileManagerViewModel vm)
        {
            e.Cancel = true;
            return;
        }

        // 同步 FileListBox.SelectedItems 到 vm.SelectedItems
        SyncSelectedItemsToViewModel();
        if (vm.SelectedItems.Count == 0 && vm.SelectedItem != null)
        {
            vm.SelectedItems.Add(vm.SelectedItem);
        }

        if (sender is ContextMenu menu)
        {
            bool hasSelection = vm.SelectedItems.Count > 0;
            foreach (Control control in menu.Items.OfType<Control>())
                if (control.Classes.Contains("selection-action")) control.IsVisible = hasSelection;
            if (this.FindControl<MenuItem>("RenameMenuItem") is { } rename)
                rename.IsVisible = vm.SelectedItems.Count == 1 && vm.SelectedItem?.Name != "..";
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
            vm.ReportOperationError(RemoteFileManagerViewModel.DescribeFileException(Strings.Get("FileManager.Error.CopyPathFailed"), ex));
        }
    }

    private static void PopulateOpenWithSubMenu(MenuItem? subMenu, RemoteFileManagerViewModel vm)
    {
        if (subMenu == null) return;

        subMenu.IsVisible = vm.SelectedItem is { IsDirectory: false };
        subMenu.Items.Clear();

        if (vm.SelectedItem is { IsDirectory: false })
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

    private void OnDirectoryTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            if (DataContext is RemoteFileManagerViewModel vm)
            {
                TreeViewItem? treeViewItem = (e.Source as Visual) as TreeViewItem
                    ?? (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>();
                if (treeViewItem?.DataContext is RemoteDirectoryNodeViewModel node)
                {
                    vm.SelectedDirectoryNode = node;
                }
            }
        }
    }

    private bool IsTextInputFocused()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused is TextBox;
    }

    private void OnFileListTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(e.Text) || IsTextInputFocused()) return;
        if (DataContext is not RemoteFileManagerViewModel vm) return;

        var items = vm.Items;
        if (items.Count == 0) return;

        int currentIndex = vm.SelectedItem != null ? items.IndexOf(vm.SelectedItem) : -1;
        int matchedIndex = _fileListTypeAhead.ProcessInput(e.Text, items, currentIndex, item => item.Name);
        if (matchedIndex >= 0 && matchedIndex < items.Count)
        {
            SelectAndScrollToFileItem(vm, items[matchedIndex]);
            e.Handled = true;
        }
    }

    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsTextInputFocused()) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;

        if (e.Key == Key.Escape)
        {
            _fileListTypeAhead.Reset();
            return;
        }

        if (e.Key == Key.Back)
        {
            if (DataContext is not RemoteFileManagerViewModel vm) return;
            var items = vm.Items;
            if (items.Count == 0) return;

            int currentIndex = vm.SelectedItem != null ? items.IndexOf(vm.SelectedItem) : -1;
            int matchedIndex = _fileListTypeAhead.HandleBackspace(items, currentIndex, item => item.Name);
            if (matchedIndex >= 0 && matchedIndex < items.Count)
            {
                SelectAndScrollToFileItem(vm, items[matchedIndex]);
                e.Handled = true;
            }
        }
    }

    private void SelectAndScrollToFileItem(RemoteFileManagerViewModel vm, RemoteFileItem item)
    {
        _isSuppressingSelectionSync = true;
        try
        {
            FileListBox.SelectedItems?.Clear();
            FileListBox.SelectedItem = item;
            vm.SelectedItems.Clear();
            vm.SelectedItems.Add(item);
            vm.SelectedItem = item;
        }
        finally
        {
            _isSuppressingSelectionSync = false;
        }
        FileListBox.ScrollIntoView(item);
    }

    private void OnDirectoryTreeTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(e.Text) || IsTextInputFocused()) return;
        if (DataContext is not RemoteFileManagerViewModel vm) return;

        var visibleNodes = CollectVisibleTreeNodes(vm.DirectoryTreeRoots);
        if (visibleNodes.Count == 0) return;

        int currentIndex = vm.SelectedDirectoryNode != null ? visibleNodes.IndexOf(vm.SelectedDirectoryNode) : -1;
        int matchedIndex = _treeTypeAhead.ProcessInput(e.Text, visibleNodes, currentIndex, node => node.Name);
        if (matchedIndex >= 0 && matchedIndex < visibleNodes.Count)
        {
            var target = visibleNodes[matchedIndex];
            vm.SelectedDirectoryNode = target;
            e.Handled = true;
        }
    }

    private void OnDirectoryTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsTextInputFocused()) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;

        if (e.Key == Key.Escape)
        {
            _treeTypeAhead.Reset();
            return;
        }

        if (e.Key == Key.Back)
        {
            if (DataContext is not RemoteFileManagerViewModel vm) return;
            var visibleNodes = CollectVisibleTreeNodes(vm.DirectoryTreeRoots);
            if (visibleNodes.Count == 0) return;

            int currentIndex = vm.SelectedDirectoryNode != null ? visibleNodes.IndexOf(vm.SelectedDirectoryNode) : -1;
            int matchedIndex = _treeTypeAhead.HandleBackspace(visibleNodes, currentIndex, node => node.Name);
            if (matchedIndex >= 0 && matchedIndex < visibleNodes.Count)
            {
                var target = visibleNodes[matchedIndex];
                vm.SelectedDirectoryNode = target;
                e.Handled = true;
            }
        }
    }

    private static System.Collections.Generic.List<RemoteDirectoryNodeViewModel> CollectVisibleTreeNodes(System.Collections.Generic.IEnumerable<RemoteDirectoryNodeViewModel> roots)
    {
        var list = new System.Collections.Generic.List<RemoteDirectoryNodeViewModel>();
        foreach (var root in roots)
        {
            CollectVisibleRecursive(root, list);
        }
        return list;
    }

    private static void CollectVisibleRecursive(RemoteDirectoryNodeViewModel node, System.Collections.Generic.List<RemoteDirectoryNodeViewModel> list)
    {
        if (node.IsDummy) return;
        list.Add(node);
        if (node.IsExpanded && node.Children.Count > 0)
        {
            foreach (var child in node.Children)
            {
                CollectVisibleRecursive(child, list);
            }
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
