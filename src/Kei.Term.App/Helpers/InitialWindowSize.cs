using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Kei.Term.App.Helpers;

/// <summary>
/// 按屏幕工作区比例计算窗口初始尺寸（逻辑像素）。
/// </summary>
public static class InitialWindowSize
{
    // 主窗口比例参照 2560×1440 屏幕上约 1780×1070 的终端窗口
    public const double WidthRatio = 0.70;
    public const double HeightRatio = 0.75;

    // 设置窗口比例：2560×1440 下约 1300×850，默认即可容纳设置 + 预览双栏
    public const double SettingsWidthRatio = 0.51;
    public const double SettingsHeightRatio = 0.61;

    // 窗口占工作区的上限，给任务栏和窗口装饰留余量
    public const double MaxRatio = 0.92;

    /// <param name="workArea">工作区逻辑尺寸；未知时传 null。</param>
    /// <param name="fallback">XAML 声明的默认尺寸，同时作为下限。</param>
    public static Size Compute(Size? workArea, Size fallback,
        double widthRatio = WidthRatio, double heightRatio = HeightRatio)
    {
        if (workArea is not { Width: > 0, Height: > 0 } area)
        {
            return fallback;
        }

        double width = Fit(area.Width * widthRatio, fallback.Width, area.Width * MaxRatio);
        double height = Fit(area.Height * heightRatio, fallback.Height, area.Height * MaxRatio);
        return new Size(Math.Floor(width), Math.Floor(height));
    }

    /// <summary>
    /// 按主屏工作区设置窗口初始尺寸并居中；拿不到屏幕信息（如 headless）时保留 XAML 声明值。
    /// </summary>
    public static void Apply(Window window, double widthRatio, double heightRatio,
        WindowStartupLocation location = WindowStartupLocation.CenterScreen)
    {
        Screen? screen = window.Screens?.Primary;
        Size? workArea = screen is null
            ? null
            : screen.WorkingArea.Size.ToSize(screen.Scaling);
        Size size = Compute(workArea, new Size(window.Width, window.Height), widthRatio, heightRatio);
        window.Width = size.Width;
        window.Height = size.Height;
        window.WindowStartupLocation = location;
    }

    // 先保证不小于默认值，再保证不超出工作区上限（小屏时上限优先）
    private static double Fit(double target, double min, double max) => Math.Min(Math.Max(target, min), max);
}
