using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;

namespace Kei.Term.App.Views;

// 冲突询问弹窗与 SCP 大文件确认。严格遵照 KeiTokens 令牌规范。
public sealed class TransferConflictWindow : Window
{
    public TransferConflictWindow(string fileName, TransferFileFacts source, TransferFileFacts target)
    {
        Title = Strings.Get("FileTransfer.Conflict.Title");
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        MinWidth = 480;
        MaxWidth = 580;
        Bind(BackgroundProperty, this.GetResourceObservable("Kei.Bg.Panel"));
        Bind(FontFamilyProperty, this.GetResourceObservable("Kei.Font.UI"));

        TextBlock header = Text(Strings.Format("FileTransfer.Conflict.Header", fileName), 13, isBold: true);

        // 两端文件信息卡片
        Border factsCard = new()
        {
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
        };
        factsCard.Bind(Border.BackgroundProperty, factsCard.GetResourceObservable("Kei.Bg.Card"));
        factsCard.Bind(Border.BorderBrushProperty, factsCard.GetResourceObservable("Kei.Border.Subtle"));

        Grid factsGrid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };

        TextBlock sourceLabel = Text(Strings.Get("FileTransfer.Conflict.SourceLabel"), 12);
        sourceLabel.Bind(TextBlock.ForegroundProperty, sourceLabel.GetResourceObservable("Kei.Text.Secondary"));
        Grid.SetRow(sourceLabel, 0);
        Grid.SetColumn(sourceLabel, 0);

        TextBlock sourceValue = Text(Format(source), 12);
        sourceValue.Bind(TextBlock.ForegroundProperty, sourceValue.GetResourceObservable("Kei.Text.Primary"));
        sourceValue.Margin = new Thickness(8, 0, 0, 4);
        Grid.SetRow(sourceValue, 0);
        Grid.SetColumn(sourceValue, 1);

        TextBlock targetLabel = Text(Strings.Get("FileTransfer.Conflict.TargetLabel"), 12);
        targetLabel.Bind(TextBlock.ForegroundProperty, targetLabel.GetResourceObservable("Kei.Text.Secondary"));
        Grid.SetRow(targetLabel, 1);
        Grid.SetColumn(targetLabel, 0);

        TextBlock targetValue = Text(Format(target), 12);
        targetValue.Bind(TextBlock.ForegroundProperty, targetValue.GetResourceObservable("Kei.Text.Primary"));
        targetValue.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetRow(targetValue, 1);
        Grid.SetColumn(targetValue, 1);

        factsGrid.Children.Add(sourceLabel);
        factsGrid.Children.Add(sourceValue);
        factsGrid.Children.Add(targetLabel);
        factsGrid.Children.Add(targetValue);
        factsCard.Child = factsGrid;

        CheckBox apply = new()
        {
            Content = Strings.Get("FileTransfer.Conflict.ApplyToRemaining"),
            FontSize = 12,
            Margin = new Thickness(0, 12, 0, 0),
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
            Children =
            {
                Choice(Strings.Get("FileTransfer.Conflict.Overwrite"), TransferConflictPolicy.Overwrite, apply),
                Choice(Strings.Get("FileTransfer.Conflict.Skip"), TransferConflictPolicy.Skip, apply),
                Choice(Strings.Get("FileTransfer.Conflict.OverwriteIfOlder"), TransferConflictPolicy.OverwriteIfOlder, apply),
                Choice(Strings.Get("FileTransfer.Conflict.AutoRename"), TransferConflictPolicy.AutoRename, apply),
            },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 16),
            Children = { header, factsCard, apply, buttons },
        };
    }

    public static async Task<bool> ConfirmLargeScpAsync(Window owner, string fileName, long size)
    {
        Window dialog = new()
        {
            Title = Strings.Get("FileTransfer.ScpLargeFile.Title"),
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MinWidth = 440,
            MaxWidth = 520,
        };
        dialog.Bind(BackgroundProperty, dialog.GetResourceObservable("Kei.Bg.Panel"));
        dialog.Bind(FontFamilyProperty, dialog.GetResourceObservable("Kei.Font.UI"));

        double sizeMib = (double)size / (1024 * 1024);
        string sizeText = sizeMib.ToString("F1", CultureInfo.CurrentCulture);
        TextBlock message = Text(Strings.Format("FileTransfer.ScpLargeFile.Message", fileName, sizeText), 12);

        Button cancel = new()
        {
            Content = Strings.Get("Common.Cancel"),
            MinWidth = 76,
            Height = 28,
            IsCancel = true,
        };
        Button confirm = new()
        {
            Content = Strings.Get("FileTransfer.ScpLargeFile.Continue"),
            MinWidth = 76,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true,
        };
        confirm.Classes.Add("accent");

        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 16),
            Children =
            {
                message,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { cancel, confirm },
                },
            },
        };
        return await dialog.ShowDialog<bool>(owner);
    }

    private Button Choice(string label, TransferConflictPolicy policy, CheckBox apply)
    {
        Button button = new()
        {
            Content = label,
            MinWidth = 80,
            Height = 28,
            Padding = new Thickness(10, 4),
            FontSize = 12,
        };
        if (policy == TransferConflictPolicy.Overwrite)
        {
            button.Classes.Add("accent");
        }
        button.Click += (_, _) => Close(new TransferConflictChoice(policy, apply.IsChecked == true));
        return button;
    }

    private static TextBlock Text(string value, double size, bool isBold = false)
    {
        TextBlock block = new()
        {
            Text = value,
            FontSize = size,
            FontWeight = isBold ? FontWeight.SemiBold : FontWeight.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("Kei.Text.Primary"));
        return block;
    }

    private static string Format(TransferFileFacts facts)
    {
        string sizeFormatted = FileSizeFormatter.Format(facts.Size, FileSizeDisplayMode.Iec);
        string timeFormatted = facts.ModifiedUtc == default
            ? "-"
            : facts.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        return Strings.Format("FileTransfer.Conflict.FileInfoFormat", sizeFormatted, timeFormatted);
    }
}
