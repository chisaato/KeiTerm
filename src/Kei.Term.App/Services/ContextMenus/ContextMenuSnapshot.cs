namespace Kei.Term.App.Services.ContextMenus;

using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;

public sealed record NativeContextMenuEntry(
    int Id, string Label, bool IsEnabled, bool IsChecked, bool IsSeparator,
    IReadOnlyList<NativeContextMenuEntry> Children);

// 在打开时读取真实菜单：保留动态子菜单、命令参数、禁用状态和勾选状态。
public sealed class ContextMenuSnapshot
{
    private readonly Dictionary<int, MenuItem> _actions = new();
    public IReadOnlyList<NativeContextMenuEntry> Entries { get; private set; } = [];

    public static ContextMenuSnapshot? Create(ContextMenu menu)
    {
        ContextMenuSnapshot snapshot = new();
        List<NativeContextMenuEntry>? entries = snapshot.ReadItems(menu.Items);
        if (entries == null || entries.Count == 0)
        {
            return null;
        }

        snapshot.Entries = entries;
        return snapshot;
    }

    public void Invoke(int id)
    {
        if (!_actions.TryGetValue(id, out MenuItem? item) || !item.IsVisible || !item.IsEnabled
            || (item.Command != null && !item.Command.CanExecute(item.CommandParameter)))
        {
            return;
        }

        // 使用原 MenuItem 的点击路由执行命令，避免复制 ViewModel 操作逻辑。
        if (item.ToggleType == MenuItemToggleType.CheckBox)
        {
            item.SetCurrentValue(MenuItem.IsCheckedProperty, !item.IsChecked);
        }
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private List<NativeContextMenuEntry>? ReadItems(IEnumerable<object?> items)
    {
        List<NativeContextMenuEntry> entries = [];
        foreach (object? value in items)
        {
            if (value is Control { IsVisible: false })
            {
                continue;
            }
            if (value is Separator || value is MenuItem { Header: "-" })
            {
                if (entries.Count > 0 && !entries[^1].IsSeparator)
                {
                    entries.Add(new(0, string.Empty, false, false, true, []));
                }
                continue;
            }
            // 自定义模板、图标和单选组需要应用控件承载，不能静默丢弃它们。
            if (value is not MenuItem item || item.Header is not string label
                || item.Icon != null || item.HeaderTemplate != null || item.ItemTemplate != null
                || item.ToggleType == MenuItemToggleType.Radio)
            {
                return null;
            }

            List<NativeContextMenuEntry>? children = ReadItems(item.Items);
            if (children == null)
            {
                return null;
            }
            int id = _actions.Count + 1;
            _actions.Add(id, item);
            bool enabled = item.IsEnabled && (item.Command == null || item.Command.CanExecute(item.CommandParameter));
            entries.Add(new(id, label, enabled, item.IsChecked, false, children));
        }
        if (entries.Count > 0 && entries[^1].IsSeparator)
        {
            entries.RemoveAt(entries.Count - 1);
        }
        return entries;
    }
}
