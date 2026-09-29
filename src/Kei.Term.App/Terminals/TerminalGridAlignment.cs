namespace Kei.Term.App.Terminals;

using System;

// 终端网格贴底：控件高度通常不是行高的整数倍，不足一行的余量默认留在底部，
// tmux / byobu / vim 的底部状态栏因此与终端底边之间空出近一整行。把余量移到顶部即可让状态栏贴底。
public static class TerminalGridAlignment
{
    // 控件按 floor(内容高度 / 行高) 计算行数；少留 0.01 像素，防止浮点误差让控件少算一行
    private const double RoundingGuard = 0.01;

    // 给定控件总高度与行高，返回应设置的顶部内边距（行数保持 floor(总高度 / 行高) 不变）
    public static double TopInset(double totalHeight, double cellHeight)
    {
        if (!double.IsFinite(totalHeight) || !double.IsFinite(cellHeight) || totalHeight <= 0 || cellHeight <= 0)
        {
            return 0;
        }

        int rows = (int)(totalHeight / cellHeight);
        if (rows < 1)
        {
            return 0;
        }

        double remainder = totalHeight - rows * cellHeight;
        return Math.Max(0, remainder - RoundingGuard);
    }
}
