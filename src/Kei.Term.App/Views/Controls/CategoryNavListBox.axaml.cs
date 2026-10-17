using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Kei.Term.App.ViewModels.Settings;

namespace Kei.Term.App.Views.Controls;

public partial class CategoryNavListBox : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<CategoryNavListBox, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<CategoryNavListBox, object?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public CategoryNavListBox()
    {
        InitializeComponent();
        PART_List.ContainerPrepared += (_, e) =>
        {
            if (e.Container is ListBoxItem item && item.DataContext is SettingsCategoryItem cat && cat.IsHeader)
            {
                item.IsHitTestVisible = false;
                item.Focusable = false;
                item.MinHeight = 24;
                item.Padding = new Thickness(12, 12, 12, 2);
            }
        };
        PART_List.SelectionChanged += (_, e) =>
        {
            if (PART_List.SelectedItem is SettingsCategoryItem item && item.IsHeader)
            {
                // 如果选中了分组标题，自动恢复为原选中项，或者选择下一项
                if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is SettingsCategoryItem previous && !previous.IsHeader)
                {
                    PART_List.SelectedItem = previous;
                }
                else
                {
                    PART_List.SelectedIndex = PART_List.SelectedIndex + 1;
                }
            }
        };
    }
}
