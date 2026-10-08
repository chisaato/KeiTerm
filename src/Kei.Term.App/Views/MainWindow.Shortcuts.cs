using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

// 平台快捷键与随焦点路由的菜单装配独立于窗口布局、拖拽和连接生命周期。
public partial class MainWindow
{
    private FocusedEditCommands? _editCommands;
    private readonly List<NativeMenu> _editMenus = new();

    private void SetUpPlatformKeyBindings()
    {
        _editCommands = new FocusedEditCommands(this, SessionTree, GetTreeEditCommand);
        AddHandler(KeyDownEvent, OnTreeEditKeyDown, RoutingStrategies.Tunnel);
        Closed += OnEditCommandsClosed;
        ApplyEditMenus();
    }

    private ICommand? GetTreeEditCommand(EditAction action) => DataContext is MainViewModel vm
        ? action switch
        {
            EditAction.Cut => vm.CutNodeCommand,
            EditAction.Copy => vm.CopyNodeCommand,
            EditAction.Paste => vm.PasteNodeCommand,
            EditAction.Delete => vm.DeleteSelectedNodeCommand,
            _ => null
        } : null;

    private void ApplyEditMenus()
    {
        NativeMenu? menu = NativeMenu.GetMenu(this);
        if (menu == null || _editCommands == null) return;
        foreach (NativeMenuItem item in EnumerateMenuItems(menu.Items))
        {
            EditAction? action = item.Header switch
            {
                var header when header == Strings.Get("Menu.Edit.Cut") => EditAction.Cut,
                var header when header == Strings.Get("Menu.Edit.Copy") => EditAction.Copy,
                var header when header == Strings.Get("Menu.Edit.Paste") => EditAction.Paste,
                var header when header == Strings.Get("Menu.Edit.DeleteSelected") => EditAction.Delete,
                var header when header == Strings.Get("Menu.Edit.SelectAll") => EditAction.SelectAll,
                _ => null
            };
            if (action is { } editAction)
            {
                item.Command = _editCommands[editAction];
                item.Gesture = editAction switch
                {
                    EditAction.Cut => AppShortcuts.Cut,
                    EditAction.Copy => AppShortcuts.Copy,
                    EditAction.Paste => AppShortcuts.Paste,
                    EditAction.SelectAll => AppShortcuts.SelectAll,
                    _ => null
                };
            }
        }
        foreach (NativeMenu nativeMenu in EnumerateMenus(menu))
        {
            nativeMenu.NeedsUpdate += OnEditMenuNeedsUpdate;
            _editMenus.Add(nativeMenu);
        }
    }

    private static IEnumerable<NativeMenu> EnumerateMenus(NativeMenu menu)
    {
        yield return menu;
        foreach (NativeMenuItemBase item in menu.Items)
        {
            if (item is NativeMenuItem { Menu: { } child })
                foreach (NativeMenu submenu in EnumerateMenus(child)) yield return submenu;
        }
    }

    private void OnEditMenuNeedsUpdate(object? sender, EventArgs args) => _editCommands?.Refresh();

    private void OnTreeEditKeyDown(object? sender, KeyEventArgs args)
    {
        // 普通 Ctrl+C 属于远端终端；只为会话树补充桌面编辑快捷键，输入框由 Avalonia 自行处理。
        if (!TreeHasFocus() || FocusIsTextInput() || _editCommands == null) return;
        EditAction? action = AppShortcuts.Cut.Matches(args) ? EditAction.Cut
            : AppShortcuts.Copy.Matches(args) ? EditAction.Copy
            : AppShortcuts.Paste.Matches(args) ? EditAction.Paste : null;
        if (action is { } editAction && _editCommands[editAction].CanExecute(null))
        {
            args.Handled = true;
            _editCommands[editAction].Execute(null);
        }
    }

    private void ApplyMenuShortcuts(MainViewModel vm)
    {
        // NativeMenu.Gesture 在 macOS 由 NSMenu 注册，在其他平台只展示；后者同时装配窗口绑定。
        KeyBindings?.Clear();
        if (!OperatingSystem.IsMacOS())
        {
            KeyBindings?.Add(new KeyBinding { Gesture = AppShortcuts.NewSession, Command = vm.CreateSessionCommand });
            KeyBindings?.Add(new KeyBinding { Gesture = AppShortcuts.QuickConnect, Command = vm.QuickConnectCommand });
            KeyBindings?.Add(new KeyBinding { Gesture = AppShortcuts.NewTab, Command = vm.NewTabCommand });
            KeyBindings?.Add(new KeyBinding { Gesture = AppShortcuts.CloseTab, Command = vm.CloseCurrentWorkspaceTabCommand });
            KeyBindings?.Add(new KeyBinding { Gesture = AppShortcuts.ConnectSavedSession, Command = vm.ConnectSavedSessionCommand });
            KeyBindings?.Add(new KeyBinding { Gesture = AppShortcuts.Find, Command = vm.OpenTerminalFindCommand });
            KeyBindings?.Add(new KeyBinding { Gesture = AppShortcuts.CommandPalette(vm.CurrentSettings.CommandPaletteShortcut), Command = vm.OpenCommandPaletteCommand });
        }
        NativeMenu? menu = NativeMenu.GetMenu(this);
        if (menu == null) return;
        foreach (NativeMenuItem item in EnumerateMenuItems(menu.Items))
        {
            if (ReferenceEquals(item.Command, vm.CreateSessionCommand)) item.Gesture = AppShortcuts.NewSession;
            else if (ReferenceEquals(item.Command, vm.QuickConnectCommand)) item.Gesture = AppShortcuts.QuickConnect;
            else if (ReferenceEquals(item.Command, vm.NewTabCommand)) item.Gesture = AppShortcuts.NewTab;
            else if (ReferenceEquals(item.Command, vm.CloseCurrentWorkspaceTabCommand)) item.Gesture = AppShortcuts.CloseTab;
            else if (ReferenceEquals(item.Command, vm.ConnectSavedSessionCommand)) item.Gesture = AppShortcuts.ConnectSavedSession;
            else if (ReferenceEquals(item.Command, vm.OpenTerminalFindCommand)) item.Gesture = AppShortcuts.Find;
            else if (ReferenceEquals(item.Command, vm.OpenCommandPaletteCommand)) item.Gesture = AppShortcuts.CommandPalette(vm.CurrentSettings.CommandPaletteShortcut);
        }
        _editCommands?.Refresh();
    }

    private void OnEditCommandsClosed(object? sender, EventArgs args)
    {
        foreach (NativeMenu menu in _editMenus) menu.NeedsUpdate -= OnEditMenuNeedsUpdate;
        _editMenus.Clear();
        RemoveHandler(KeyDownEvent, OnTreeEditKeyDown);
        _editCommands?.Dispose();
        Closed -= OnEditCommandsClosed;
    }
}
