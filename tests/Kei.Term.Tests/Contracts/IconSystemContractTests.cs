using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Kei.Term.Tests.Contracts;

public class IconSystemContractTests
{
    [Fact]
    public void DesignSystem_KeiIcons_ShouldExistAndDefineCoreIcons()
    {
        var solutionDir = FindSolutionRoot();
        var iconFile = Path.Combine(solutionDir, "src", "Kei.Term.App", "DesignSystem", "KeiIcons.axaml");
        Assert.True(File.Exists(iconFile), "KeiIcons.axaml 应存在于 DesignSystem 目录中");

        var text = File.ReadAllText(iconFile);

        // 验证核心连接与操作图标
        Assert.Contains(@"x:Key=""Kei.Icon.QuickConnect""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.Connect""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.Disconnect""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.Key""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.Settings""", text);

        // 验证会话与文件管理核心图标
        Assert.Contains(@"x:Key=""Kei.Icon.Terminal""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.Folder""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.FolderOpen""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.File""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.FileManager""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.ArrowUp""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.Refresh""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.ExternalLink""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.DockSwap""", text);
        Assert.Contains(@"x:Key=""Kei.Icon.Close""", text);
    }

    [Fact]
    public void KeiControls_ShouldDefineIconStyleRules()
    {
        var solutionDir = FindSolutionRoot();
        var controlsFile = Path.Combine(solutionDir, "src", "Kei.Term.App", "DesignSystem", "KeiControls.axaml");
        Assert.True(File.Exists(controlsFile), "KeiControls.axaml 应存在");

        var text = File.ReadAllText(controlsFile);
        Assert.Contains(@"Selector=""Path.kei-icon""", text);
        Assert.Contains(@"Selector=""Button.iconBtn Path.kei-icon""", text);
        Assert.Contains(@"Selector=""Button.iconBtn:pointerover Path.kei-icon""", text);
    }

    [Fact]
    public void RemoteFileManagerView_ShouldNotContainRawUnicodeSymbolsOrEmojis()
    {
        var solutionDir = FindSolutionRoot();
        var viewFile = Path.Combine(solutionDir, "src", "Kei.Term.App", "Views", "RemoteFileManagerView.axaml");
        Assert.True(File.Exists(viewFile), "RemoteFileManagerView.axaml 应存在");

        var text = File.ReadAllText(viewFile);

        // 验证不再包含曾经临时作为按钮/图标的 Unicode 字符与 Emoji
        Assert.DoesNotContain("📁", text);
        Assert.DoesNotContain("📄", text);
        Assert.DoesNotContain("⇄", text);
        Assert.DoesNotContain("↗", text);
        Assert.DoesNotContain("↻", text);
        Assert.DoesNotContain("✕", text);
        Assert.DoesNotContain("⬆", text);
        Assert.DoesNotContain("▲", text);
        Assert.DoesNotContain("▼", text);
    }

    [Fact]
    public void RemoteFileManagerView_ShouldSatisfyZeroRawHexColors()
    {
        var solutionDir = FindSolutionRoot();
        var viewFile = Path.Combine(solutionDir, "src", "Kei.Term.App", "Views", "RemoteFileManagerView.axaml");
        var text = File.ReadAllText(viewFile);

        // 排除 xml 声明、注释中的 hex，检查 XAML 属性中的硬编码十六进制颜色 (如 #4EC9B0, #2D2D2D)
        var rawHexMatches = Regex.Matches(text, @"(Background|Foreground|BorderBrush)=""#[0-9a-fA-F]{3,8}""");
        Assert.Empty(rawHexMatches);
    }

    [Fact]
    public void MainWindow_ShouldUseGlobalKeiIconsAndEliminateLocalDefinitions()
    {
        var solutionDir = FindSolutionRoot();
        var mainFile = Path.Combine(solutionDir, "src", "Kei.Term.App", "Views", "MainWindow.axaml");
        Assert.True(File.Exists(mainFile), "MainWindow.axaml 应存在");

        var text = File.ReadAllText(mainFile);

        // 验证不再包含旧的局部 Icon.* 资源声明
        Assert.DoesNotContain(@"x:Key=""Icon.QuickConnect""", text);
        Assert.DoesNotContain(@"x:Key=""Icon.Connect""", text);
        Assert.DoesNotContain(@"x:Key=""Icon.Disconnect""", text);
        Assert.DoesNotContain(@"x:Key=""Icon.Key""", text);
        Assert.DoesNotContain(@"x:Key=""Icon.Settings""", text);
        Assert.DoesNotContain(@"x:Key=""Icon.Folder""", text);
        Assert.DoesNotContain(@"x:Key=""Icon.Session""", text);

        // 工具栏图标仍在主窗口；树条目图标随共享模板走，主窗口引用该控件
        Assert.Contains(@"Data=""{StaticResource Kei.Icon.QuickConnect}""", text);
        Assert.Contains(@"Data=""{StaticResource Kei.Icon.Connect}""", text);
        Assert.Contains(@"Data=""{StaticResource Kei.Icon.Disconnect}""", text);
        Assert.Contains("SessionTreeItemView", text);

        var itemView = Path.Combine(solutionDir, "src", "Kei.Term.App", "Views", "Controls", "SessionTreeItemView.axaml");
        var itemText = File.ReadAllText(itemView);
        Assert.Contains(@"Data=""{StaticResource Kei.Icon.Folder}""", itemText);
        Assert.Contains(@"Data=""{StaticResource Kei.Icon.Terminal}""", itemText);
        Assert.DoesNotContain("{StaticResource Icon.", itemText);

        // 验证不再有旧的 {StaticResource Icon. 引用
        Assert.DoesNotContain("{StaticResource Icon.", text);
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
