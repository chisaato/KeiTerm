using System.Collections.Generic;
using Kei.Term.App.ViewModels;
using Xunit;

namespace Kei.Term.Tests;

public class ColorChooserViewModelTests
{
    [Fact]
    public void Constructor_InitializesWithHexAndCorrectHsvRgb()
    {
        var vm = new ColorChooserViewModel("#FF0000");

        Assert.Equal(255, vm.Red);
        Assert.Equal(0, vm.Green);
        Assert.Equal(0, vm.Blue);
        Assert.Equal(255, vm.Alpha);
        Assert.Equal(0, vm.Hue);
        Assert.Equal(255, vm.Sat);
        Assert.Equal(255, vm.Val);
        Assert.Equal("#FF0000", vm.Hex);
    }

    [Fact]
    public void UpdateHsv_ChangesRgbAndHex_AndTriggersLiveCallback()
    {
        var liveUpdates = new List<string>();
        var vm = new ColorChooserViewModel("#000000", hex => liveUpdates.Add(hex));

        // 设置为纯蓝 (H=240, S=255, V=255)
        vm.UpdateHsv(240, 255, 255);

        Assert.Equal(0, vm.Red);
        Assert.Equal(0, vm.Green);
        Assert.Equal(255, vm.Blue);
        Assert.Equal("#0000FF", vm.Hex);
        Assert.NotEmpty(liveUpdates);
        Assert.Equal("#0000FF", liveUpdates[^1]);
    }

    [Fact]
    public void SelectColor_UpdatesAllComponentsAndTriggersLive()
    {
        string? latestLive = null;
        var vm = new ColorChooserViewModel("#FFFFFF", hex => latestLive = hex);

        vm.SelectColor("#232627");

        Assert.Equal("#232627", vm.Hex);
        Assert.Equal(0x23, vm.Red);
        Assert.Equal(0x26, vm.Green);
        Assert.Equal(0x27, vm.Blue);
        Assert.Equal("#232627", latestLive);
    }

    [Fact]
    public void AddToCustomColors_PutsColorInList()
    {
        var vm = new ColorChooserViewModel("#123456");
        vm.AddToCustomColors();

        Assert.Contains("#123456", vm.CustomColors);
    }

    [Fact]
    public void HexWithAlpha_FormatsCorrectlyAndUpdatesAlpha()
    {
        var vm = new ColorChooserViewModel("#80123456");

        Assert.Equal(128, vm.Alpha);
        Assert.Equal(0x12, vm.Red);
        Assert.Equal(0x34, vm.Green);
        Assert.Equal(0x56, vm.Blue);
        Assert.Equal("#80123456", vm.Hex);
    }
}
