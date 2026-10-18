using Kei.Term.App.Services;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class SystemFontScannerTests
{
    [Theory]
    [InlineData("JetBrains Mono", false, false, false)]
    [InlineData("JetBrainsMono Nerd Font Mono", true, false, false)]
    [InlineData("JetBrainsMono Nerd Font", false, true, false)]
    [InlineData("JetBrainsMono Nerd Font Propo", false, false, true)]
    public void FontSymbols_ClassifyTheActualFamilyVariant(string family, bool single, bool doubleCell, bool proportional)
    {
        FontFamilyOption option = new(family, true);
        Assert.Equal(single, option.HasSingleCellIcons);
        Assert.Equal(doubleCell, option.HasDoubleCellIcons);
        Assert.Equal(proportional, option.HasProportionalIcons);
    }

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
        page.SetTerminalFallbackFonts([new() { Family = "Consolas" }, new() { Family = "Courier New" }]);
        Assert.Equal(2, page.TerminalFallbackFonts.Count);
        Assert.Equal("Consolas", page.TerminalFallbackFonts[0].Family);
        Assert.Equal("Courier New", page.TerminalFallbackFonts[1].Family);

        page.TerminalFallbackFonts[1].MoveUpCommand.Execute(null);
        Assert.Equal("Courier New", page.TerminalFallbackFonts[0].Family);
        Assert.Equal("Consolas", page.TerminalFallbackFonts[1].Family);

        // 验证修改字体时同步更新 SelectedFont 与 PreviewFontFamily
        page.FontFamily = "Consolas";
        Assert.Equal("Consolas", page.SelectedFont.Name);
        Assert.Contains("Consolas", page.PreviewFontFamily.Name);
    }
}
