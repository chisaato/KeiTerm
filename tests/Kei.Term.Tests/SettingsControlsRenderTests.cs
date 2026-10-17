using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
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
        }
        finally { window?.Close(); Directory.Delete(directory, recursive: true); }
    });

    private static int Verify(SettingsWindow window, string context, List<(string Context, int Group, double Width)> cardWidths)
    {
        window.UpdateLayout();
        HeadlessAvalonia.Pump();
        int count = 0;
        CollectCardWidths(window, context, cardWidths);
        // 双栏页：窗口 900 宽时预览落到设置卡片下方（单栏），1280 宽时保持左右双栏
        foreach (AdaptiveColumnsGrid grid in window.GetVisualDescendants().OfType<AdaptiveColumnsGrid>().Where(g => g.IsEffectivelyVisible))
        {
            Control settings = grid.Children[0];
            Control preview = grid.Children[1];
            if (window.Width < 1000) Assert.True(preview.Bounds.Top >= settings.Bounds.Bottom, $"{context}: preview should sit below settings");
            else Assert.True(preview.Bounds.Left >= settings.Bounds.Right, $"{context}: preview should sit beside settings");
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
        return count;
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
                group = grid.Children.ToList().FindIndex(child => child == card || card.GetVisualAncestors().Contains(child));
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
