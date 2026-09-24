using System.Linq;
using Avalonia.Media;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class SystemFontScannerTests
{
    [Fact]
    public void GetInstalledFonts_ReturnsNonEmptyList()
    {
        var fonts = SystemFontScanner.GetInstalledFonts();
        Assert.NotNull(fonts);
        Assert.NotEmpty(fonts);
    }

    [Theory]
    [InlineData("JetBrains Mono", true)]
    [InlineData("Cascadia Code", true)]
    [InlineData("Fira Code", true)]
    [InlineData("Consolas", true)]
    [InlineData("Windows Terminal", true)]
    [InlineData("Arial", false)]
    [InlineData("Segoe UI", false)]
    [InlineData("Times New Roman", false)]
    [InlineData("SimSun", false)]
    public void IsRecommendedMonospace_RecognizesKeywords(string fontName, bool expected)
    {
        var actual = SystemFontScanner.IsRecommendedMonospace(fontName);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GetInstalledFonts_PlacesMonospaceFontsFirst()
    {
        var fonts = SystemFontScanner.GetInstalledFonts();
        var hasSeenNonMono = false;
        foreach (var font in fonts)
        {
            if (!font.IsMonospaceRecommended)
            {
                hasSeenNonMono = true;
            }
            else if (hasSeenNonMono)
            {
                Assert.Fail($"Found recommended monospace font '{font.Name}' after a non-monospace font.");
            }
        }
    }

    [Fact]
    public void AppearanceSettingsPage_FallbackFontsAndItalic_UpdatesPreviewProperties()
    {
        var page = new AppearanceSettingsPage();

        Assert.NotEmpty(page.AvailableFonts);
        Assert.NotNull(page.SelectedFont);

        // 验证回退字体列表添加与排序
        page.SetTerminalFallbackFonts("Consolas, Courier New");
        Assert.Equal(2, page.TerminalFallbackFonts.Count);
        Assert.Equal("Consolas", page.TerminalFallbackFonts[0]);
        Assert.Equal("Courier New", page.TerminalFallbackFonts[1]);

        page.SelectedTerminalFallbackFont = "Courier New";
        page.MoveUpTerminalFallbackFont();
        Assert.Equal("Courier New", page.TerminalFallbackFonts[0]);
        Assert.Equal("Consolas", page.TerminalFallbackFonts[1]);

        // 验证斜体与预览联动
        page.IsItalic = true;
        Assert.Equal(FontStyle.Italic, page.PreviewFontStyle);

        page.IsItalic = false;
        Assert.Equal(FontStyle.Normal, page.PreviewFontStyle);

        // 验证修改字体时同步更新 SelectedFont 与 PreviewFontFamily
        page.FontFamily = "Consolas";
        Assert.Equal("Consolas", page.SelectedFont.Name);
        Assert.Contains("Consolas", page.PreviewFontFamily.Name);
    }
}
