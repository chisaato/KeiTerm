using System;
using Avalonia;

namespace Kei.Term.App.Helpers;

/// <summary>
/// 按屏幕工作区比例计算窗口初始尺寸（逻辑像素）。
/// </summary>
public static class InitialWindowSize
{
    // 比例参照 2560×1440 屏幕上约 1780×1070 的终端窗口
    public const double WidthRatio = 0.70;
    public const double HeightRatio = 0.75;

    // 窗口占工作区的上限，给任务栏和窗口装饰留余量
    public const double MaxRatio = 0.92;

    /// <param name="workArea">工作区逻辑尺寸；未知时传 null。</param>
    /// <param name="fallback">XAML 声明的默认尺寸，同时作为下限。</param>
    public static Size Compute(Size? workArea, Size fallback)
    {
        if (workArea is not { Width: > 0, Height: > 0 } area)
        {
            return fallback;
        }

        double width = Fit(area.Width * WidthRatio, fallback.Width, area.Width * MaxRatio);
        double height = Fit(area.Height * HeightRatio, fallback.Height, area.Height * MaxRatio);
        return new Size(Math.Floor(width), Math.Floor(height));
    }

    // 先保证不小于默认值，再保证不超出工作区上限（小屏时上限优先）
    private static double Fit(double target, double min, double max) => Math.Min(Math.Max(target, min), max);
}
