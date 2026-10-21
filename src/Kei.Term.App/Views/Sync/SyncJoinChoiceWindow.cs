using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Sync;

namespace Kei.Term.App.Views.Sync;

/// <summary>
/// 首次加入同步选择框（三选一：合并 / 以同步位置为准 / 以本机为准，默认合并，取消返回 null）
/// </summary>
public sealed class SyncJoinChoiceWindow : Window
{
    private SyncJoinChoice? _result;

    public SyncJoinChoiceWindow(SyncConfirmation preview)
    {
        Title = "首次加入同步";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;
        Bind(BackgroundProperty, this.GetResourceObservable("Kei.Bg.Panel"));
        Bind(FontFamilyProperty, this.GetResourceObservable("Kei.Font.UI"));

        var header = new TextBlock
        {
            Text = "远端已存在同步数据",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        header.Bind(TextBlock.ForegroundProperty, header.GetResourceObservable("Kei.Text.Primary"));

        var description = new TextBlock
        {
            Text = $"检测到远端同步包。本机当前存活实体：{preview.LocalLiveCount} 个。请选择首次接入的合并策略：",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        description.Bind(TextBlock.ForegroundProperty, description.GetResourceObservable("Kei.Text.Secondary"));

        var optionsCard = new Border
        {
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 16)
        };
        optionsCard.Bind(Border.BackgroundProperty, optionsCard.GetResourceObservable("Kei.Bg.Card"));
        optionsCard.Bind(Border.BorderBrushProperty, optionsCard.GetResourceObservable("Kei.Border.Subtle"));

        var radioGroup = "SyncJoinGroup";
        var mergeSub = new TextBlock { Text = "保留双方最新修改的实体，不覆盖无冲突配置", FontSize = 12 };
        mergeSub.Bind(TextBlock.ForegroundProperty, mergeSub.GetResourceObservable("Kei.Text.Muted"));
        var mergeRadio = new RadioButton
        {
            GroupName = radioGroup,
            IsChecked = true,
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = "合并双向数据（推荐）", FontSize = 13, FontWeight = FontWeight.SemiBold },
                    mergeSub
                }
            },
            Margin = new Thickness(0, 0, 0, 8)
        };

        var remoteSub = new TextBlock { Text = "用远端数据覆盖本机（若会导致大量删除，后续将提示二次确认）", FontSize = 12 };
        remoteSub.Bind(TextBlock.ForegroundProperty, remoteSub.GetResourceObservable("Kei.Text.Muted"));
        var remoteRadio = new RadioButton
        {
            GroupName = radioGroup,
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = "以同步位置（远端）为准", FontSize = 13, FontWeight = FontWeight.SemiBold },
                    remoteSub
                }
            },
            Margin = new Thickness(0, 0, 0, 8)
        };

        var localSub = new TextBlock { Text = "以当前设备的数据为准覆盖远端同步包", FontSize = 12 };
        localSub.Bind(TextBlock.ForegroundProperty, localSub.GetResourceObservable("Kei.Text.Muted"));
        var localRadio = new RadioButton
        {
            GroupName = radioGroup,
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = "以本机为准", FontSize = 13, FontWeight = FontWeight.SemiBold },
                    localSub
                }
            }
        };

        var radioStack = new StackPanel { Children = { mergeRadio, remoteRadio, localRadio } };
        optionsCard.Child = radioStack;

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
            Content = Strings.Get("Common.Confirm"),
            MinWidth = 80,
            Height = 28,
            IsDefault = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        confirmBtn.Classes.Add("accent");
        confirmBtn.Click += (_, _) =>
        {
            if (remoteRadio.IsChecked == true) _result = SyncJoinChoice.Remote;
            else if (localRadio.IsChecked == true) _result = SyncJoinChoice.Local;
            else _result = SyncJoinChoice.Merge;

            Close(_result);
        };

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
                optionsCard,
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
