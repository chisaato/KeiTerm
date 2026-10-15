using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.App.Workspaces;
using Xunit;
using TabPlacement = Kei.Term.Core.Models.Profiles.TabPlacement;

namespace Kei.Term.Tests;

public sealed partial class DockTabThemeContractTests
{
    [Theory]
    [InlineData(TabPlacement.Top)]
    [InlineData(TabPlacement.Bottom)]
    public Task HorizontalTabViewport_UsesAvailableWidthAfterAddingAndResizing(TabPlacement placement) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        await using MainViewModel model = CreateModel();
        model.TabPlacement = placement;
        MainWindow window = new() { DataContext = model, Width = 1000, Height = 700 };
        try
        {
            window.Show();
            for (int i = 0; i < 3; i++)
            {
                model.NewTabCommand.Execute(null);
                HeadlessAvalonia.Pump();
                AssertViewportFillsStrip(window);
            }
            window.Width = 800;
            HeadlessAvalonia.Pump();
            AssertViewportFillsStrip(window);
            window.Width = 1200;
            HeadlessAvalonia.Pump();
            AssertViewportFillsStrip(window);

            // 标签超过一屏时仍须滚动到当前标签，不能靠禁用裁剪掩盖问题。
            for (int i = 0; i < 12; i++)
            {
                model.NewTabCommand.Execute(null);
                HeadlessAvalonia.Pump();
            }
            DocumentTabStripItem selected = window.GetVisualDescendants().OfType<DocumentTabStripItem>().Single(control => control.IsSelected);
            ScrollViewer viewport = selected.GetVisualAncestors().OfType<ScrollViewer>().First();
            Point origin = selected.TranslatePoint(default, viewport)!.Value;
            Assert.True(viewport.Extent.Width > viewport.Viewport.Width);
            Assert.InRange(origin.X, 0, viewport.Bounds.Width - selected.Bounds.Width);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(TabPlacement.Top)]
    [InlineData(TabPlacement.Bottom)]
    public Task ClickingTab_MovesAccentIndicatorAndDistinctBackground(TabPlacement placement) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        await using MainViewModel model = CreateModel();
        model.TabPlacement = placement;
        MainWindow window = new() { DataContext = model, Width = 1000, Height = 700 };
        try
        {
            window.Show();
            model.NewTabCommand.Execute(null);
            model.NewTabCommand.Execute(null);
            HeadlessAvalonia.Pump();
            DocumentTabStripItem[] headers = window.GetVisualDescendants().OfType<DocumentTabStripItem>().ToArray();
            Assert.Equal(2, headers.Length);
            AssertSelectionAppearance(window, headers[1], headers[0], placement);

            // 实际点击标题区域，不能只修改 IsSelected 来验证样式。
            foreach (int index in new[] { 0, 1 })
            {
                DocumentTabStripItem header = headers[index];
                Point point = header.TranslatePoint(new Point(20, header.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                window.MouseMove(new Point(0, 0));
                HeadlessAvalonia.Pump();
                Assert.Same(model.NewTabs[index], model.ActiveWorkspaceTab);
                AssertSelectionAppearance(window, header, headers[1 - index], placement);
            }
        }
        finally { window.Close(); }
    });

    private static void AssertViewportFillsStrip(Window window)
    {
        DocumentTabStrip strip = Assert.Single(window.GetVisualDescendants().OfType<DocumentTabStrip>());
        ScrollViewer viewport = strip.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "PART_ScrollViewer");
        Assert.InRange(viewport.Bounds.Width, strip.Bounds.Width - 1, strip.Bounds.Width);
    }

    private static void AssertSelectionAppearance(Window window, DocumentTabStripItem selected, DocumentTabStripItem inactive, TabPlacement placement)
    {
        Assert.True(selected.IsSelected);
        Assert.False(inactive.IsSelected);
        Border selectedBody = selected.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_TabBody");
        Border inactiveBody = inactive.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "PART_TabBody");
        Color accent = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource("Kei.Accent")).Color;
        Assert.Equal(accent, Assert.IsAssignableFrom<ISolidColorBrush>(selectedBody.BorderBrush).Color);
        Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(inactiveBody.BorderBrush).Color.A);
        double indicatorThickness = placement == TabPlacement.Top ? selectedBody.BorderThickness.Top : selectedBody.BorderThickness.Bottom;
        Assert.True(indicatorThickness >= 2);
        Assert.NotEqual(Assert.IsAssignableFrom<ISolidColorBrush>(inactiveBody.Background).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(selectedBody.Background).Color);
        string titleText = Assert.IsAssignableFrom<WorkspaceDocument>(selected.DataContext).Title;
        TextBlock title = selected.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Text == titleText);
        Assert.Equal(FontWeight.SemiBold, title.FontWeight);
        Panel separator = window.GetVisualDescendants().OfType<Panel>().Single(control => control.Name == "PART_DocumentSeperator");
        Color separatorColor = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource("Kei.Border.Subtle")).Color;
        Assert.Equal(separatorColor, Assert.IsAssignableFrom<ISolidColorBrush>(separator.Background).Color);
    }
}
