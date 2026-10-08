namespace Kei.Term.Tests;

// 平台条件跳过。用法：[PlatformFact(TestPlatform.MacOS)] 或 [PlatformTheory(TestPlatform.Linux)]。
// 非当前平台时设置 Skip，不要在方法体内 return，否则非目标平台会假绿。
public enum TestPlatform
{
    Windows,
    Linux,
    MacOS,
    FreeBSD,
}

public sealed class PlatformFactAttribute : FactAttribute
{
    public PlatformFactAttribute(TestPlatform platform)
    {
        if (!IsCurrent(platform))
        {
            Skip = SkipReason(platform);
        }
    }

    internal static bool IsCurrent(TestPlatform platform) => platform switch
    {
        TestPlatform.Windows => OperatingSystem.IsWindows(),
        TestPlatform.Linux => OperatingSystem.IsLinux(),
        TestPlatform.MacOS => OperatingSystem.IsMacOS(),
        TestPlatform.FreeBSD => OperatingSystem.IsFreeBSD(),
        _ => false,
    };

    internal static string SkipReason(TestPlatform platform) => $"仅在 {platform} 上运行";
}

public sealed class PlatformTheoryAttribute : TheoryAttribute
{
    public PlatformTheoryAttribute(TestPlatform platform)
    {
        if (!PlatformFactAttribute.IsCurrent(platform))
        {
            Skip = PlatformFactAttribute.SkipReason(platform);
        }
    }
}
