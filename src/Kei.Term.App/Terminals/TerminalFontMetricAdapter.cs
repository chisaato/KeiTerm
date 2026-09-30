namespace Kei.Term.App.Terminals;

using System;
using Kei.Term.App.Models;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Rendering;
using RoyalTerminal.Avalonia.Scrolling;
using RoyalTerminal.Terminal;
using SkiaSharp;

/// <summary>
/// 终端字体与行高度量适配器：
/// 计算目标行高（在 DPR=1 且 96 DPI 环境下对齐 Qt/Konsole 整数像素字号与 Skia 度量），
/// 并将目标行高同步至 TerminalControl 的 Renderer、ScrollData、布局与贴底状态。
/// </summary>
public static class TerminalFontMetricAdapter
{
    private const double PointToDipScale = 96.0 / 72.0;

    /// <summary>
    /// 尝试计算给定字号与字体的目标单元格行高。
    /// 在 DPR=1 / 96 DPI 环境下，Qt/Konsole 会将 11pt 换算为 11 * 96/72 ≈ 14.6667 并取整为 15px 物理像素字号进行 Skia 度量；
    /// 本函数优先通过已解析的 Typeface/GlyphCache 度量该物理像素下的 ascent/descent/leading。
    /// 若输入无效或度量失败，返回 null，调用方应保留渲染器原本计算的自然行高，不作无依据的猜测或硬编码。
    /// </summary>
    public static float? TryCalculateTargetCellHeight(string? fontFamily, double fontSize, GlyphCache? glyphCache = null)
    {
        if (fontSize <= 0 || !double.IsFinite(fontSize))
        {
            return null;
        }

        float dipSize = (float)(fontSize * PointToDipScale);
        float pixelSize = MathF.Round(dipSize);
        if (pixelSize <= 0 || !float.IsFinite(pixelSize))
        {
            return null;
        }

        try
        {
            // 优先使用 renderer 内部已解析的 GlyphCache，避免对 fallback/style 产生不一致的重复解析
            if (glyphCache is not null)
            {
                using var cacheFont = glyphCache.CreateFont(pixelSize);
                var metrics = cacheFont.Metrics;
                float height = MathF.Round(metrics.Descent - metrics.Ascent + metrics.Leading, MidpointRounding.AwayFromZero);
                if (height > 0 && float.IsFinite(height))
                {
                    return height;
                }
            }

            // 若无 GlyphCache，尝试从规范化主族名解析字体度量
            string primaryFamily = TerminalFontSnapshot.NormalizePrimaryFontFamily(fontFamily);
            using var typeface = SKTypeface.FromFamilyName(primaryFamily);
            if (typeface is not null)
            {
                using var font = new SKFont(typeface, pixelSize)
                {
                    LinearMetrics = true,
                    Subpixel = true
                };
                var metrics = font.Metrics;
                float height = MathF.Round(metrics.Descent - metrics.Ascent + metrics.Leading, MidpointRounding.AwayFromZero);
                if (height > 0 && float.IsFinite(height))
                {
                    return height;
                }
            }
        }
        catch
        {
            // 度量异常时不臆测比例，返回 null 以保留原有自然行高
        }

        return null;
    }

    /// <summary>
    /// 将目标行高安全同步至 TerminalControl 及其 Renderer、ScrollData、贴底与重绘。
    /// </summary>
    public static void ApplyCellHeight(TerminalControl terminal, float targetHeight)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (terminal.Renderer is not { } renderer || targetHeight <= 0 || !float.IsFinite(targetHeight))
        {
            return;
        }

        float currentWidth = renderer.CellWidth;
        float currentHeight = renderer.CellHeight;

        // 若高度已一致且 ScrollData 也已对齐，则无需重复处理
        if (Math.Abs(currentHeight - targetHeight) < 0.001f &&
            terminal.ScrollData != null &&
            Math.Abs(terminal.ScrollData.CellHeight - targetHeight) < 0.001f)
        {
            return;
        }

        // 1. 设置渲染器单元格尺寸（保持字号与 CellWidth 不变，仅更新 CellHeight 并重置基线与字形排版缓存）
        renderer.SetCellSize(currentWidth, targetHeight);

        // 2. 同步 ScrollData：保持原生滚动度量与绝对行数位置
        if (terminal.ScrollData is { } scrollData)
        {
            bool wasAtBottom = scrollData.IsAtBottom;
            double oldCellHeight = scrollData.CellHeight;
            double newCellHeight = targetHeight;

            // 以 scrollData 自身的真实度量计算当前历史行数与视口行数，避免在 renderer 与 scrollData 不同步时误缩放
            double currentOffsetRows = oldCellHeight > 0 ? scrollData.Offset / oldCellHeight : 0;
            double currentExtentRows = oldCellHeight > 0 ? scrollData.Extent / oldCellHeight : 0;

            scrollData.CellHeight = newCellHeight;
            scrollData.Viewport = terminal.Rows * newCellHeight;

            // 保持原生历史 extent，避免用 Screen.TotalRows 替换原生历史
            if (currentExtentRows > 0)
            {
                scrollData.Extent = currentExtentRows * newCellHeight;
            }

            if (wasAtBottom && terminal.AutoScroll)
            {
                scrollData.ScrollToBottom();
            }
            else
            {
                scrollData.Offset = currentOffsetRows * newCellHeight;
            }
        }

        // 3. 触发测量与排列重算，并通知呈现器刷新
        terminal.InvalidateMeasure();
        terminal.InvalidateArrange();
        terminal.InvalidateTerminal();
    }

    /// <summary>
    /// 为给定的 TerminalControl 执行度量计算并应用行高。
    /// 若度量失败，则保留控件渲染器原本的自然行高，不强制修改。
    /// </summary>
    public static void AdaptCellHeight(TerminalControl terminal, string? fontFamily, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (terminal.Renderer is not { } renderer)
        {
            return;
        }

        float? targetHeight = TryCalculateTargetCellHeight(fontFamily, fontSize, renderer.GlyphCache);
        if (targetHeight.HasValue && targetHeight.Value > 0)
        {
            ApplyCellHeight(terminal, targetHeight.Value);
        }
    }
}
