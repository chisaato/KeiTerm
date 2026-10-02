namespace Kei.Term.App.Terminals;

using System;

// 终端 Ctrl+滚轮缩放的纯逻辑：字号步进与滚轮累积都不依赖任何 UI，可在无控件环境下直接测试
public static class TerminalFontZoom
{
    // 与设置页滑条（SettingsWindow.axaml）及保存钳制保持一致的全局字号范围
    public const double MinFontSize = 8.0;
    public const double MaxFontSize = 36.0;

    // 每格滚轮的字号步进量（与 Konsole 一致：pointSizeF ± 1，无倍率）
    public const double PointsPerNotch = 1.0;

    // 按格数步进并钳制到 8–36；current 越界时先归入最近边界，保证结果始终可安全写回全局配置
    public static double Step(double current, int notches)
    {
        double baseline = Math.Clamp(current, MinFontSize, MaxFontSize);
        return Math.Clamp(baseline + notches * PointsPerNotch, MinFontSize, MaxFontSize);
    }
}

// 把滚轮/触控板的小数 delta 累积成整数格：凑满 1 才产生一格，不足一格的余量留到下次
public sealed class WheelNotchAccumulator
{
    private double _remainder;

    // 返回本次应步进的整数格数（不足一格时为 0）；余量按符号保留
    public int Accumulate(double delta)
    {
        _remainder += delta;
        int notches = (int)_remainder;
        _remainder -= notches;
        return notches;
    }

    // 当前未凑满一格的余量（供测试断言小数不被丢弃）
    public double Remainder => _remainder;
}
