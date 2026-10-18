using Kei.Term.Core.Fonts;
using Xunit;

namespace Kei.Term.Tests;

public class FontFaceNamingTests
{
    [Theory]
    [InlineData("JetBrainsMono Nerd Font Mono", NerdFontVariant.Mono)]
    [InlineData("JetBrainsMono NFM Medium", NerdFontVariant.Mono)]
    [InlineData("JetBrainsMono Nerd Font Propo", NerdFontVariant.Propo)]
    [InlineData("Example NFP Bold", NerdFontVariant.Propo)]
    [InlineData("JetBrainsMono NF Medium", NerdFontVariant.Standard)]
    [InlineData("JetBrainsMono Nerd Font", NerdFontVariant.Standard)]
    [InlineData("Noto Sans Mono", NerdFontVariant.None)]
    public void ClassifiesVariants(string family, NerdFontVariant expected) => Assert.Equal(expected, FontFaceNaming.Classify(family));

    [Theory]
    [InlineData(0, 100)]
    [InlineData(12, 200)]
    [InlineData(25, 300)]
    [InlineData(50, 400)]
    [InlineData(57, 500)]
    [InlineData(63, 600)]
    [InlineData(75, 700)]
    [InlineData(81, 800)]
    [InlineData(87, 900)]
    [InlineData(500, 500)]
    [InlineData(800, 800)]
    public void ConvertsQtWeights(int input, int expected) => Assert.Equal(expected, FontFaceNaming.FromQtWeight(input));

    [Theory]
    [InlineData(100, "Thin")]
    [InlineData(200, "ExtraLight")]
    [InlineData(300, "Light")]
    [InlineData(400, "Regular")]
    [InlineData(500, "Medium")]
    [InlineData(600, "SemiBold")]
    [InlineData(700, "Bold")]
    [InlineData(800, "ExtraBold")]
    [InlineData(900, "Black")]
    public void MapsWeightNames(int weight, string name)
    {
        Assert.Equal(name, FontFaceNaming.WeightName(weight));
        Assert.Equal(weight, FontFaceNaming.WeightFromName(name));
    }

    [Theory]
    [InlineData(449, 400)]
    [InlineData(450, 500)]
    [InlineData(-20, 100)]
    [InlineData(999, 900)]
    public void RoundsWeights(int input, int expected) => Assert.Equal(expected, FontFaceNaming.RoundWeight(input));

    [Theory]
    [InlineData("JetBrainsMono NF Medium,11,-1,5,57,0,0,0,0,0,Regular", 500, "Regular")]
    [InlineData("JetBrainsMono Nerd Font,11,-1,5,500,0,0,0,0,0,0,0,0,0,0,1,Medium", 500, "Medium")]
    [InlineData("Example,11,-1,5,75,0,0,0,0,0", 700, null)]
    public void ParsesKonsoleProfiles(string serialized, int weight, string? styleName)
    {
        Assert.True(FontFaceNaming.TryParseKonsoleProfile($"[General]\nFont=wrong\n[Appearance]\nFont={serialized}\nColorScheme=Breeze", out KonsoleFont? font, out string error), error);
        Assert.Equal(serialized.Split(',')[0], font!.Family);
        Assert.Equal(11, font.PointSize);
        Assert.Equal(weight, font.Weight);
        Assert.Equal(styleName, font.StyleName);
    }

    [Theory]
    [InlineData("[Appearance]\nColorScheme=Breeze")]
    [InlineData("[General]\nFont=Example,11,-1,5,57,0,0,0,0,0")]
    [InlineData("[Appearance]\nFont=Example,bad,-1,5,57,0,0,0,0,0")]
    [InlineData("[Appearance]\nFont=Example,11,-1,5,bad,0,0,0,0,0")]
    [InlineData("[Appearance]\nFont=Example,11,-1,5,57,bad,0,0,0,0")]
    [InlineData("[Appearance]\nFont=Example,11")]
    public void RejectsDamagedProfiles(string text)
    {
        Assert.False(FontFaceNaming.TryParseKonsoleProfile(text, out KonsoleFont? font, out string error));
        Assert.Null(font);
        Assert.NotEmpty(error);
    }
}
