using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Kei.Term.App.Converters;
using Kei.Term.App.Services;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Models.Profiles.BuiltIns.Gui;
using System.Globalization;

namespace Kei.Term.Tests;

public class GuiThemeTokenTests
{
    [Fact]
    public Task SwitchingGuiTheme_UpdatesDerivedSurfacesAndDockColorAliases() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        var originalResources = Application.Current!.Resources.ToDictionary(pair => pair.Key, pair => pair.Value);
        Border dockIndicator = new();
        dockIndicator.Bind(Border.BackgroundProperty, new DynamicResourceExtension("DockSplitterHoverBrush"));
        Border input = new();
        input.Bind(Border.BackgroundProperty, new DynamicResourceExtension("Kei.Bg.Input"));
        Grid grid = new();
        grid.Children.Add(dockIndicator);
        grid.Children.Add(input);
        Window window = new() { Width = 300, Height = 200, Content = grid };
        window.Show();
        try
        {
            string[] derivedKeys = ["Kei.Bg.Card", "Kei.Bg.Input", "Kei.Bg.Hover", "Kei.Bg.Pressed",
                "Kei.Bg.TabInactive", "Kei.Text.Muted", "Kei.Status.Disconnected", "Kei.Border.Focus", "Kei.Accent.Pressed"];
            ProfileManagerService.ApplyGuiProfile(NordGuiPreset.Instance);
            HeadlessAvalonia.Pump();
            var previous = derivedKeys.ToDictionary(key => key, key => BrushColor(window.FindResource(key)));
            Assert.Equal(Color.Parse(NordGuiPreset.Instance.AccentColor), BrushColor(dockIndicator.Background));
            Assert.Equal(Color.Parse(NordGuiPreset.Instance.WindowBackground), BrushColor(input.Background));

            ProfileManagerService.ApplyGuiProfile(VeritasKotamaGuiPreset.Instance);
            HeadlessAvalonia.Pump();
            Assert.Equal(Color.Parse(VeritasKotamaGuiPreset.Instance.AccentColor), BrushColor(dockIndicator.Background));
            Assert.Equal(Color.Parse(VeritasKotamaGuiPreset.Instance.WindowBackground), BrushColor(input.Background));
            foreach (string key in derivedKeys)
            {
                Color current = BrushColor(window.FindResource(key));
                Assert.NotEqual(previous[key], current);
                Assert.Equal(current, Assert.IsType<Color>(window.FindResource("Kei.Color." + key["Kei.".Length..])));
            }
            Assert.Equal(BrushColor(window.FindResource("Kei.Accent.Pressed")), BrushColor(window.FindResource("DockSplitterDragBrush")));
            Assert.Equal(BrushColor(window.FindResource("Kei.Text.Secondary")), BrushColor(window.FindResource("DockTabForegroundBrush")));
            ConnectionStateBrushConverter converter = new();
            Assert.Equal(BrushColor(window.FindResource("Kei.Text.Muted")), BrushColor(converter.Convert(null, typeof(IBrush), null, CultureInfo.InvariantCulture)));
        }
        finally
        {
            window.Close();
            foreach (object key in Application.Current!.Resources.Keys.ToArray()) Application.Current.Resources.Remove(key);
            foreach (var resource in originalResources) Application.Current.Resources[resource.Key] = resource.Value;
        }
    });

    [Fact]
    public Task MutedText_RemainsReadableAcrossBuiltInGuiThemes() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        var originalResources = Application.Current!.Resources.ToDictionary(pair => pair.Key, pair => pair.Value);
        TextBlock label = new() { Text = "Placeholder and disabled text" };
        label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Kei.Text.Muted"));
        Window window = new() { Content = label, Width = 300, Height = 200 };
        window.Show();
        try
        {
            foreach (GuiProfile profile in BuiltInPresets.DefaultGuiProfiles)
            {
                ProfileManagerService.ApplyGuiProfile(profile);
                HeadlessAvalonia.Pump();
                Color foreground = BrushColor(label.Foreground);
                foreach (string surfaceKey in new[] { "Kei.Bg.Window", "Kei.Bg.Panel", "Kei.Bg.PanelAlt", "Kei.Bg.Input" })
                {
                    Color background = BrushColor(window.FindResource(surfaceKey));
                    double light = Math.Max(Luminance(foreground), Luminance(background));
                    double dark = Math.Min(Luminance(foreground), Luminance(background));
                    Assert.True((light + 0.05) / (dark + 0.05) >= 3, $"{profile.Name}: muted text is unreadable on {surfaceKey}");
                }
            }
        }
        finally
        {
            window.Close();
            foreach (object key in Application.Current!.Resources.Keys.ToArray()) Application.Current.Resources.Remove(key);
            foreach (var resource in originalResources) Application.Current.Resources[resource.Key] = resource.Value;
        }
    });

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static Color BrushColor(object? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
