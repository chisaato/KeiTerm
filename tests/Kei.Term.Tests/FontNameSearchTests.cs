using Kei.Term.App.Services;
using Kei.Term.App.ViewModels.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class FontNameSearchTests
{
    [Fact]
    public void IsMatch_NullOrWhitespacePattern_ReturnsTrue()
    {
        Assert.True(FontNameSearch.IsMatch(null, "Noto Sans CJK SC"));
        Assert.True(FontNameSearch.IsMatch("", "Noto Sans CJK SC"));
        Assert.True(FontNameSearch.IsMatch("   ", "Noto Sans CJK SC"));
    }

    [Fact]
    public void IsMatch_NullOrWhitespaceTarget_WhenPatternProvided_ReturnsFalse()
    {
        Assert.False(FontNameSearch.IsMatch("noto", null));
        Assert.False(FontNameSearch.IsMatch("noto", ""));
        Assert.False(FontNameSearch.IsMatch("noto", "   "));
    }

    [Fact]
    public void IsMatch_MultiToken_IgnoresOrderAndCase()
    {
        Assert.True(FontNameSearch.IsMatch("noto cjk sc", "Noto Sans CJK SC Medium"));
        Assert.True(FontNameSearch.IsMatch("SC NOTO CJK", "Noto Sans CJK SC Medium"));
        Assert.True(FontNameSearch.IsMatch("noto cjk sc", "NOTO SANS CJK SC MEDIUM"));
    }

    [Fact]
    public void IsMatch_AllTokensMustMatch()
    {
        // 所有 token 必须都命中；部分命中不通过
        Assert.True(FontNameSearch.IsMatch("noto cjk", "Noto Sans CJK SC Medium"));
        Assert.False(FontNameSearch.IsMatch("noto cjk tc", "Noto Sans CJK SC Medium"));
        Assert.False(FontNameSearch.IsMatch("noto monospace", "Noto Sans CJK SC Medium"));
    }

    [Fact]
    public void AppearanceSettingsPage_FontItemFilter_MatchesFontFamilyOption()
    {
        var page = new AppearanceSettingsPage();
        var opt = new FontFamilyOption("Noto Sans CJK SC", false);
        Assert.True(page.FontItemFilter("noto cjk", opt));
        Assert.False(page.FontItemFilter("jetbrains", opt));
    }
}
