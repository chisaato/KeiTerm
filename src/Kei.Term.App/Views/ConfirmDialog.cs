using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Kei.Term.App.Helpers;

namespace Kei.Term.App.Views;

public static class ConfirmDialog
{
    public static Task<bool> ShowAsync(Window owner, string title, string message)
    {
        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            FontSize = 13,
        };
        messageBlock.Bind(TextBlock.ForegroundProperty, messageBlock.GetResourceObservable("Kei.Text.Primary"));

        var cancel = new Button
        {
            Content = Strings.Get("Common.Cancel"),
            MinWidth = 76,
            IsCancel = true,
        };

        var confirm = new Button
        {
            Content = Strings.Get("Common.Confirm"),
            MinWidth = 76,
            IsDefault = true,
            Margin = new Thickness(8, 0, 0, 0),
        };
        confirm.Classes.Add("accent");

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20, 18, 20, 16),
                Spacing = 14,
                Children =
                {
                    messageBlock,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, confirm },
                    },
                },
            },
        };

        dialog.Bind(Window.BackgroundProperty, dialog.GetResourceObservable("Kei.Bg.Window"));
        dialog.Bind(Window.FontFamilyProperty, dialog.GetResourceObservable("Kei.Font.UI"));

        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);

        return dialog.ShowDialog<bool>(owner);
    }
}
