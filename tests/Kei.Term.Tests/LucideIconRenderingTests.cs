using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Kei.Term.App.Converters;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels.Settings;

namespace Kei.Term.Tests;

public class LucideIconRenderingTests
{
    [Fact]
    public Task EveryIconAndSettingsCategory_HasCenteredConsistentVisibleBounds() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        XDocument icons = XDocument.Load(System.IO.Path.Combine(FindRoot(), "src/Kei.Term.App/DesignSystem/KeiIcons.axaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (XElement resource in icons.Root!.Elements())
        {
            string key = resource.Attribute(xaml + "Key")!.Value;
            Geometry geometry = Assert.IsAssignableFrom<Geometry>(Application.Current!.FindResource(key));
            AssertVisibleBounds(geometry, key);
        }

        foreach (FieldInfo category in typeof(SettingsIcons).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            Geometry geometry = Assert.IsAssignableFrom<Geometry>(LucideIconGeometryConverter.Instance.Convert(
                category.GetRawConstantValue(), typeof(Geometry), null, CultureInfo.InvariantCulture));
            AssertVisibleBounds(geometry, category.Name);
        }
    });

    [Fact]
    public Task Icons_RenderWithoutClippingAtToolbarAndRowSizes() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        XDocument resources = XDocument.Load(System.IO.Path.Combine(FindRoot(), "src/Kei.Term.App/DesignSystem/KeiIcons.axaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        Grid sheet = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*,*"), Margin = new Thickness(12) };
        string[] keys = resources.Root!.Elements().Select(resource => resource.Attribute(xaml + "Key")!.Value).ToArray();
        for (int row = 0; row < (keys.Length + 7) / 8; row++) sheet.RowDefinitions.Add(new RowDefinition(new GridLength(72)));
        for (int i = 0; i < keys.Length; i++)
        {
            StackPanel examples = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            foreach (int size in new[] { 16, 24 })
            {
                Avalonia.Controls.Shapes.Path icon = new()
                {
                    Data = (Geometry)Application.Current!.FindResource(keys[i])!,
                    Classes = { "kei-icon" }, Stroke = Brushes.White
                };
                Viewbox viewport = new() { Width = size, Height = size, Child = new Grid { Width = 24, Height = 24, Children = { icon } } };
                examples.Children.Add(viewport);
            }
            StackPanel cell = new() { Spacing = 8, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children = { examples, new TextBlock { Text = keys[i][9..], FontSize = 10,
                    Foreground = Brushes.LightGray, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center } } };
            Grid.SetColumn(cell, i % 8);
            Grid.SetRow(cell, i / 8);
            sheet.Children.Add(cell);
        }
        Window window = new() { Width = 800, Height = 384, Background = Brushes.Black, Content = sheet };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            // 像素跨度断言已去掉。设置 KEITERM_ICON_PREVIEW 时仍导出这一帧，供人工检查细长图标。
            string? preview = Environment.GetEnvironmentVariable("KEITERM_ICON_PREVIEW");
            if (!string.IsNullOrEmpty(preview))
            {
                using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame");
                frame.Save(preview, PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ToolbarIcon_PreservesRoundStrokesAndChangesColorWithButtonState() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        Avalonia.Controls.Shapes.Path icon = new()
        {
            Data = (Geometry)Application.Current!.FindResource("Kei.Icon.Terminal")!,
            Classes = { "kei-icon" }
        };
        Grid canvas = new() { Width = 24, Height = 24, Children = { icon } };
        Viewbox viewport = new() { Width = 16, Height = 16, Child = canvas };
        Button action = new() { Classes = { "iconBtn" }, Content = viewport };
        Button outside = new() { Content = "Outside" };
        Window window = new()
        {
            Width = 220, Height = 120,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 10, Children = { action, outside } }
        };
        try
        {
            window.Show();
            Move(window, outside);
            AssertStrokeColor(icon, window, "Kei.Text.Primary");

            Move(window, action);
            AssertStrokeColor(icon, window, "Kei.Text.Primary");
            action.IsEnabled = false;
            HeadlessAvalonia.Pump();
            AssertStrokeColor(icon, window, "Kei.Text.Muted");

            action.IsEnabled = true;
            action.Classes.Add("danger");
            HeadlessAvalonia.Pump();
            AssertStrokeColor(icon, window, "Kei.Status.Error");
            action.IsEnabled = false;
            HeadlessAvalonia.Pump();
            AssertStrokeColor(icon, window, "Kei.Text.Muted");
        }
        finally { window.Close(); }
    });

    private static void AssertVisibleBounds(Geometry geometry, string name)
    {
        Pen stroke = new(Brushes.White, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        Rect bounds = geometry.GetWidenedGeometry(stroke).Bounds;
        Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{name} has no visible geometry");
        Assert.True(bounds.Left >= -0.01 && bounds.Top >= -0.01 && bounds.Right <= 24.01 && bounds.Bottom <= 24.01,
            $"{name} exceeds its 24×24 viewport: {bounds}");
    }

    private static void AssertStrokeColor(Avalonia.Controls.Shapes.Path icon, Window window, string token)
        => Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource(token)).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(icon.Stroke).Color);

    private static void Move(Window window, Control control)
    {
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        HeadlessAvalonia.Pump();
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Kei.Term.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到解决方案目录");
    }
}
