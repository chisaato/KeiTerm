using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Sync;

namespace Kei.Term.App.Views.Sync;

/// <summary>
/// 批量删除确认弹窗（防误删安全网，显示删除数与存活数，默认拒绝取消）
/// </summary>
public sealed class SyncDeletionConfirmationWindow : Window
{
    public SyncDeletionConfirmationWindow(SyncConfirmation preview)
    {
        Title = "同步批量删除安全警报";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;
        Bind(BackgroundProperty, this.GetResourceObservable("Kei.Bg.Panel"));
        Bind(FontFamilyProperty, this.GetResourceObservable("Kei.Font.UI"));

        var header = new TextBlock
        {
            Text = "警告：本次同步将删除较多本机数据",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
        header.Bind(TextBlock.ForegroundProperty, header.GetResourceObservable("Kei.Status.Error"));

        var infoCard = new Border
        {
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 12)
        };
        infoCard.Bind(Border.BackgroundProperty, infoCard.GetResourceObservable("Kei.Bg.Card"));
        infoCard.Bind(Border.BorderBrushProperty, infoCard.GetResourceObservable("Kei.Border.Subtle"));

        var infoGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };

        var delLabel = new TextBlock { Text = "待删除实体数：", FontSize = 12 };
        delLabel.Bind(TextBlock.ForegroundProperty, delLabel.GetResourceObservable("Kei.Text.Secondary"));
        Grid.SetRow(delLabel, 0);
        Grid.SetColumn(delLabel, 0);

        var delVal = new TextBlock { Text = $"{preview.DeletedCount} 个", FontSize = 12, FontWeight = FontWeight.Bold };
        delVal.Bind(TextBlock.ForegroundProperty, delVal.GetResourceObservable("Kei.Status.Error"));
        delVal.Margin = new Thickness(8, 0, 0, 4);
        Grid.SetRow(delVal, 0);
        Grid.SetColumn(delVal, 1);

        var liveLabel = new TextBlock { Text = "本机存活实体数：", FontSize = 12 };
        liveLabel.Bind(TextBlock.ForegroundProperty, liveLabel.GetResourceObservable("Kei.Text.Secondary"));
        Grid.SetRow(liveLabel, 1);
        Grid.SetColumn(liveLabel, 0);

        var liveVal = new TextBlock { Text = $"{preview.LocalLiveCount} 个", FontSize = 12 };
        liveVal.Bind(TextBlock.ForegroundProperty, liveVal.GetResourceObservable("Kei.Text.Primary"));
        liveVal.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetRow(liveVal, 1);
        Grid.SetColumn(liveVal, 1);

        infoGrid.Children.Add(delLabel);
        infoGrid.Children.Add(delVal);
        infoGrid.Children.Add(liveLabel);
        infoGrid.Children.Add(liveVal);
        infoCard.Child = infoGrid;

        var tip = new TextBlock
        {
            Text = "若这是远端或其他设备的预期变更，请确认后继续执行；否则请取消并在同步页检查快照或远端历史。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        tip.Bind(TextBlock.ForegroundProperty, tip.GetResourceObservable("Kei.Text.Muted"));

        var cancelBtn = new Button
        {
            Content = Strings.Get("Common.Cancel"),
            MinWidth = 80,
            Height = 28,
            IsCancel = true,
            IsDefault = true, // 默认焦点在取消上，防止意外回车确认
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        cancelBtn.Click += (_, _) => Close(false);

        var confirmBtn = new Button
        {
            Content = "确认删除并继续",
            MinWidth = 110,
            Height = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        confirmBtn.Classes.Add("danger");
        confirmBtn.Click += (_, _) => Close(true);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(false);
                e.Handled = true;
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 16),
            Children =
            {
                header,
                infoCard,
                tip,
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
