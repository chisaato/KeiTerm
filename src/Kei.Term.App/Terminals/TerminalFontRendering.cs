namespace Kei.Term.App.Terminals;

using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal;

// 终端字形光栅化参数：所有 TerminalControl（会话标签与设置预览）统一经此配置，保证渲染一致。
public static class TerminalFontRendering
{
    public static void Apply(TerminalControl terminal)
    {
        // RoyalTerminal 先画到未声明像素几何的离屏 SKSurface 再贴回窗口，Skia 在这种表面上
        // 不会做 LCD 子像素抗锯齿，默认的 SubpixelAntialias 实际退化为灰度；这里显式声明灰度，避免误导。
        terminal.FontEdging = TerminalFontEdging.Antialias;

        // 灰度抗锯齿下靠 Full hinting 把笔画吸附到整像素，竖笔不再糊成两列半透明像素
        // （对齐 SecureCRT / FreeType 默认 hinting 的清晰度）
        terminal.FontHinting = TerminalFontHinting.Full;

        // 亚像素定位与线性度量会绕过 hinting 的整像素前进宽度，令同一字形在不同列落在小数位置而发虚
        terminal.FontSubpixelPositioning = false;
        terminal.FontLinearMetrics = false;
        terminal.FontBaselineSnap = true;
    }
}
