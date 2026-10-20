using Kei.Term.App.Helpers;
using Xunit;

namespace Kei.Term.Tests;

public class DockDragDropHelperTests
{
    [Theory]
    [InlineData(100, 800, true)]
    [InlineData(399.9, 800, true)]
    [InlineData(400, 800, false)]
    [InlineData(400.1, 800, false)]
    [InlineData(700, 800, false)]
    [InlineData(0, 500, true)]
    [InlineData(250, 500, false)]
    [InlineData(-10, 500, true)]
    [InlineData(510, 500, false)]
    [InlineData(50, 0, false)]
    public void CalculateTargetSide_ReturnsExpectedSideBasedOnCenterLine(double pointerX, double totalWidth, bool expectedOnLeft)
    {
        bool actual = DockDragDropHelper.CalculateTargetSide(pointerX, totalWidth);
        Assert.Equal(expectedOnLeft, actual);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(5, 5, false)]
    [InlineData(8, 0, true)]
    [InlineData(0, 8, true)]
    [InlineData(6, 6, true)]
    [InlineData(-8, 0, true)]
    [InlineData(0, -9, true)]
    public void HasExceededDragThreshold_DetectsSufficientMovement(double deltaX, double deltaY, bool expected)
    {
        bool actual = DockDragDropHelper.HasExceededDragThreshold(deltaX, deltaY);
        Assert.Equal(expected, actual);
    }
}
