using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Kei.Term.App.Services;

namespace Kei.Term.Tests;

// 验证实际模板布局和 Skia 绘制的四条边，不只检查控件自身声明的 BorderThickness。
public class InputBorderRenderingTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("multiline")]
    [InlineData("combo")]
    [InlineData("editableCombo")]
    [InlineData("number")]
    [InlineData("numberLeft")]
    public Task InputFrame_RemainsClosedInNormalHoverFocusAndDisabledStates(string kind) => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        Control input = CreateInput(kind);
        input.Width = 240;
        input.HorizontalAlignment = HorizontalAlignment.Left;
        Button outside = new() { Content = "Outside", HorizontalAlignment = HorizontalAlignment.Left };
        StackPanel content = new() { Margin = new Thickness(20), Spacing = 12, Children = { input, outside } };
        Window window = new() { Width = 320, Height = 220, Content = content };
        try
        {
            window.Show();
            outside.Focus();
            Move(window, outside);
            AssertFrameFits(input);
            AssertFramePixels(window, input, "Kei.Border");

            Move(window, input);
            AssertFrameFits(input);
            AssertFramePixels(window, input, "Kei.Text.Muted");

            Move(window, outside);
            Control editor = input.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(text => text.IsEffectivelyVisible) ?? input;
            Assert.True(editor.Focus(NavigationMethod.Tab));
            HeadlessAvalonia.Pump();
            AssertFrameFits(input);
            AssertFramePixels(window, input, "Kei.Border.Focus");

            outside.Focus();
            input.IsEnabled = false;
            HeadlessAvalonia.Pump();
            AssertFrameFits(input);
            AssertFramePixels(window, input, "Kei.Border.Subtle");
        }
        finally { window.Close(); }
    });

    private static Control CreateInput(string kind) => kind switch
    {
        "text" => new TextBox { Text = "A host name" },
        "multiline" => new TextBox { Text = "First line\nSecond line", AcceptsReturn = true, Height = 72 },
        "combo" => new ComboBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 },
        "editableCombo" => new ComboBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0, IsEditable = true },
        "number" => new NumericUpDown { Value = 14, Minimum = 8, Maximum = 36 },
        "numberLeft" => new NumericUpDown { Value = 14, Minimum = 8, Maximum = 36, ButtonSpinnerLocation = Location.Left },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void AssertFrameFits(Control input)
    {
        Control owner = input is NumericUpDown
            ? input.GetVisualDescendants().OfType<ButtonSpinner>().Single(spinner => spinner.Name == "PART_Spinner") : input;
        Border frame = input switch
        {
            TextBox => owner.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_BorderElement"),
            ComboBox => owner.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "Background"),
            _ => owner.GetVisualDescendants().OfType<Border>().Single(border => ReferenceEquals(border.TemplatedParent, owner))
        };
        Point top = frame.TranslatePoint(default, input)!.Value;
        Assert.True(top.Y >= -0.01 && top.Y + frame.Bounds.Height <= input.Bounds.Height + 0.01,
            $"{input.GetType().Name}: control={input.Bounds.Height}, frameTop={top.Y}, frameHeight={frame.Bounds.Height}, ownerMinHeight={owner.MinHeight}");
        Assert.Equal(new Thickness(1), frame.BorderThickness);
    }

    private static void AssertFramePixels(Window window, Control input, string token)
    {
        Color expected = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource(token)).Color;
        using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame");
        using ILockedFramebuffer pixels = frame.Lock();
        Point origin = input.TranslatePoint(default, window)!.Value;
        double scale = window.RenderScaling;
        int left = (int)Math.Round(origin.X * scale);
        int top = (int)Math.Round(origin.Y * scale);
        int width = (int)Math.Round(input.Bounds.Width * scale);
        int height = (int)Math.Round(input.Bounds.Height * scale);
        (int x, int y, string edge)[] edges =
        [
            (left + width / 2, top, "top"),
            (left + width / 2, top + height - 1, "bottom"),
            (left, top + height / 2, "left"),
            (left + width - 1, top + height / 2, "right")
        ];
        foreach ((int x, int y, string edge) in edges)
        {
            Color actual = ReadPixel(pixels, x, y);
            Assert.True(expected == actual, $"{input.GetType().Name} {edge}: expected {token}={expected}, actual={actual}\n"
                + string.Join("\n", input.GetVisualDescendants().OfType<Border>().Select(border =>
                    $"{border.Name ?? "border"} owner={border.TemplatedParent?.GetType().Name} background={border.Background} stroke={border.BorderBrush} thickness={border.BorderThickness} bounds={border.Bounds}"))
                + "\n" + string.Join("\n", input.GetVisualDescendants().OfType<TextBox>().Select(text =>
                    $"{text.Name} owner={text.TemplatedParent?.GetType().Name} background={text.Background} bounds={text.Bounds}")));
        }
    }

    private static Color ReadPixel(ILockedFramebuffer pixels, int x, int y)
    {
        int offset = y * pixels.RowBytes + x * 4;
        byte first = Marshal.ReadByte(pixels.Address, offset);
        byte green = Marshal.ReadByte(pixels.Address, offset + 1);
        byte third = Marshal.ReadByte(pixels.Address, offset + 2);
        byte alpha = Marshal.ReadByte(pixels.Address, offset + 3);
        Assert.True(pixels.Format == PixelFormats.Bgra8888 || pixels.Format == PixelFormats.Rgba8888);
        return pixels.Format == PixelFormats.Bgra8888 ? Color.FromArgb(alpha, third, green, first) : Color.FromArgb(alpha, first, green, third);
    }

    private static void Move(Window window, Control control)
    {
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        HeadlessAvalonia.Pump();
    }
}
