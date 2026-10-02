using Avalonia;
using Avalonia.Controls;
using Kei.Term.Core.Models;

namespace Kei.Term.App.Views.Controls;

// 只负责条目长什么样。拖放、右键、双击连接留在主窗口。
public partial class SessionTreeItemView : UserControl
{
    public static readonly StyledProperty<bool> HideNameProperty =
        AvaloniaProperty.Register<SessionTreeItemView, bool>(nameof(HideName));

    public bool HideName
    {
        get => GetValue(HideNameProperty);
        set => SetValue(HideNameProperty, value);
    }

    public SessionTreeItemView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ApplyKind();
        HideNameProperty.Changed.AddClassHandler<SessionTreeItemView>((view, e) => view.ToggleEditing(e.NewValue is true));
    }

    private void ToggleEditing(bool editing)
    {
        if (editing)
        {
            Classes.Add("editing");
        }
        else
        {
            Classes.Remove("editing");
        }
    }

    private void ApplyKind()
    {
        Classes.Remove("session");
        Classes.Remove("virtualRoot");
        if (DataContext is VirtualRootNode)
        {
            Classes.Add("virtualRoot");
        }
        else if (DataContext is SessionNode)
        {
            Classes.Add("session");
        }
    }
}
