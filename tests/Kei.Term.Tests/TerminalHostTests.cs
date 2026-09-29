using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Kei.Term.App.Models;
using Kei.Term.App.Terminals;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.Tests;

public class TerminalGridAlignmentTests
{
    [Theory]
    [InlineData(505, 22)]
    [InlineData(500, 22)]
    [InlineData(484, 22)]
    [InlineData(733.5, 17.6)]
    [InlineData(1000, 19.3333)]
    [InlineData(30, 22)]
    public void TopInset_KeepsRowCount_AndFillsHeightExactly(double height, double cell)
    {
        int expectedRows = (int)(height / cell);

        double inset = TerminalGridAlignment.TopInset(height, cell);

        // 控件按 floor(内容高度 / 行高) 算行数：设置内边距后行数不能变少，且网格恰好贴住底边
        int rowsAfter = (int)((height - inset) / cell);
        Assert.Equal(expectedRows, rowsAfter);
        Assert.InRange(height - inset - rowsAfter * cell, 0, 0.02);
    }

    [Theory]
    [InlineData(10, 22)]
    [InlineData(0, 22)]
    [InlineData(500, 0)]
    [InlineData(double.NaN, 22)]
    public void TopInset_NoFullRow_OrInvalidMetrics_ReturnsZero(double height, double cell)
    {
        Assert.Equal(0, TerminalGridAlignment.TopInset(height, cell));
    }
}

// 真实 TerminalControl（Headless + Skia）：验证用户能观察到的滚动范围与网格位置
public class TerminalHostTests
{
    private static (Window Window, TerminalTabViewModel Tab) Host(double height, int scrollbackLines = 5000)
    {
        var tab = new TerminalTabViewModel(
            "t",
            new TerminalFontSnapshot("DejaVu Sans Mono", Array.Empty<string>(), 14, false),
            BuiltInPresets.GetDefaultTerminalProfile(),
            explicitProfileId: null,
            scrollbackLines: scrollbackLines);
        var window = new Window
        {
            Width = 800,
            Height = height,
            Content = new ScrollViewer
            {
                Content = tab.Terminal,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                AllowAutoHide = false
            }
        };
        window.Show();
        HeadlessAvalonia.Pump();
        return (window, tab);
    }

    private static void WriteLines(TerminalTabViewModel tab, int count)
    {
        string text = string.Concat(Enumerable.Range(1, count).Select(i => $"line {i}\r\n")) + "prompt$ half-typed";
        tab.Terminal.WriteOutput(Encoding.UTF8.GetBytes(text));
        HeadlessAvalonia.Pump();
    }

    [Fact]
    public Task Grid_IsBottomAligned_SoLastRowTouchesBottomEdge() => HeadlessAvalonia.RunAsync(() =>
    {
        // 505 px 高、行高约 22 px 时余量接近一整行：未贴底时 tmux 状态栏下方会空出一行
        var (window, tab) = Host(505);
        var terminal = tab.Terminal;
        double cell = terminal.Renderer!.CellHeight;
        double height = terminal.Bounds.Height;

        Assert.Equal((int)(height / cell), terminal.Rows);
        Assert.InRange(height - (terminal.Padding.Top + terminal.Rows * cell), 0, 0.05);
        window.Close();
    });

    [Fact]
    public Task ClearScreen_RemovesScrollback_SoNothingLeftToScroll() => HeadlessAvalonia.RunAsync(() =>
    {
        var (window, tab) = Host(505);
        WriteLines(tab, 300);
        var scroll = tab.Terminal.ScrollData!;
        Assert.True(scroll.Extent > scroll.Viewport);

        tab.ClearTerminalScreenCommand.Execute(null);
        HeadlessAvalonia.Pump();

        Assert.Equal(scroll.Viewport, scroll.Extent, 3);
        window.Close();
    });

    [Fact]
    public Task ScrollbackSetting_LimitsRetainedHistory() => HeadlessAvalonia.RunAsync(() =>
    {
        var (window, tab) = Host(505, scrollbackLines: 100);
        WriteLines(tab, 1000);
        var scroll = tab.Terminal.ScrollData!;
        double cell = tab.Terminal.Renderer!.CellHeight;

        // 可滚动的历史行数不超过设置值（默认 5000 时 1000 行会全部保留）
        double historyRows = (scroll.Extent - scroll.Viewport) / cell;
        Assert.InRange(historyRows, 1, 101);
        window.Close();
    });
}
