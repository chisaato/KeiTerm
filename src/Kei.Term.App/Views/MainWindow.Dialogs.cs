using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Kei.Term.App.Helpers;

namespace Kei.Term.App.Views;

public partial class MainWindow
{
    private void OnAboutClick(object? sender, EventArgs args) => _ = ShowAboutDialogAsync();

    // 关于窗口与 XAML 弹窗共享动态字体、颜色与紧凑控件样式。
    private static Window CreateThemedDialog(string title)
    {
        Window dialog = new()
        {
            Title = title,
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        dialog.Bind(FontFamilyProperty, dialog.GetResourceObservable("Kei.Font.UI"));
        dialog.Bind(BackgroundProperty, dialog.GetResourceObservable("Kei.Bg.Panel"));
        dialog.Bind(BorderBrushProperty, dialog.GetResourceObservable("Kei.Border"));
        return dialog;
    }

    private static TextBlock CreateDialogText(string text, string colorKey, double fontSize = 12)
    {
        TextBlock block = new() { Text = text, FontSize = fontSize, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(colorKey));
        return block;
    }

    public async Task ShowAboutDialogAsync()
    {
        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
        Window about = CreateThemedDialog(Strings.Get("About.Title"));
        TextBlock title = CreateDialogText(Strings.Get("About.AppName"), "Kei.Text.Primary", 14);
        title.FontWeight = FontWeight.SemiBold;
        Button close = new() { Content = Strings.Get("Common.Confirm"), IsDefault = true, IsCancel = true,
            MinWidth = 76, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => about.Close();
        about.Content = new StackPanel
        {
            Margin = new Thickness(20, 18), Spacing = 8,
            Children =
            {
                title,
                CreateDialogText(string.Format(Strings.Get("About.VersionFormat"), version), "Kei.Text.Secondary"),
                CreateDialogText(Strings.Get("About.Description"), "Kei.Text.Secondary"),
                close
            }
        };
        await about.ShowDialog(this);
    }

}
