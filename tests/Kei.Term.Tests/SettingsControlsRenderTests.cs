using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.App.Views.Controls;
using Kei.Term.App.Views.Settings.Controls;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;

namespace Kei.Term.Tests;

// 设置行/卡片内容丢失（高度为 0）与下钻行窄宽度挤压的回归测试：遍历所有分类页和下钻子页。
public class SettingsControlsRenderTests
{
    [Theory]
    [InlineData(900)]
    [InlineData(1280)]
    public Task AllSettingsPages_RenderRowsCardsAndDrillDownRowsWithVisibleSize(int width)
        => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_settings_render_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        SettingsWindow? window = null;
        try
        {
            string connection = $"Data Source={Path.Combine(directory, "render.db")}";
            SqliteTreeRepository tree = new(connection);
            await tree.InitializeAsync();
            JsonSettingsService service = new(Path.Combine(directory, "settings.json"));
            SettingsViewModel model = new(service, directory, proxyRepo: new SqliteProxyRepository(connection));
            window = new SettingsWindow(model) { Width = width, Height = 760 };
            window.Show();

            int checkedControls = 0;
            List<(string Context, int Group, double Width)> cardWidths = new();
            foreach (SettingsCategoryItem category in model.Categories)
            {
                if (category.IsHeader) continue;
                model.SelectedCategory = category;
                string name = category.Page.GetType().Name;
                checkedControls += Verify(window, name, cardWidths);

                // 导出截图供人工与自动化视觉检查
                string? screenshotDirectory = Environment.GetEnvironmentVariable("KEITERM_SETTINGS_SCREENSHOT_DIR");
                if (!string.IsNullOrWhiteSpace(screenshotDirectory))
                {
                    Directory.CreateDirectory(screenshotDirectory);
                    HeadlessAvalonia.Pump();
                    using var frame = window.CaptureRenderedFrame();
                    frame?.Save(Path.Combine(screenshotDirectory, $"{width}-{name}.png"), PngBitmapEncoderOptions.Default);

                    // 滚动到底的截图
                    AdaptiveColumnsGrid? grid = window.GetVisualDescendants().OfType<AdaptiveColumnsGrid>().FirstOrDefault(g => g.IsEffectivelyVisible);
                    if (grid != null)
                    {
                        Control settings = grid.Children[0];
                        ScrollViewer? settingsScroll = settings as ScrollViewer ?? settings.FindDescendantOfType<ScrollViewer>();
                        if (settingsScroll != null)
                        {
                            settingsScroll.Offset = new Vector(0, double.MaxValue);
                            window.UpdateLayout();
                            HeadlessAvalonia.Pump();
                            using var bottomFrame = window.CaptureRenderedFrame();
                            bottomFrame?.Save(Path.Combine(screenshotDirectory, $"{width}-{name}-scrolled-bottom.png"), PngBitmapEncoderOptions.Default);
                            settingsScroll.Offset = default;
                            window.UpdateLayout();
                            HeadlessAvalonia.Pump();
                        }
                    }
                    else if (category.Page is TreeSettingsProxyPage or TerminalSettingsCombinedPage or SshSettingsPage or TransferSettingsPage)
                    {
                        foreach (Expander advanced in window.GetVisualDescendants().OfType<Expander>().ToArray()) advanced.IsExpanded = true;
                        ScrollViewer? contentScroll = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Content is ContentControl);
                        if (contentScroll != null)
                        {
                            window.UpdateLayout();
                            contentScroll.Offset = new Vector(0, double.MaxValue);
                            HeadlessAvalonia.Pump();
                            using var bottomFrame = window.CaptureRenderedFrame();
                            bottomFrame?.Save(Path.Combine(screenshotDirectory, $"{width}-{name}-scrolled-bottom.png"), PngBitmapEncoderOptions.Default);
                            contentScroll.Offset = default;
                        }
                    }
                }

                // 页面自身或嵌套属性上的下钻宿主都要进子页（终端外观页的宿主在 Appearance 上，不是页面自身）
                foreach ((ISettingsDrillDownHost host, ICommand command, string commandName) in EnumerateDrillDowns(category.Page))
                {
                    if (!command.CanExecute(null)) continue;
                    command.Execute(null);
                    checkedControls += Verify(window, $"{name}/{commandName}", cardWidths);
                    host.NavigateBack();
                }
            }
            // 防止遍历空转：真实页面里至少有几十个设置行
            Assert.True(checkedControls > 40, $"only {checkedControls} controls checked");
            // 跨页面、跨下钻子页：同一组（单栏 / 双栏左栏）内的卡片宽度一致（误差 1px）
            Assert.True(cardWidths.Count > 10, $"only {cardWidths.Count} cards measured");
            foreach (IGrouping<int, (string Context, int Group, double Width)> group in cardWidths.GroupBy(c => c.Group))
            {
                double min = group.Min(c => c.Width);
                double max = group.Max(c => c.Width);
                Assert.True(max - min <= 1, $"card widths differ across pages in group {group.Key}: {min}..{max} (widest: {group.First(c => c.Width == max).Context}, narrowest: {group.First(c => c.Width == min).Context})");
            }

