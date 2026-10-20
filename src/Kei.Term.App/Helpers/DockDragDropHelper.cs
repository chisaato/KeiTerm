namespace Kei.Term.App.Helpers;

public static class DockDragDropHelper
{
    public const double DragThreshold = 8.0;

    /// <summary>
    /// 根据指针横向位置与视图总宽度，计算放置的目标停靠侧（true 表示停靠在左侧，false 表示右侧）。
    /// </summary>
    public static bool CalculateTargetSide(double pointerX, double totalWidth)
    {
        if (totalWidth <= 0)
        {
            return false;
        }

        // 以中线为界：中线以左放置到左侧，中线以右放置到右侧
        return pointerX < (totalWidth / 2.0);
    }

    /// <summary>
    /// 判断指针移动距离是否超过拖拽防误触阈值。
    /// </summary>
    public static bool HasExceededDragThreshold(double deltaX, double deltaY, double threshold = DragThreshold)
    {
        return (deltaX * deltaX) + (deltaY * deltaY) >= threshold * threshold;
    }
}
