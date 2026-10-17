using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kei.Term.App.ViewModels.Settings;

namespace Kei.Term.App.Views.Settings;

public partial class GeneralSettingsPageView : UserControl
{
    private TopLevel? _shortcutHost;

    public GeneralSettingsPageView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 保持窗口级隧道路由：录制时即使焦点移到导航或底部按钮也能捕获按键。
        _shortcutHost = TopLevel.GetTopLevel(this);
        _shortcutHost?.AddHandler(KeyDownEvent, CommandPaletteShortcutRecorder_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _shortcutHost?.RemoveHandler(KeyDownEvent, CommandPaletteShortcutRecorder_KeyDown);
        _shortcutHost = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void CommandPaletteShortcutRecorder_KeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is GeneralSettingsPage { IsRecordingCommandPaletteShortcut: true } page)
        {
            page.RecordCommandPaletteShortcut(args.Key, args.KeyModifiers, args.PhysicalKey);
            args.Handled = true;
        }
    }
}
