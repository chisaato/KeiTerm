using Kei.Term.App.Services;
using Kei.Term.App.ViewModels.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class SystemFontScannerTests
{
    [Theory]
    [InlineData("JetBrains Mono", true)]
    [InlineData("Consolas", true)]
    [InlineData("Arial", false)]
    [InlineData("SimSun", false)]
    public void IsRecommendedMonospace_RecognizesKeywords(string fontName, bool expected)
    {
        var actual = SystemFontScanner.IsRecommendedMonospace(fontName);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AppearanceSettingsPage_FallbackFonts_UpdatesPreviewProperties()
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

        // 验证修改字体时同步更新 SelectedFont 与 PreviewFontFamily
        page.FontFamily = "Consolas";
        Assert.Equal("Consolas", page.SelectedFont.Name);
        Assert.Contains("Consolas", page.PreviewFontFamily.Name);
    }
}
