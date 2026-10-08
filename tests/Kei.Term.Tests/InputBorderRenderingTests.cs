using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kei.Term.App.Services;

namespace Kei.Term.Tests;

// 验证实际模板布局：边框不超出控件，自动完成的文字 viewport 不被裁切。
public class InputBorderRenderingTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("combo")]
    public Task InputFrame_RemainsClosedInNormalHoverFocusAndDisabledStates(string kind) => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        AssertFrameSurvivesStates(CreateInput(kind));
        // 原字体自动完成用例并入 text 行，只保留 viewport 不裁切。
        if (kind == "text") AssertAutocompleteTextIsNotClipped();
    });

    private static Control CreateInput(string kind) => kind switch
    {
        "text" => new TextBox { Text = "A host name" },
        "combo" => new ComboBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 },
        "autocomplete" => new AutoCompleteBox { Text = "PingFang SC · AaBb gypq 中文", ItemsSource = new[] { "PingFang SC", "DejaVu Sans" } },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void AssertFrameSurvivesStates(Control input)
    {
        input.Width = 240;
        input.HorizontalAlignment = HorizontalAlignment.Left;
        Button outside = new() { Content = "Outside", HorizontalAlignment = HorizontalAlignment.Left };
        Window window = new()
        {
            Width = 320,
            Height = 220,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { input, outside } }
        };
        try
        {
            window.Show();
            outside.Focus();
            Move(window, outside);
            AssertFrameFits(input);

            Move(window, input);
            AssertFrameFits(input);

            Move(window, outside);
            Control editor = input.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(text => text.IsEffectivelyVisible) ?? input;
            Assert.True(editor.Focus(NavigationMethod.Tab));
            HeadlessAvalonia.Pump();
            AssertFrameFits(input);

            outside.Focus();
            input.IsEnabled = false;
            HeadlessAvalonia.Pump();
            AssertFrameFits(input);
        }
        finally { window.Close(); }
    }

    private static void AssertAutocompleteTextIsNotClipped()
    {
        AutoCompleteBox input = (AutoCompleteBox)CreateInput("autocomplete");
        input.Width = 240;
        Button outside = new() { Content = "Outside" };
        Window window = new()
        {
            Width = 320,
            Height = 180,
            FontFamily = (FontFamily)Application.Current!.FindResource("Kei.Font.UI")!,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { input, outside } }
        };
        try
        {
            window.Show();
            outside.Focus();
            Move(window, outside);
            AssertTextFits(input);
            Move(window, input);
            AssertTextFits(input);
            TextBox editor = input.GetVisualDescendants().OfType<TextBox>().Single(text => text.Name == "PART_TextBox");
            editor.Focus();
            HeadlessAvalonia.Pump();
            AssertTextFits(input);
        }
        finally { window.Close(); }
    }

    private static void AssertTextFits(AutoCompleteBox input)
    {
        TextBox editor = input.GetVisualDescendants().OfType<TextBox>().Single(text => text.Name == "PART_TextBox");
        ScrollViewer viewport = editor.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Name == "PART_ScrollViewer");
        TextPresenter text = editor.GetVisualDescendants().OfType<TextPresenter>().Single();
        Assert.True(viewport.Viewport.Height + 0.01 >= text.TextLayout.Height,
            $"AutoCompleteBox text is clipped: viewport={viewport.Viewport.Height}, text={text.TextLayout.Height}, padding={editor.Padding}");
        AssertFrameFits(input);
    }

    private static void AssertFrameFits(Control input)
    {
        Control owner = input switch
        {
            AutoCompleteBox => input.GetVisualDescendants().OfType<TextBox>().Single(text => text.Name == "PART_TextBox"),
            _ => input
        };
        Border frame = input switch
        {
            TextBox => owner.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_BorderElement"),
            ComboBox => owner.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "Background"),
            AutoCompleteBox => owner.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_BorderElement"),
            _ => owner.GetVisualDescendants().OfType<Border>().Single(border => ReferenceEquals(border.TemplatedParent, owner))
        };
        Point top = frame.TranslatePoint(default, input)!.Value;
        Assert.True(top.Y >= -0.01 && top.Y + frame.Bounds.Height <= input.Bounds.Height + 0.01,
            $"{input.GetType().Name}: control={input.Bounds.Height}, frameTop={top.Y}, frameHeight={frame.Bounds.Height}, ownerMinHeight={owner.MinHeight}");
    }

    private static void Move(Window window, Control control)
    {
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        HeadlessAvalonia.Pump();
    }
}
