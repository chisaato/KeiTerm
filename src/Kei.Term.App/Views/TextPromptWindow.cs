using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Kei.Term.App.Helpers;

namespace Kei.Term.App.Views;

// 单行文本输入框（重命名等）：确认返回输入文本，取消 / 关闭返回 null
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _input;

    public TextPromptWindow(string title, string label, string? initialText)
    {
        Title = title;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Bind(BackgroundProperty, this.GetResourceObservable("Kei.Bg.Panel"));

        var caption = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 6)
        };
        caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("Kei.Text.Primary"));

        _input = new TextBox { Text = initialText ?? string.Empty, MinWidth = 320 };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Close(_input.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
        };

        var cancel = new Button { Content = Strings.Get("Common.Cancel"), Padding = new Thickness(14, 5), MinWidth = 76 };
        var confirm = new Button
        {
            Content = Strings.Get("Common.Confirm"),
            Padding = new Thickness(14, 5),
            MinWidth = 76,
            Margin = new Thickness(8, 0, 0, 0)
        };
        confirm.Bind(BackgroundProperty, confirm.GetResourceObservable("Kei.Accent"));
        confirm.Bind(ForegroundProperty, confirm.GetResourceObservable("Kei.Accent.Foreground"));
        cancel.Click += (_, _) => Close(null);
        confirm.Click += (_, _) => Close(_input.Text);

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 16),
            Children =
            {
                caption,
                _input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { cancel, confirm }
                }
            }
        };

        Opened += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }
}
