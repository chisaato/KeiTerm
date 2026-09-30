using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.VisualTree;
using Xunit;

namespace Kei.Term.Tests.Contracts;

public class TreeThemeContractTests
{
    [Fact]
    public void DesignSystem_TreeTokens_ShouldExistAndBePrefixed()
    {
        var solutionDir = FindSolutionRoot();
        var brushFile = Path.Combine(solutionDir, "src", "Kei.Term.App", "DesignSystem", "KeiTokens.axaml");
        Assert.True(File.Exists(brushFile), "KeiTokens.axaml 应存在");

        var text = File.ReadAllText(brushFile);
        Assert.Contains("Kei.Tree.Line", text);
        Assert.Contains("Kei.Tree.Indent", text);
    }

    [Fact]
    public void KeiControls_ShouldDefineTreeViewThemeWithLines()
    {
        var solutionDir = FindSolutionRoot();
        var compatFile = Path.Combine(solutionDir, "src", "Kei.Term.App", "DesignSystem", "KeiControls.axaml");
        Assert.True(File.Exists(compatFile), "KeiControls.axaml 应存在");

        var text = File.ReadAllText(compatFile);
        Assert.Contains("KeiTreeViewItem", text);
        Assert.Contains("Kei.Tree.Line", text);
        Assert.Contains("PART_ItemsPresenter", text);
    }

    [Fact]
    public void AccentForegroundToken_ShouldExistInTokensAndControls()
    {
        var solutionDir = FindSolutionRoot();
        var brushFile = Path.Combine(solutionDir, "src", "Kei.Term.App", "DesignSystem", "KeiTokens.axaml");
        Assert.True(File.Exists(brushFile), "KeiTokens.axaml 应存在");

        var tokenText = File.ReadAllText(brushFile);
        Assert.Contains(@"x:Key=""Kei.Accent.Foreground""", tokenText);
        Assert.Contains(@"x:Key=""Kei.Color.Accent.Foreground""", tokenText);

        var controlsFile = Path.Combine(solutionDir, "src", "Kei.Term.App", "DesignSystem", "KeiControls.axaml");
        Assert.True(File.Exists(controlsFile), "KeiControls.axaml 应存在");

        var controlsText = File.ReadAllText(controlsFile);
        Assert.Contains("{DynamicResource Kei.Accent.Foreground}", controlsText);
    }

    [Fact]
    public void ProfileManagerService_AdaptiveColorCalculations_ShouldProduceHighContrast()
    {
        // 1. 亮色 Accent (如 VeritasHare #DAEF00 柠檬亮黄) 自适应为深色前景色 #18181B
        var hareFg = Kei.Term.App.Services.ProfileManagerService.CalculateContrastForeground("#DAEF00");
        Assert.Equal("#18181B", hareFg);

        // 2. 暗色 Accent (如默认 JetBrains 蓝 #3574F0, 深红 #CB1919) 自适应为白色 #FFFFFF
        var blueFg = Kei.Term.App.Services.ProfileManagerService.CalculateContrastForeground("#3574F0");
        Assert.Equal("#FFFFFF", blueFg);

        var redFg = Kei.Term.App.Services.ProfileManagerService.CalculateContrastForeground("#CB1919");
        Assert.Equal("#FFFFFF", redFg);

        // 3. 树引导线自适应：在暗色面板上必须有足够的对比度提亮，不融入背景
        var (_, treeLineDark) = Kei.Term.App.Services.ProfileManagerService.CalculateTreeAndSubtleBorder("#25252A", "#3A3A42");
        Assert.NotEqual("#25252A", treeLineDark);
    }

    [Fact]
    public Task TreeLines_LastChildHasNoBottomOverflow() => HeadlessAvalonia.RunAsync(() =>
    {
        // 确保挂载 Kei 控件主题
        Kei.Term.App.Services.UiDesignSystemService.Apply();

        // 验证最后一个节点的竖线停止于节点横线位置，不再向下突出
        var tree = new Avalonia.Controls.TreeView();
        var item1 = new Avalonia.Controls.TreeViewItem { Header = "1" };
        var item2 = new Avalonia.Controls.TreeViewItem { Header = "2" };
        var root = new Avalonia.Controls.TreeViewItem { Header = "root", IsExpanded = true };
        root.Items.Add(item1);
        root.Items.Add(item2);
        tree.Items.Add(root);

        var window = new Avalonia.Controls.Window { Content = tree, Width = 300, Height = 400 };
        window.Show();
        HeadlessAvalonia.Pump();

        var treeLine = root.GetVisualDescendants().OfType<Kei.Term.App.Controls.KeiTreeLine>().FirstOrDefault();
        Assert.NotNull(treeLine);

        double length = treeLine.CalculateLineLength();
        Assert.True(length > 0, "Line length should be positive when children exist");

        // 验证总高度（ItemsPresenter）大于 LineLength（也就是竖线确实被截断，不延伸到末尾子项的底端）
        var itemsPresenter = root.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ItemsPresenter>().FirstOrDefault();
        Assert.NotNull(itemsPresenter);
        Assert.True(itemsPresenter.Bounds.Height > length, $"ItemsPresenter height ({itemsPresenter.Bounds.Height}) should be strictly greater than line length ({length})");

        // 验证高亮容器 PART_ContentPill 存在且位于 Header 内部，确保高亮不覆盖外部层级引导线
        var pill = item1.GetVisualDescendants().OfType<Avalonia.Controls.Border>().FirstOrDefault(b => b.Name == "PART_ContentPill");
        Assert.NotNull(pill);

        window.Close();
    });

    private static string FindSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !Directory.GetFiles(dir, "*.sln*").Any())
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? Environment.CurrentDirectory;
    }
}
