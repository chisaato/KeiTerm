using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kei.Term.App.Services;

namespace Kei.Term.Tests;

public class SettingsFontSymbolsRenderingTests
{
    [Fact]
    public Task SharedFontRows_KeepTwoAlignedSlotsAndAccessibleVariantSymbols() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        Window window = new() { Width = 272, Height = 270 };
        window.Background = Assert.IsAssignableFrom<IBrush>(window.FindResource("Kei.Bg.Window"));
        window.Foreground = Assert.IsAssignableFrom<IBrush>(window.FindResource("Kei.Text.Primary"));
        window.Styles.Add(new StyleInclude(new Uri("avares://Kei.Term.App/"))
        {
            Source = new Uri("avares://Kei.Term.App/Views/Settings/SettingsStyles.axaml")
        });
        IDataTemplate template = Assert.IsAssignableFrom<IDataTemplate>(window.FindResource("SettingsFontOptionTemplate"));
        StackPanel sheet = new() { Margin = new Thickness(16), Spacing = 4 };
        FontFamilyOption[] options = [new("JetBrains Mono", true), new("JetBrainsMono Nerd Font Mono", true),
            new("JetBrainsMono Nerd Font", true), new("JetBrainsMono Nerd Font Propo", false), new("Sans", false)];
        foreach (FontFamilyOption option in options)
            sheet.Children.Add(new ContentControl { Content = option, ContentTemplate = template, Height = 28 });
        sheet.Children.Add(new ContentControl
        {
            Template = Assert.IsAssignableFrom<IControlTemplate>(window.FindResource("SettingsFontLegendTemplate"))
        });
        window.Content = sheet;
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            Grid[] slots = sheet.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Classes.Contains("fontSymbols")).ToArray();
            Assert.Equal(options.Length, slots.Length);
            double? left = null;
            for (int i = 0; i < slots.Length; i++)
            {
                Grid slot = slots[i];
                Assert.Equal(48, slot.Bounds.Width);
                double x = slot.TranslatePoint(default, window)!.Value.X;
                if (left is not null) Assert.Equal(left.Value, x);
                left = x;
                var visible = slot.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                    .Where(path => path.IsEffectivelyVisible).ToArray();
                Assert.Equal(i switch { 0 => 1, 1 or 2 => 2, 3 => 1, _ => 0 }, visible.Length);
                foreach (Avalonia.Controls.Shapes.Path icon in visible)
                {
                    Border description = icon.GetVisualAncestors().OfType<Border>().First();
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(description)));
                    Assert.Equal(AutomationProperties.GetName(description), ToolTip.GetTip(description));
                    Assert.Equal(16, icon.GetVisualAncestors().OfType<Viewbox>().First().Bounds.Width);
                    Rect ink = icon.Data!.GetWidenedGeometry(new Pen(Brushes.White, 2,
                        lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)).Bounds;
                    Assert.True(ink.Left >= 0 && ink.Top >= 0 && ink.Right <= 24 && ink.Bottom <= 24);
                }
            }
            Point center = slots[1].TranslatePoint(new Point(12, 12), window)!.Value;
            window.MouseMove(center, RawInputModifiers.None);
            HeadlessAvalonia.Pump();
            foreach (Avalonia.Controls.Shapes.Path icon in slots[1].GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>())
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource("Kei.Text.Secondary")).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(icon.Stroke).Color);
            string? preview = Environment.GetEnvironmentVariable("KEITERM_FONT_SYMBOL_PREVIEW");
            if (!string.IsNullOrEmpty(preview))
            {
                using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame");
                frame.Save(preview, PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    });
}
