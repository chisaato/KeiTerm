using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
            AssertVisibleBounds(geometry, key, ExpectedVisibleSpan(key));
        }

        foreach (FieldInfo category in typeof(SettingsIcons).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            Geometry geometry = Assert.IsAssignableFrom<Geometry>(LucideIconGeometryConverter.Instance.Convert(
                category.GetRawConstantValue(), typeof(Geometry), null, CultureInfo.InvariantCulture));
            AssertVisibleBounds(geometry, category.Name, 20);
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
        var samples = new System.Collections.Generic.List<(Viewbox viewport, string name, int size)>();
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
                samples.Add((viewport, keys[i], size));
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
            using WriteableBitmap frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame");
            using (ILockedFramebuffer pixels = frame.Lock())
            {
                foreach ((Viewbox viewport, string name, int size) in samples)
                    AssertVisiblePixels(pixels, window, viewport, name, size);
            }
            // 可选输出与断言使用同一帧，便于人工检查细长图标的视觉补偿。
            string? preview = Environment.GetEnvironmentVariable("KEITERM_ICON_PREVIEW");
            if (!string.IsNullOrEmpty(preview)) frame.Save(preview, PngBitmapEncoderOptions.Default);
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
            Assert.Equal(new Size(24, 24), icon.Bounds.Size);
            Assert.Equal(new Size(16, 16), viewport.Bounds.Size);
            Assert.Null(icon.Fill);
            Assert.Equal(Stretch.None, icon.Stretch);
            Assert.Equal(2, icon.StrokeThickness);
            Assert.Equal(PenLineCap.Round, icon.StrokeLineCap);
            Assert.Equal(PenLineJoin.Round, icon.StrokeJoin);
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

    private static void AssertVisibleBounds(Geometry geometry, string name, double expectedSpan)
    {
        Pen stroke = new(Brushes.White, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        Rect bounds = geometry.GetWidenedGeometry(stroke).Bounds;
        Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{name} has no visible geometry");
        Assert.True(bounds.Left >= -0.01 && bounds.Top >= -0.01 && bounds.Right <= 24.01 && bounds.Bottom <= 24.01,
            $"{name} exceeds its 24×24 viewport: {bounds}");
        // Skia 曲线描边的细分在变换后会有亚像素误差；24px 下允许 0.3px。
        Assert.True(Math.Abs(Math.Max(bounds.Width, bounds.Height) - expectedSpan) < 0.3,
            $"{name} has inconsistent visible size: {bounds}");
        Assert.True(Math.Abs(bounds.Center.X - 12) < 0.01 && Math.Abs(bounds.Center.Y - 12) < 0.01,
            $"{name} is not visually centered: {bounds}");
    }

    private static void AssertVisiblePixels(ILockedFramebuffer pixels, Window window, Viewbox viewport, string name, int size)
    {
        Point origin = viewport.TranslatePoint(default, window)!.Value;
        int left = (int)Math.Round(origin.X * window.RenderScaling);
        int top = (int)Math.Round(origin.Y * window.RenderScaling);
        int extent = (int)Math.Round(size * window.RenderScaling);
        int minX = extent, minY = extent, maxX = -1, maxY = -1;
        Assert.True(pixels.Format == PixelFormats.Bgra8888 || pixels.Format == PixelFormats.Rgba8888);
        for (int y = 0; y < extent; y++)
        for (int x = 0; x < extent; x++)
        {
            // 白色描边在黑色背景上的覆盖率，避免极淡的抗锯齿像素放大可见边界。
            byte green = Marshal.ReadByte(pixels.Address, (top + y) * pixels.RowBytes + (left + x) * 4 + 1);
            if (green < 64) continue;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        Assert.True(minX > 0 && minY > 0 && maxX < extent - 1 && maxY < extent - 1,
            $"{name} at {size}px touches the viewport edge: ({minX},{minY})–({maxX},{maxY})");
        double expected = size * window.RenderScaling * ExpectedVisibleSpan(name) / 24;
        int visible = Math.Max(maxX - minX + 1, maxY - minY + 1);
        Assert.True(Math.Abs(visible - expected) <= 2, $"{name} at {size}px renders at {visible}px, expected ~{expected:F1}px");
        Assert.True(Math.Abs((minX + maxX + 1) / 2.0 - extent / 2.0) <= 1
            && Math.Abs((minY + maxY + 1) / 2.0 - extent / 2.0) <= 1, $"{name} at {size}px renders off center");
    }

    private static double ExpectedVisibleSpan(string name) => name switch
    {
        "Kei.Icon.Connect" => 21.2,
        "Kei.Icon.QuickConnect" => 20.8,
        "Kei.Icon.Disconnect" => 20.6,
        _ => 20
    };

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
