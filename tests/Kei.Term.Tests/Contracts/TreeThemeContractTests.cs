using System;
using System.IO;
using System.Linq;
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