            // 宽窄来回切换：分隔条只在窄模式存在，行列分配不能残留
            foreach (SettingsCategoryItem category in model.Categories)
            {
                if (category.Page is not (AppearanceSettingsPage or TerminalAppearanceSettingsPage)) continue;
                if (category.Page is TerminalAppearanceSettingsPage terminal && terminal.Appearance.IsInSubPage)
                    terminal.Appearance.NavigateBack();
                if (category.Page is ISettingsDrillDownHost host && host.IsInSubPage) host.NavigateBack();
                model.SelectedCategory = category;
                AssertSplitterTracksWidth(window, 900, expectNarrow: true);
                AssertSplitterTracksWidth(window, 1280, expectNarrow: false);
                AssertSplitterTracksWidth(window, 900, expectNarrow: true);
            }
        }
        finally { window?.Close(); Directory.Delete(directory, recursive: true); }
    });

    private static int Verify(SettingsWindow window, string context, List<(string Context, int Group, double Width)> cardWidths)
    {
        window.UpdateLayout();
        HeadlessAvalonia.Pump();
        int count = 0;
        CollectCardWidths(window, context, cardWidths);

        // 同一宽度下，所有设置页和下钻子页的页头左边缘一致（紧贴导航栏），误差不超过 1px
        SettingsPageHeader? pageHeader = window.GetVisualDescendants().OfType<SettingsPageHeader>().FirstOrDefault(h => h.IsEffectivelyVisible);
        if (pageHeader != null)
        {
            Point? headerOrigin = pageHeader.TranslatePoint(new Point(0, 0), window);
            if (headerOrigin.HasValue)
            {
                // 页头左边缘 = 导航列宽 + 内容区左边距。1280 下导航列 190、边距 26，即 216，表示紧贴导航栏。
                double navWidth = NavColumnWidth(window);
                ContentControl presenter = window.GetVisualDescendants().OfType<ContentControl>()
                    .First(control => control.Name == "PART_SettingsPagePresenter");
                double expectedHeaderLeft = navWidth + presenter.Margin.Left;
                Assert.True(Math.Abs(headerOrigin.Value.X - expectedHeaderLeft) <= 1,
                    $"{context}: page header Left is {headerOrigin.Value.X}, expected nav {navWidth} + margin {presenter.Margin.Left} = {expectedHeaderLeft}");
            }
        }

        // 双栏页：窗口 900 宽时预览落到设置卡片下方（单栏），1280 宽时保持左右双栏
        foreach (AdaptiveColumnsGrid grid in window.GetVisualDescendants().OfType<AdaptiveColumnsGrid>().Where(g => g.IsEffectivelyVisible))
        {
            Control settings = grid.Children[0];
            Control preview = grid.Children[^1];
            if (window.Width < 1000) Assert.True(preview.Bounds.Top >= settings.Bounds.Bottom, $"{context}: preview should sit below settings");
            else Assert.True(preview.Bounds.Left >= settings.Bounds.Right, $"{context}: preview should sit beside settings");

            // 滚动测试：当设置区滚到底时，预览面板仍与窗口可视区有交集
            ScrollViewer? settingsScroll = settings as ScrollViewer ?? settings.FindDescendantOfType<ScrollViewer>();
            if (settingsScroll != null)
            {
                settingsScroll.Offset = new Vector(0, double.MaxValue);
                window.UpdateLayout();
                HeadlessAvalonia.Pump();

                Point? previewPos = preview.TranslatePoint(new Point(0, 0), window);
                Assert.True(previewPos.HasValue, $"{context}: preview has no position in window");
                Rect previewInWindow = new(previewPos.Value, preview.Bounds.Size);
                // 完全落在内容可视区内（底部按钮栏之上），相交不够：窄模式把预览挤出窗口时仍可能与窗口相交
                Rect contentViewport = ContentViewportAboveButtonBar(window);
                Assert.True(previewInWindow.Width > 1 && previewInWindow.Height > 1
                    && previewInWindow.Left >= contentViewport.Left - 1
                    && previewInWindow.Top >= contentViewport.Top - 1
                    && previewInWindow.Right <= contentViewport.Right + 1
                    && previewInWindow.Bottom <= contentViewport.Bottom + 1,
                    $"{context}: preview should sit fully inside the content viewport above the button bar. previewBounds: {previewInWindow}, viewport: {contentViewport}");

                settingsScroll.Offset = new Vector(0, 0);
                window.UpdateLayout();
                HeadlessAvalonia.Pump();
            }
        }
        foreach (Control control in window.GetVisualDescendants().OfType<Control>().Where(c =>
            c is SettingRow or SettingsCard or DrillDownRow && c.IsEffectivelyVisible))
        {
            count++;
            string label = $"{context}/{control.GetType().Name}";
            Assert.True(control.Bounds.Height > 0, $"{label}: height is 0");
            switch (control)
            {
                case SettingRow row:
                    Assert.NotNull(row.Content);
                    Assert.True(row.Title is { Length: > 0 }, $"{label}: empty title");
                    AssertTitleNotSqueezed(row, row.Title, label);
                    break;
                case SettingsCard card:
                    Assert.NotNull(card.Content);
                    break;
                case DrillDownRow drill:
                    Button button = drill.GetVisualDescendants().OfType<Button>().First(b => b.Command == drill.Command && b.Content is string);
                    // 注意：CheckBox 也派生自 Button，不能直接取第一个 Button
                    Assert.True(button.Bounds.Width > 0, $"{label} '{drill.Title}': action button width is 0");
                    Assert.True(button.Bounds.Height > 0, $"{label} '{drill.Title}': action button height is 0");
                    AssertTitleNotSqueezed(drill, drill.Title, label);
                    // 窄宽度下标题列不能被压成竖排长条
                    Assert.True(drill.Bounds.Height < 120, $"{label} '{drill.Title}': row height {drill.Bounds.Height} width {drill.Bounds.Width}");
                    break;
            }
        }

        // 高级折叠区展开断言：
        // 1. 展开后内部每个 SettingRow 的宽度和同一卡片里普通 SettingRow 一致（误差 <= 1px）
        // 2. 内部 SettingRow 不得嵌套带边框的容器（如 Border.settingsCard、默认 ExpanderContent 边框等）
        foreach (Border card in window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("settingsCard") && b.IsEffectivelyVisible))
        {
            List<Expander> expanders = card.GetVisualDescendants().OfType<Expander>().Where(e => e.IsEffectivelyVisible).ToList();
            if (expanders.Count == 0) continue;

            List<SettingRow> normalRows = card.GetVisualDescendants().OfType<SettingRow>()
                .Where(r => r.IsEffectivelyVisible && r.FindAncestorOfType<Expander>() == null).ToList();

            foreach (Expander expander in expanders)
            {
                bool wasExpanded = expander.IsExpanded;
                expander.IsExpanded = true;
                window.UpdateLayout();
                HeadlessAvalonia.Pump();

                List<SettingRow> childRows = expander.GetVisualDescendants().OfType<SettingRow>()
                    .Where(r => r.IsEffectivelyVisible).ToList();

                if (normalRows.Count > 0 && childRows.Count > 0)
                {
                    double expectedWidth = normalRows[0].Bounds.Width;
                    foreach (SettingRow childRow in childRows)
                    {
                        Assert.True(Math.Abs(childRow.Bounds.Width - expectedWidth) <= 1,
                            $"{context}: Expander child SettingRow '{childRow.Title}' width {childRow.Bounds.Width} != normal row width {expectedWidth}");
                    }
                }

                foreach (SettingRow childRow in childRows)
                {
                    Visual? current = childRow.GetVisualParent();
                    while (current != null && current != expander)
                    {
                        if (current is Border b && (b.Classes.Contains("settingsCard") || b.BorderThickness.Top > 0 || b.BorderThickness.Left > 0))
                        {
                            Assert.Fail($"{context}: Expander child SettingRow '{childRow.Title}' is wrapped in a bordered container: {b}");
                        }
                        current = current.GetVisualParent();
                    }
                }

                expander.IsExpanded = wasExpanded;
                window.UpdateLayout();
                HeadlessAvalonia.Pump();
            }
        }
        return count;
    }

    // 切到目标窗口宽后，双栏容器的分隔条与行列必须跟当前模式一致
    private static void AssertSplitterTracksWidth(SettingsWindow window, double width, bool expectNarrow)
    {
        window.Width = width;
        window.UpdateLayout();
        HeadlessAvalonia.Pump();
        AdaptiveColumnsGrid grid = window.GetVisualDescendants().OfType<AdaptiveColumnsGrid>()
            .First(candidate => candidate.IsEffectivelyVisible);
        int splitters = grid.Children.OfType<GridSplitter>().Count();
        Control preview = grid.Children.OfType<Control>().Last(child => child is not GridSplitter);
        Assert.Equal(expectNarrow, grid.IsNarrow);
        if (expectNarrow)
        {
            Assert.Equal(1, splitters);
            GridSplitter splitter = grid.Children.OfType<GridSplitter>().Single();
            Assert.Equal(GridResizeDirection.Rows, splitter.ResizeDirection);
            Assert.True(splitter.Bounds.Height >= 4, $"splitter height {splitter.Bounds.Height}");
            Assert.Equal(3, grid.RowDefinitions.Count);
            Assert.Equal(160, grid.RowDefinitions[2].MinHeight);
            Assert.Equal(480, grid.RowDefinitions[2].MaxHeight);
            Assert.Equal(240, grid.RowDefinitions[2].Height.Value);
            Assert.Equal(GridUnitType.Star, grid.RowDefinitions[0].Height.GridUnitType);
            Assert.Equal(GridUnitType.Auto, grid.RowDefinitions[1].Height.GridUnitType);
            Assert.Equal(0, Grid.GetRow(grid.Children[0]));
            Assert.Equal(1, Grid.GetRow(grid.Children.OfType<GridSplitter>().Single()));
            Assert.Equal(2, Grid.GetRow(preview));
            Assert.Equal(0, Grid.GetColumn(preview));
        }
        else
        {
            Assert.Equal(0, splitters);
            Assert.Empty(grid.GetVisualDescendants().OfType<GridSplitter>());
            Assert.Empty(grid.RowDefinitions);
            Assert.Equal(1, Grid.GetColumn(preview));
            Assert.Equal(0, Grid.GetRow(preview));
            Assert.Equal(0, Grid.GetColumn(grid.Children[0]));
        }
    }

    // 导航列实际宽度（列宿主 Border），取不到时退回窗口里导航控件的宽度
    private static double NavColumnWidth(SettingsWindow window)
    {
        CategoryNavListBox nav = window.GetVisualDescendants().OfType<CategoryNavListBox>().First();
        Border? column = nav.FindAncestorOfType<Border>();
        return column?.Bounds.Width ?? nav.Bounds.Width;
    }

    // 内容可视区：导航列右侧、底部操作栏上沿之上。预览必须整块落在这里，不能只是与窗口相交。
    private static Rect ContentViewportAboveButtonBar(SettingsWindow window)
    {
        double navWidth = NavColumnWidth(window);
        Button confirm = window.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("save"));
        Border bottomBar = confirm.FindAncestorOfType<Border>()
            ?? throw new InvalidOperationException("settings button bar not found");
        Point barOrigin = bottomBar.TranslatePoint(default, window)!.Value;
        return new Rect(navWidth, 0, Math.Max(0, window.ClientSize.Width - navWidth), barOrigin.Y);
    }

    // 收集可见卡片宽度：宽双栏布局的左栏、右栏预览各自成组，其余（单栏）同组
    private static void CollectCardWidths(SettingsWindow window, string context, List<(string Context, int Group, double Width)> cardWidths)
    {
        IEnumerable<Border> cards = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("settingsCard") && b.IsEffectivelyVisible && b.Bounds.Width > 0);
        foreach (Border card in cards)
        {
            AdaptiveColumnsGrid? grid = card.FindAncestorOfType<AdaptiveColumnsGrid>();
            int group = -1;
            if (grid != null && !grid.IsNarrow)
                group = card == grid.Children[^1] || card.GetVisualAncestors().Contains(grid.Children[^1]) ? 1 : 0;
            cardWidths.Add((context, group, card.Bounds.Width));
        }
    }

    // 标题文字的实际宽度不得被控件区挤到下限以下（否则会一两个字一行）
    private const double MinTitleWidth = 120;

    private static void AssertTitleNotSqueezed(Control row, string? title, string label)
    {
        TextBlock titleText = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == title);
        Assert.True(titleText.Bounds.Width >= MinTitleWidth,
            $"{label} '{title}': title width {titleText.Bounds.Width} < {MinTitleWidth}, row width {row.Bounds.Width}");
        // 标题行数上限：常见标题最多折成两行
        Assert.True(titleText.Bounds.Height <= 2 * titleText.FontSize * 1.6,
            $"{label} '{title}': title height {titleText.Bounds.Height}");
    }

    // 页面自身及其持有的下钻宿主上，所有 DrillDown*Command
    private static IEnumerable<(ISettingsDrillDownHost, ICommand, string)> EnumerateDrillDowns(object page)
    {
        List<ISettingsDrillDownHost> hosts = [];
        if (page is ISettingsDrillDownHost self) hosts.Add(self);
        foreach (PropertyInfo property in page.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length == 0 && property.GetValue(page) is ISettingsDrillDownHost nested
                && !hosts.Contains(nested))
                hosts.Add(nested);
        }
        foreach (ISettingsDrillDownHost host in hosts)
        {
            foreach (PropertyInfo property in host.GetType().GetProperties()
                .Where(p => p.Name.StartsWith("DrillDown") && p.Name.EndsWith("Command")))
            {
                if (property.GetValue(host) is ICommand command)
                    yield return (host, command, property.Name);
            }
        }
    }
}
