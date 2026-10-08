using System;
using Avalonia;
using Avalonia.Media;

namespace Kei.Term.App.DesignSystem;

// 统一可见轮廓而不是 Path 控件的空白外框；几何缩放不改变最终的 2px 描边。
public static class LucideIconGeometry
{
    private static readonly Pen Stroke = new(Brushes.White, 2,
        lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    public static Geometry Normalize(Geometry source, double opticalScale = 1)
    {
        double target = 20 * opticalScale;
        Geometry result = source.Clone();
        double low = 0;
        double high = 4;

        // 用实际描边轮廓求比例，包含圆端点、曲线与折角，避免仅按中心线包围盒估算。
        for (int i = 0; i < 24; i++)
        {
            double scale = (low + high) / 2;
            result.Transform = new MatrixTransform(Matrix.CreateScale(scale, scale));
            Rect outline = result.GetWidenedGeometry(Stroke).Bounds;
            if (Math.Max(outline.Width, outline.Height) < target) low = scale;
            else high = scale;
        }

        double finalScale = (low + high) / 2;
        result.Transform = new MatrixTransform(Matrix.CreateScale(finalScale, finalScale));
        Rect bounds = result.GetWidenedGeometry(Stroke).Bounds;
        result.Transform = new MatrixTransform(new Matrix(finalScale, 0, 0, finalScale,
            12 - bounds.Center.X, 12 - bounds.Center.Y));
        return result;
    }
}
