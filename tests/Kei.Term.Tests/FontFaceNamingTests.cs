using Kei.Term.Core.Fonts;
using Xunit;

using Kei.Term.App.Services;
namespace Kei.Term.Tests;

public class FontFaceNamingTests
{
    [Fact]
    public void ClassifiesVariants()
    {
        (string Family, NerdFontVariant Expected)[] cases =
        [
            ("JetBrainsMono Nerd Font Mono", NerdFontVariant.Mono),
            ("JetBrainsMono NFM Medium", NerdFontVariant.Mono),
            ("JetBrainsMono Nerd Font Propo", NerdFontVariant.Propo),
            ("Example NFP Bold", NerdFontVariant.Propo),
            ("JetBrainsMono NF Medium", NerdFontVariant.Standard),
            ("JetBrainsMono Nerd Font", NerdFontVariant.Standard),
            ("Noto Sans Mono", NerdFontVariant.None),
        ];
        foreach ((string family, NerdFontVariant expected) in cases)
            Assert.Equal(expected, FontFaceNaming.Classify(family));
    }

    [Fact]
    public void ConvertsQtWeights()
    {
        (int Input, int Expected)[] cases =
        [
            (0, 100), (12, 200), (25, 300), (50, 400), (57, 500),
            (63, 600), (75, 700), (81, 800), (87, 900), (500, 500), (800, 800),
        ];
        foreach ((int input, int expected) in cases)
            Assert.Equal(expected, FontFaceNaming.FromQtWeight(input));
    }

    [Fact]
    public void MapsWeightNames()
    {
        (int Weight, string Name)[] cases =
        [
            (100, "Thin"), (200, "ExtraLight"), (300, "Light"),
            (400, "Regular"), (500, "Medium"), (600, "SemiBold"),
            (700, "Bold"), (800, "ExtraBold"), (900, "Black"),
        ];
        foreach ((int weight, string name) in cases)
        {
            Assert.Equal(name, FontFaceNaming.WeightName(weight));
            Assert.Equal(weight, FontFaceNaming.WeightFromName(name));
        }
    }

    [Fact]
    public void RoundsWeights()
    {
        (int Input, int Expected)[] cases = [(449, 400), (450, 500), (-20, 100), (999, 900)];
        foreach ((int input, int expected) in cases)
            Assert.Equal(expected, FontFaceNaming.RoundWeight(input));
    }

    [Fact]
    public void ParsesKonsoleProfiles()
    {
        (string Serialized, int Weight, string? StyleName)[] cases =
        [
            ("JetBrainsMono NF Medium,11,-1,5,57,0,0,0,0,0,Regular", 500, "Regular"),
            ("JetBrainsMono Nerd Font,11,-1,5,500,0,0,0,0,0,0,0,0,0,0,1,Medium", 500, "Medium"),
            ("Example,11,-1,5,75,0,0,0,0,0", 700, null),
        ];
        foreach ((string serialized, int weight, string? styleName) in cases)
        {
            Assert.True(FontFaceNaming.TryParseKonsoleProfile($"[General]\nFont=wrong\n[Appearance]\nFont={serialized}\nColorScheme=Breeze", out KonsoleFont? font, out string error), error);
            Assert.Equal(serialized.Split(',')[0], font!.Family);
            Assert.Equal(11, font.PointSize);
            Assert.Equal(weight, font.Weight);
            Assert.Equal(styleName, font.StyleName);
        }
    }

    [Fact]
    public void RejectsDamagedProfiles()
    {
        string[] cases =
        [
            "[Appearance]\nColorScheme=Breeze",
            "[General]\nFont=Example,11,-1,5,57,0,0,0,0,0",
            "[Appearance]\nFont=Example,bad,-1,5,57,0,0,0,0,0",
            "[Appearance]\nFont=Example,11,-1,5,bad,0,0,0,0,0",
            "[Appearance]\nFont=Example,11,-1,5,57,bad,0,0,0,0",
            "[Appearance]\nFont=Example,11",
        ];
        foreach (string text in cases)
        {
            Assert.False(FontFaceNaming.TryParseKonsoleProfile(text, out KonsoleFont? font, out string error));
            Assert.Null(font);
            Assert.NotEmpty(error);
        }
    }
}

public class FontNameSearchTests
{
    [Fact]
    public void IsMatch_NullOrWhitespace_TreatsEmptyPatternAsMatchAndEmptyTargetAsMiss()
    {
        Assert.True(FontNameSearch.IsMatch(null, "Noto Sans CJK SC"));
        Assert.True(FontNameSearch.IsMatch("", "Noto Sans CJK SC"));
        Assert.True(FontNameSearch.IsMatch("   ", "Noto Sans CJK SC"));
        Assert.False(FontNameSearch.IsMatch("noto", null));
        Assert.False(FontNameSearch.IsMatch("noto", ""));
        Assert.False(FontNameSearch.IsMatch("noto", "   "));
    }

    [Fact]
    public void IsMatch_MultiToken_RequiresEveryToken_IgnoringOrderAndCase()
    {
        Assert.True(FontNameSearch.IsMatch("noto cjk sc", "Noto Sans CJK SC Medium"));
        Assert.True(FontNameSearch.IsMatch("SC NOTO CJK", "Noto Sans CJK SC Medium"));
        Assert.True(FontNameSearch.IsMatch("noto cjk sc", "NOTO SANS CJK SC MEDIUM"));
        // 所有 token 必须都命中；部分命中不通过
        Assert.True(FontNameSearch.IsMatch("noto cjk", "Noto Sans CJK SC Medium"));
        Assert.False(FontNameSearch.IsMatch("noto cjk tc", "Noto Sans CJK SC Medium"));
        Assert.False(FontNameSearch.IsMatch("noto monospace", "Noto Sans CJK SC Medium"));
    }
}
