using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views.Settings.Controls;

namespace Kei.Term.Tests;

public class SettingsAdaptiveColumnsBehaviorTests
{
    [Theory]
    [InlineData(120, true)]
    [InlineData(700, true)]
    [InlineData(739, true)]
    [InlineData(740, false)]
    [InlineData(900, false)]
    [InlineData(1280, false)]
    [InlineData(1920, false)]
    public Task MeasureArrange_UsesAvailableWidthAndExactBreakpoint(double width, bool narrow)
        => HeadlessAvalonia.RunAsync(() =>
        {
            Border settings = new();
            Border preview = new() { MinHeight = 73, MaxHeight = 530, VerticalAlignment = VerticalAlignment.Top };
            AdaptiveColumnsGrid grid = new() { Children = { settings, preview } };
            Layout(grid, width);
            AssertMode(grid, settings, preview, narrow);
            // 同一尺寸重复布局不得重建定义或累计分隔条。
            ColumnDefinitions columns = grid.ColumnDefinitions;
            RowDefinitions rows = grid.RowDefinitions;
            Layout(grid, width);
            Assert.Same(columns, grid.ColumnDefinitions);
            Assert.Same(rows, grid.RowDefinitions);
            AssertMode(grid, settings, preview, narrow);
        });

    [Fact]
    public Task WideNarrowWide_RestoresPreviewConstraintsAndDoesNotAccumulateSplitters()
        => HeadlessAvalonia.RunAsync(() =>
        {
            Border settings = new();
            Border preview = new() { MinHeight = 73, MaxHeight = 530, VerticalAlignment = VerticalAlignment.Top };
            AdaptiveColumnsGrid grid = new() { Children = { settings, preview } };
            foreach (double width in new double[] { 1920, 700, 1280, 120, 900, 739, 740, 1920 })
            {
                Layout(grid, width);
                AssertMode(grid, settings, preview, width < 740);
                Assert.Equal(width < 740 ? 160 : 73, preview.MinHeight);
                Assert.Equal(width < 740 ? 480 : 530, preview.MaxHeight);
                Assert.Equal(width < 740 ? VerticalAlignment.Stretch : VerticalAlignment.Top, preview.VerticalAlignment);
            }
        });

    [Fact]
    public Task SettingsWindow_ResizingUsesContentWidthRatherThanWindowWidth()
        => HeadlessAvalonia.RunAsync(async () =>
        {
            await using SettingsHeadlessTestContext context = await SettingsHeadlessTestContext.CreateAsync();
            context.Model.SelectedCategory = context.Model.Categories.Single(c => c.Page is AppearanceSettingsPage);
            foreach (int width in new[] { 1920, 900, 1280, 700, 1920 })
            {
                context.Window.Width = width;
                context.Layout();
                AdaptiveColumnsGrid grid = context.Window.GetVisualDescendants().OfType<AdaptiveColumnsGrid>().Single(g => g.IsEffectivelyVisible);
                AssertMode(grid, grid.Children[0], grid.Children[^1], grid.Bounds.Width < grid.NarrowThreshold);
                SettingsHeadlessTestContext.AssertVisible(grid.Children[^1]);
            }
        });

    private static void Layout(AdaptiveColumnsGrid grid, double width)
    {
        grid.InvalidateMeasure();
        grid.Measure(new Size(width, 760));
        grid.Arrange(new Rect(0, 0, width, 760));
    }

    private static void AssertMode(AdaptiveColumnsGrid grid, Control settings, Control preview, bool narrow)
    {
        Assert.Equal(narrow, grid.IsNarrow);
        Assert.Equal(narrow ? 1 : 2, grid.ColumnDefinitions.Count);
        Assert.Equal(narrow ? 3 : 0, grid.RowDefinitions.Count);
        Assert.Equal(narrow ? 1 : 0, grid.Children.OfType<GridSplitter>().Count());
        Assert.Equal(narrow ? 2 : 0, Grid.GetRow(preview));
        Assert.Equal(narrow ? 0 : 1, Grid.GetColumn(preview));
        Assert.Equal(0, Grid.GetRow(settings));
        Assert.Equal(0, Grid.GetColumn(settings));
        if (narrow) Assert.True(preview.Bounds.Top >= settings.Bounds.Bottom);
        else Assert.True(preview.Bounds.Left >= settings.Bounds.Right);
    }
}
