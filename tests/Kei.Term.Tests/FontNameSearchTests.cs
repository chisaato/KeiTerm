using Kei.Term.App.Services;
using Xunit;

namespace Kei.Term.Tests;

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
