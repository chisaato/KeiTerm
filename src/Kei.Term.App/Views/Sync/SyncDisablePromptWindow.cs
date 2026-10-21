using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Kei.Term.App.Helpers;

namespace Kei.Term.App.Views.Sync;

/// <summary>
/// 停用同步确认弹窗（null 取消、false 仅停用、true 停用并删除远端主包及历史）
/// </summary>
public sealed class SyncDisablePromptWindow : Window
{
    public SyncDisablePromptWindow()
    {
        Title = "停用端到端加密同步";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;
        Bind(BackgroundProperty, this.GetResourceObservable("Kei.Bg.Panel"));
        Bind(FontFamilyProperty, this.GetResourceObservable("Kei.Font.UI"));

        var header = new TextBlock
        {
            Text = "停用当前设备的同步配置",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
        header.Bind(TextBlock.ForegroundProperty, header.GetResourceObservable("Kei.Text.Primary"));

        var description = new TextBlock
        {
            Text = "停用后，本机会清空同步口令并停止所有定时及自动同步，本机已有数据不受影响。\n\n请选择是否同时清理远端存储位置中的同步包与历史版本：",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        description.Bind(TextBlock.ForegroundProperty, description.GetResourceObservable("Kei.Text.Secondary"));

        var deleteRemoteCheck = new CheckBox
        {
            Content = "同时删除远端存储中的同步主包与全部历史版本",
            FontSize = 12,
            IsChecked = false,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var warning = new TextBlock
        {
            Text = "注意：删除远端数据不会影响其他已同步设备上的本地数据，但无法撤回已同步内容，也不会撤销或注销 WebDAV/S3 存储账号的访问凭据。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        warning.Bind(TextBlock.ForegroundProperty, warning.GetResourceObservable("Kei.Text.Muted"));

        var cancelBtn = new Button
        {
            Content = Strings.Get("Common.Cancel"),
            MinWidth = 80,
            Height = 28,
            IsCancel = true,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        cancelBtn.Click += (_, _) => Close(null);

        var confirmBtn = new Button
        {
            Content = "确认停用",
            MinWidth = 90,
            Height = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        confirmBtn.Classes.Add("danger");
        confirmBtn.Click += (_, _) => Close(deleteRemoteCheck.IsChecked == true);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 16),
            Children =
            {
                header,
                description,
                deleteRemoteCheck,
                warning,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancelBtn, confirmBtn }
                }
            }
        };
    }
}
