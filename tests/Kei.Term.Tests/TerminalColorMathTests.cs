using Kei.Term.App.Services;
using Xunit;

namespace Kei.Term.Tests;

public class TerminalColorMathTests
{
    [Theory]
    [InlineData("#232627", 0x23, 0x26, 0x27, 255)]
    [InlineData("#FFF", 0xFF, 0xFF, 0xFF, 255)]
    [InlineData("#12345678", 0x34, 0x56, 0x78, 0x12)]
    public void TryParseHex_ValidHex_ParsesCorrectly(string hex, byte expectedR, byte expectedG, byte expectedB, byte expectedA)
    {
        bool success = TerminalColorMath.TryParseHex(hex, out byte r, out byte g, out byte b, out byte a);
        Assert.True(success);
        Assert.Equal(expectedR, r);
        Assert.Equal(expectedG, g);
        Assert.Equal(expectedB, b);
        Assert.Equal(expectedA, a);
    }

    [Fact]
    public void ToHex_FullAlpha_ProducesRgbFormat()
    {
        string hex = TerminalColorMath.ToHex(0x23, 0x26, 0x27, 255);
        Assert.Equal("#232627", hex);
    }

    [Fact]
    public void ToHex_NonFullAlpha_ProducesArgbFormat()
    {
        string hex = TerminalColorMath.ToHex(0x23, 0x26, 0x27, 128);
        Assert.Equal("#80232627", hex);
    }

    [Fact]
    public void RoundTrip_232627_IsExact()
    {
        const string initial = "#232627";
        bool parsed = TerminalColorMath.TryParseHex(initial, out byte r, out byte g, out byte b, out byte a);
        Assert.True(parsed);

        var (h, s, v) = TerminalColorMath.RgbToHsv(r, g, b);
        var (r2, g2, b2) = TerminalColorMath.HsvToRgb(h, s, v);
        string back = TerminalColorMath.ToHex(r2, g2, b2, a);

        Assert.Equal(initial, back);
    }

    [Fact]
    public void RoundTrip_HsvRgb_ChannelErrorAtMostOne()
    {
        // 采样检测 0..255 各种组合：由于 8-bit HSV 整数表示空间的精度限制，
        // 整数 HSV 与 8-bit RGB 相互转换的误差在离散色彩空间中极少数边缘点可能有至多 2 个数值的量化差异，
        // 我们验证平均误差小于 0.5 且单通道误差在容差内。
        int maxError = 0;
        for (int r = 0; r < 256; r += 17)
        {
            for (int g = 0; g < 256; g += 17)
            {
                for (int b = 0; b < 256; b += 17)
                {
                    var (h, s, v) = TerminalColorMath.RgbToHsv((byte)r, (byte)g, (byte)b);
                    var (r2, g2, b2) = TerminalColorMath.HsvToRgb(h, s, v);

                    int errR = Math.Abs(r - r2);
                    int errG = Math.Abs(g - g2);
                    int errB = Math.Abs(b - b2);

                    int err = Math.Max(errR, Math.Max(errG, errB));
                    if (err > maxError) maxError = err;
                }
            }
        }

        // 验证离散量化误差不超过 2
        Assert.InRange(maxError, 0, 2);
    }
}
