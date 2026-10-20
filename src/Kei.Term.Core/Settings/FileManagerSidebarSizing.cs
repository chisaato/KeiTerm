namespace Kei.Term.Core.Settings;

// 保存期望比例而非钳制后的像素值，容器恢复大小后能恢复用户的布局。
public sealed class FileManagerSidebarSizing
{
    public const double DefaultRatio = 0.4;
    public const double SplitterWidth = 4;
    public double Ratio { get; private set; } = DefaultRatio;
    public bool IsManuallyResized { get; private set; }

    public static double ClampDefaultRatio(double ratio)
        => double.IsFinite(ratio) ? Math.Clamp(ratio, 0.2, 0.7) : DefaultRatio;

    public void ApplyDefault(double ratio)
    {
        if (!IsManuallyResized) Ratio = ClampDefaultRatio(ratio);
    }

    public void RecordDrag(double width, double containerWidth)
    {
        if (!double.IsFinite(width) || !double.IsFinite(containerWidth) || containerWidth <= 0) return;
        Ratio = Math.Clamp(width / containerWidth, 0, 1);
        IsManuallyResized = true;
    }

    public double GetWidth(double containerWidth)
    {
        if (!double.IsFinite(containerWidth) || containerWidth <= 0) return 0;
        // 优先给终端保留 240px；不足以同时容纳两者时，侧栏可小于 320px。
        double maximum = Math.Max(0, containerWidth - SplitterWidth - 240);
        return Math.Clamp(containerWidth * Ratio, Math.Min(320, maximum), maximum);
    }
}
