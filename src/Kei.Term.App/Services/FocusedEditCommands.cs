using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using RoyalTerminal.Avalonia.Controls;

namespace Kei.Term.App.Services;

public enum EditAction { Cut, Copy, Paste, Delete, SelectAll }

// 编辑命令属于焦点所在视图；打开菜单时保留此前目标，不把焦点误退到会话树。
public sealed class FocusedEditCommands : IDisposable
{
    private readonly Window _window;
    private readonly TreeView _tree;
    private readonly Func<EditAction, ICommand?> _treeCommand;
    private readonly Dictionary<EditAction, RelayCommand> _commands = new();
    private Control? _target;
    private bool _disposed;

    public FocusedEditCommands(Window window, TreeView tree, Func<EditAction, ICommand?> treeCommand)
    {
        _window = window;
        _tree = tree;
        _treeCommand = treeCommand;
        foreach (EditAction action in Enum.GetValues<EditAction>())
        {
            _commands[action] = new RelayCommand(() => Execute(action), () => CanExecute(action));
        }
        window.AddHandler(InputElement.GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        CaptureFocus();
    }

    public ICommand this[EditAction action] => _commands[action];

    public void Refresh()
    {
        CaptureFocus();
        foreach (RelayCommand command in _commands.Values) command.NotifyCanExecuteChanged();
    }

    private void OnGotFocus(object? sender, RoutedEventArgs args)
    {
        // GotFocus 路由期间 FocusManager 可能仍返回旧元素，以事件源记录新目标。
        CaptureFocus(args.Source as Control);
        foreach (RelayCommand command in _commands.Values) command.NotifyCanExecuteChanged();
    }

    private void CaptureFocus(Control? focused = null)
    {
        if (_disposed) return;
        focused ??= _window.FocusManager?.GetFocusedElement() as Control;
        if (focused == null) return;
        // 窗口内菜单会取得焦点，原生菜单则保留应用内焦点。两种路径都作用于此前编辑目标。
        foreach (Control ancestor in focused.GetSelfAndVisualAncestors().OfType<Control>())
        {
            if (ancestor is MenuItem or MenuBase or NativeMenuBar) return;
        }
        Control? target = null;
        foreach (Control ancestor in focused.GetSelfAndVisualAncestors().OfType<Control>())
        {
            if (ancestor is TextBox or TerminalControl || ReferenceEquals(ancestor, _tree))
            {
                target = ancestor;
                break;
            }
        }
        if (ReferenceEquals(target, _target)) return;
        if (_target != null) _target.PropertyChanged -= OnTargetPropertyChanged;
        _target = target;
        if (_target != null) _target.PropertyChanged += OnTargetPropertyChanged;
    }

    private void OnTargetPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == TextBox.CanCopyProperty || args.Property == TextBox.CanCutProperty
            || args.Property == TextBox.CanPasteProperty || args.Property == TextBox.TextProperty
            || args.Property == TextBox.SelectionStartProperty || args.Property == TextBox.SelectionEndProperty
            || args.Property == InputElement.IsEnabledProperty)
        {
            foreach (RelayCommand command in _commands.Values) command.NotifyCanExecuteChanged();
        }
    }

    private bool CanExecute(EditAction action)
    {
        CaptureFocus();
        if (_target == null || !_target.IsEffectivelyVisible || !_target.IsEffectivelyEnabled
            || !ReferenceEquals(TopLevel.GetTopLevel(_target), _window)) return false;
        return _target switch
        {
            TextBox box => action switch
            {
                EditAction.Cut => box.CanCut,
                EditAction.Copy => box.CanCopy,
                EditAction.Paste => box.CanPaste,
                EditAction.Delete => !box.IsReadOnly && box.SelectionStart != box.SelectionEnd,
                EditAction.SelectAll => !string.IsNullOrEmpty(box.Text),
                _ => false
            },
            TerminalControl terminal => action switch
            {
                EditAction.Copy => terminal.HasSelection,
                EditAction.Paste or EditAction.SelectAll => true,
                _ => false
            },
            _ => ReferenceEquals(_target, _tree) && (_treeCommand(action)?.CanExecute(null) ?? false)
        };
    }

    private void Execute(EditAction action)
    {
        // 目标、选区和可执行状态可能在菜单展开后变化，执行前再次校验。
        if (!CanExecute(action)) return;
        if (_target is TextBox box)
        {
            switch (action)
            {
                case EditAction.Cut: box.Cut(); break;
                case EditAction.Copy: box.Copy(); break;
                case EditAction.Paste: box.Paste(); break;
                case EditAction.Delete: box.SelectedText = string.Empty; break;
                case EditAction.SelectAll: box.SelectAll(); break;
            }
        }
        else if (_target is TerminalControl terminal)
        {
            switch (action)
            {
                case EditAction.Copy: _ = terminal.CopySelectionAsync(); break;
                case EditAction.Paste: _ = terminal.PasteAsync(); break;
                case EditAction.SelectAll: terminal.SelectAll(); break;
            }
        }
        else if (ReferenceEquals(_target, _tree)) _treeCommand(action)?.Execute(null);
        Refresh();
    }

    public void Dispose()
    {
        _disposed = true;
        _window.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
        if (_target != null) _target.PropertyChanged -= OnTargetPropertyChanged;
        _target = null;
    }
}
