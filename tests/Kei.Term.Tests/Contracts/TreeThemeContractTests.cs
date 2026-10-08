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
}
