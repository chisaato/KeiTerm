using Kei.Term.App.Helpers;
using Xunit;

namespace Kei.Term.Tests;

public class DockDragDropHelperTests
{
    [Fact]
    public void CalculateTargetSide_ReturnsExpectedSideBasedOnCenterLine()
    {
        (double PointerX, double TotalWidth, bool ExpectedOnLeft)[] cases =
        [
            (100, 800, true), (399.9, 800, true), (400, 800, false), (400.1, 800, false),
            (700, 800, false), (0, 500, true), (250, 500, false), (-10, 500, true),
            (510, 500, false), (50, 0, false),
        ];
        foreach ((double pointerX, double totalWidth, bool expectedOnLeft) in cases)
            Assert.Equal(expectedOnLeft, DockDragDropHelper.CalculateTargetSide(pointerX, totalWidth));
    }

    [Fact]
    public void HasExceededDragThreshold_DetectsSufficientMovement()
    {
        (double DeltaX, double DeltaY, bool Expected)[] cases =
        [
            (0, 0, false), (5, 5, false), (8, 0, true), (0, 8, true),
            (6, 6, true), (-8, 0, true), (0, -9, true),
        ];
        foreach ((double deltaX, double deltaY, bool expected) in cases)
            Assert.Equal(expected, DockDragDropHelper.HasExceededDragThreshold(deltaX, deltaY));
    }
}
