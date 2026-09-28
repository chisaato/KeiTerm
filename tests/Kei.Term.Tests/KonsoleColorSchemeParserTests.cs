using System;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class KonsoleColorSchemeParserTests
{
    private const string SampleKonsoleScheme = """
        [General]
        Description=Breeze Test
        Opacity=1
        Wallpaper=

        [Background]
        Color=35,38,39

        [Foreground]
        Color=252,252,252

        [Color0]
        Color=35,38,39
        [Color0Intense]
        Color=127,140,141

        [Color1]
        Color=237,21,21
        [Color1Intense]
        Color=192,57,43

        [Color2]
        Color=17,209,22
        [Color2Intense]
        Color=28,220,154

        [Color3]
        Color=246,116,0
        [Color3Intense]
        Color=253,188,75

        [Color4]
        Color=29,153,243
        [Color4Intense]
        Color=61,174,233

        [Color5]
        Color=155,89,182
        [Color5Intense]
        Color=142,68,173

        [Color6]
        Color=26,188,156
        [Color6Intense]
        Color=22,160,133

        [Color7]
        Color=207,216,220
        [Color7Intense]
        Color=255,255,255
        """;

    [Fact]
    public void Parse_ValidKonsoleScheme_ExtractsColorsCorrectly()
    {
        TerminalProfile profile = KonsoleColorSchemeParser.Parse(SampleKonsoleScheme, "FallbackName");

        Assert.NotNull(profile);
        Assert.Equal("Breeze Test", profile.Name);
        Assert.False(profile.IsBuiltIn);
        Assert.Equal("#232627", profile.Background);
        Assert.Equal("#FCFCFC", profile.Foreground);
        Assert.Equal(16, profile.AnsiColors.Length);

        // Color0 (Black) = 35,38,39 -> #232627
        Assert.Equal("#232627", profile.AnsiColors[0]);
        // Color0Intense (Bright Black) = 127,140,141 -> #7F8C8D
        Assert.Equal("#7F8C8D", profile.AnsiColors[8]);

        // Color1 (Red) = 237,21,21 -> #ED1515
        Assert.Equal("#ED1515", profile.AnsiColors[1]);
        // Color1Intense (Bright Red) = 192,57,43 -> #C0392B
        Assert.Equal("#C0392B", profile.AnsiColors[9]);

        // 光标色与选区高亮色必须存在且为有效 Hex
        Assert.False(string.IsNullOrWhiteSpace(profile.CursorColor));
        Assert.False(string.IsNullOrWhiteSpace(profile.SelectionBackground));
    }

    [Fact]
    public void Parse_MissingDescription_UsesDefaultNameFallback()
    {
        string schemeWithoutDesc = """
            [Background]
            Color=0,0,0
            [Foreground]
            Color=255,255,255
            """;

        TerminalProfile profile = KonsoleColorSchemeParser.Parse(schemeWithoutDesc, "CustomFallback");
        Assert.Equal("CustomFallback", profile.Name);
        Assert.Equal("#000000", profile.Background);
        Assert.Equal("#FFFFFF", profile.Foreground);
    }

    [Fact]
    public void Parse_PartialColors_AppliesSafeFallbacks()
    {
        string partial = """
            [Color0]
            Color = 10, 20, 30
            """;

        TerminalProfile profile = KonsoleColorSchemeParser.Parse(partial, "PartialTest");
        Assert.Equal("#0A141E", profile.AnsiColors[0]);
        // 其余未定义的颜色应用标准 ANSI 兜底，不应为 null 或空白
        for (int i = 0; i < 16; i++)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.AnsiColors[i]));
            Assert.StartsWith("#", profile.AnsiColors[i]);
        }
    }
}
