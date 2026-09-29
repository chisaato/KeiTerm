using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels.Settings;
using Xunit;

namespace Kei.Term.Tests;

public class FontNameSearchTests
{
    private record DummyFont(string DisplayName);

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
    public void IsMatch_MultiToken_OrderIndependent_Matches()
    {
        // 顺序无关
        Assert.True(FontNameSearch.IsMatch("noto cjk sc", "Noto Sans CJK SC Medium"));
        Assert.True(FontNameSearch.IsMatch("sc noto cjk", "Noto Sans CJK SC Medium"));
        Assert.True(FontNameSearch.IsMatch("cjk sc noto", "Noto Sans CJK SC Medium"));
    }

    [Fact]
    public void IsMatch_CaseInsensitive()
    {
        Assert.True(FontNameSearch.IsMatch("NOTO CJK SC", "Noto Sans CJK SC Medium"));
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
    public void IsMatch_VariantAndStyleNames_Matches()
    {
        Assert.True(FontNameSearch.IsMatch("cjk jp light", "Noto Sans CJK JP Light"));
        Assert.True(FontNameSearch.IsMatch("mono bold", "JetBrains Mono Bold"));
    }

    [Fact]
    public void Filter_ConvenienceMethod_FiltersCorrectly()
    {
        var fonts = new List<DummyFont>
        {
            new("Noto Sans CJK SC Medium"),
            new("Noto Sans CJK JP Light"),
            new("JetBrains Mono (Mono)"),
            new("Cascadia Mono (Mono)")
        };

        // 空输入返回全部
        var all = FontNameSearch.Filter(fonts, "", f => f.DisplayName).ToList();
        Assert.Equal(4, all.Count);

        var whitespace = FontNameSearch.Filter(fonts, "   ", f => f.DisplayName).ToList();
        Assert.Equal(4, whitespace.Count);

        var nullPattern = FontNameSearch.Filter(fonts, null, f => f.DisplayName).ToList();
        Assert.Equal(4, nullPattern.Count);

        // 多 token 过滤
        var filtered = FontNameSearch.Filter(fonts, "noto cjk sc", f => f.DisplayName).ToList();
        Assert.Single(filtered);
        Assert.Equal("Noto Sans CJK SC Medium", filtered[0].DisplayName);

        var monoFiltered = FontNameSearch.Filter(fonts, "mono jetbrains", f => f.DisplayName).ToList();
        Assert.Single(monoFiltered);
        Assert.Equal("JetBrains Mono (Mono)", monoFiltered[0].DisplayName);
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
