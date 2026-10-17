using Avalonia;
using Kei.Term.App.Helpers;
using Xunit;

namespace Kei.Term.Tests;

public class InitialWindowSizeTests
{
    private static readonly Size Fallback = new(1280, 820);

    [Fact]
    public void Compute_On1440pWorkArea_ScalesToRatio()
    {
        Size size = InitialWindowSize.Compute(new Size(2560, 1400), Fallback);

        Assert.Equal(1792, size.Width);
        Assert.Equal(1050, size.Height);
    }

    [Fact]
    public void Compute_OnSmallWorkArea_KeepsFallbackAsLowerBound()
    {
        // 1600×860 的 70%/75% 小于默认值，取默认值
        Size size = InitialWindowSize.Compute(new Size(1600, 860), Fallback);

        Assert.Equal(1280, size.Width);
        Assert.Equal(791, size.Height);
    }

    [Fact]
    public void Compute_WhenFallbackExceedsWorkArea_CapsToWorkArea()
    {
        Size size = InitialWindowSize.Compute(new Size(1280, 720), Fallback);

        Assert.True(size.Width <= 1280 * InitialWindowSize.MaxRatio);
        Assert.True(size.Height <= 720 * InitialWindowSize.MaxRatio);
    }

    [Fact]
    public void Compute_WithoutWorkArea_ReturnsFallback()
    {
        Assert.Equal(Fallback, InitialWindowSize.Compute(null, Fallback));
    }
}
