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

    // 迁移契约：设计系统资源已迁至 DesignSystem，旧 Themes 目录不得回潮
    [Fact]
    public void LegacyThemesDirectory_ShouldNotExist()
    {
        var solutionDir = FindSolutionRoot();
        var legacyDir = Path.Combine(solutionDir, "src", "Kei.Term.App", "Themes");
        Assert.False(Directory.Exists(legacyDir), "旧 Themes 目录应已移除（迁移至 DesignSystem）");
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
