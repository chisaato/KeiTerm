using System;
using Avalonia.Media;
using Xunit;

namespace Kei.Term.Tests.Contracts;

public class TreeThemeContractTests
{
    [Fact]
    public void ProfileManagerService_AdaptiveColorCalculations_ShouldProduceHighContrast()
    {
        // 亮色 Accent (如 VeritasHare #DAEF00 柠檬亮黄) 自适应为深色前景色 #18181B
        string hareFg = Kei.Term.App.Services.ProfileManagerService.CalculateContrastForeground("#DAEF00");
        Assert.Equal("#18181B", hareFg);

        // 暗色 Accent (如默认 JetBrains 蓝 #3574F0, 深红 #CB1919) 自适应为白色 #FFFFFF
        string blueFg = Kei.Term.App.Services.ProfileManagerService.CalculateContrastForeground("#3574F0");
        Assert.Equal("#FFFFFF", blueFg);

        string redFg = Kei.Term.App.Services.ProfileManagerService.CalculateContrastForeground("#CB1919");
        Assert.Equal("#FFFFFF", redFg);
    }

    // 树引导线是 1px 细线：与面板底接近同色就等于没有线（默认令牌曾借用 Border.Subtle，只有 1.13:1）。
    // 派生规则的目标是“明显但克制”（约 1.9:1），这里把下限锁在 1.5:1，并覆盖浅色面板分支。
    [Theory]
    [InlineData("#1F1F1F", "#3A3A42")]
    [InlineData("#25252A", "#3A3A42")]
    [InlineData("#000000", "#202020")]
    [InlineData("#8A8A8A", "#5A5A5A")]
    [InlineData("#F5F5F5", "#303030")]
    public void TreeGuideLine_StaysVisibleOnPanelBackgrounds(string panel, string border)
    {
        string line = Kei.Term.App.Services.ProfileManagerService.CalculateTreeAndSubtleBorder(panel, border).TreeLineHex;
        double ratio = ContrastRatio(Color.Parse(line), Color.Parse(panel));
        Assert.True(ratio >= 1.5, $"引导线 {line} 在面板 {panel} 上只有 {ratio:F2}:1，会被背景吞掉");
    }

    private static double ContrastRatio(Color foreground, Color background)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                double value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }

        double light = Math.Max(Luminance(foreground), Luminance(background));
        double dark = Math.Min(Luminance(foreground), Luminance(background));
        return (light + 0.05) / (dark + 0.05);
    }
}
