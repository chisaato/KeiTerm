using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Kei.Term.App.Models;
using Kei.Term.App.Terminals;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models.Profiles;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Rendering;

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
        string family = InstalledMonospace.Require();
        TerminalTabViewModel tab = new(
            "t",
            new TerminalFontSnapshot(family, Array.Empty<string>(), 14, false),
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
        InstalledMonospace.AssertUsableCellHeight(tab.Terminal, family);
        return (window, tab);
    }

    private static void WriteLines(TerminalTabViewModel tab, int count)
    {
        string text = string.Concat(Enumerable.Range(1, count).Select(i => $"line {i}\r\n")) + "prompt$ half-typed";
        tab.Terminal.WriteOutput(Encoding.UTF8.GetBytes(text));
        HeadlessAvalonia.Pump();
    }

    [MonospaceFact]
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

    [MonospaceFact]
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

    [MonospaceFact]
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

    [MonospaceFact]
    public Task Reparent_PreservesTerminalInstance_ContinuesOutput_AndResizesGrid() => HeadlessAvalonia.RunAsync(() =>
    {
        Window? firstWindow = null;
        Window? secondWindow = null;
        try
        {
            (firstWindow, TerminalTabViewModel tab) = ProbeHost(960, 640);
            TerminalControl terminal = tab.Terminal;
            ScrollViewer first = Assert.IsType<ScrollViewer>(firstWindow.Content);
            (int columnsBefore, int rowsBefore) = ExpectedGrid(terminal);
            Assert.Equal(columnsBefore, terminal.Columns);
            Assert.Equal(rowsBefore, terminal.Rows);
            double widthBefore = terminal.Bounds.Width;
            double heightBefore = terminal.Bounds.Height;
            AssertRealCellMetrics(terminal);

            const string history = "HIST-KEEP-42";
            const string tail = "prompt$ half-typed";
            terminal.WriteOutput(Encoding.UTF8.GetBytes(history + "\r\n"));
            WriteLines(tab, 80);
            string bufferBeforeMove = ReadBufferText(terminal);
            Assert.Contains(history, bufferBeforeMove);
            Assert.Contains(tail, bufferBeforeMove);
            Assert.True(terminal.ScrollData!.CanScroll);
            double historyRows = (terminal.ScrollData.Extent - terminal.ScrollData.Viewport) / terminal.Renderer!.CellHeight;
            Assert.True(historyRows >= 20, $"historyRows={historyRows}");

            ScrollViewer second = CreatePaneScroll();
            secondWindow = new Window
            {
                Width = firstWindow.Width,
                Height = firstWindow.Height,
                Content = second
            };
            secondWindow.Show();
            HeadlessAvalonia.Pump();
            DetachThenAttach(first, second, terminal);

            // 换宿主后仍是同一实例，后续输出继续写入
            Assert.Same(terminal, tab.Terminal);
            AssertRealCellMetrics(terminal);
            const string shown = "SHOW-AFTER-REPARENT";
            terminal.WriteOutput(Encoding.UTF8.GetBytes(shown + "\r\n"));
            HeadlessAvalonia.Pump();
            Assert.Contains(shown, ReadViewportText(terminal));
            Assert.Contains(history, ReadBufferText(terminal));

            secondWindow.Width = 360;
            secondWindow.Height = 220;
            HeadlessAvalonia.Pump();
            tab.TrySyncTerminalSize();

            (int columnsAfter, int rowsAfter) = ExpectedGrid(terminal);
            Assert.True(terminal.Bounds.Width < widthBefore - 100);
            Assert.True(terminal.Bounds.Height < heightBefore - 100);
            Assert.NotEqual(columnsBefore, columnsAfter);
            Assert.NotEqual(rowsBefore, rowsAfter);
            Assert.Equal(columnsAfter, terminal.Columns);
            Assert.Equal(rowsAfter, terminal.Rows);
            Assert.Equal(terminal.Columns, terminal.Screen!.Columns);
            Assert.Equal(terminal.Rows, terminal.Screen.ViewportRows);

            string bufferAfterResize = ReadBufferText(terminal);
            Assert.Contains(history, bufferAfterResize);
            Assert.Contains(tail, bufferAfterResize);
            // 缩窄后 shown 会折行，不在此处整串断言
            Assert.True(terminal.ScrollData.CanScroll);
            Assert.Equal(terminal.ScrollData.Extent, second.Extent.Height, 1);
            Assert.Equal(terminal.ScrollData.Viewport, second.Viewport.Height, 1);

            second.Offset = new Vector(0, 0);
            HeadlessAvalonia.Pump();
            Assert.InRange(terminal.ScrollData.Offset, 0, terminal.Renderer.CellHeight);
            Assert.Contains(history, ReadViewportText(terminal));
            Assert.DoesNotContain(tail, ReadViewportText(terminal));

            second.Offset = new Vector(0, terminal.ScrollData.MaxOffset);
            HeadlessAvalonia.Pump();
            Assert.True(terminal.ScrollData.IsAtBottom);
            Assert.Contains(tail, ReadViewportText(terminal));
            Assert.DoesNotContain(history, ReadViewportText(terminal));
        }
        finally
        {
            secondWindow?.Close();
            firstWindow?.Close();
        }
    });

    private static (Window Window, TerminalTabViewModel Tab) ProbeHost(double width, double height)
    {
        (Window window, TerminalTabViewModel tab) = OpenProbe(width, height, InstalledMonospace.Require());
        AssertRealCellMetrics(tab.Terminal);
        return (window, tab);
    }

    private static (Window Window, TerminalTabViewModel Tab) OpenProbe(double width, double height, string family)
    {
        TerminalTabViewModel tab = new(
            "reparent",
            new TerminalFontSnapshot(family, Array.Empty<string>(), 14, false),
            BuiltInPresets.GetDefaultTerminalProfile(),
            explicitProfileId: null,
            scrollbackLines: 5000);
        ScrollViewer scroll = CreatePaneScroll();
        scroll.Content = tab.Terminal;
        Window window = new()
        {
            Width = width,
            Height = height,
            Content = scroll
        };
        window.Show();
        HeadlessAvalonia.Pump();
        return (window, tab);
    }

    private static void AssertRealCellMetrics(TerminalControl terminal)
    {
        SkiaTerminalRenderer renderer = terminal.Renderer!;
        Assert.True(renderer.CellWidth >= 4, $"CellWidth={renderer.CellWidth}");
        // 比 > 4 更严：换宿主探针要能看出行数变化，1px 回落会把网格排满窗口。
        Assert.True(renderer.CellHeight >= 8, $"CellHeight={renderer.CellHeight}，缺字体回落不能当成真实行高");
    }

    private static ScrollViewer CreatePaneScroll()
    {
        ScrollViewer scroll = new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AllowAutoHide = false
        };
        scroll.Classes.Add("terminalScroll");
        return scroll;
    }

    private static void DetachThenAttach(ScrollViewer from, ScrollViewer to, TerminalControl terminal)
    {
        Assert.Same(terminal, from.Content);
        from.Content = null;
        HeadlessAvalonia.Pump();
        Assert.Null(terminal.Parent);
        Assert.DoesNotContain(from, terminal.GetVisualAncestors());

        to.Content = terminal;
        HeadlessAvalonia.Pump();
        Assert.Same(terminal, to.Content);
        Assert.Contains(to, terminal.GetVisualAncestors());
    }

    private static (int Columns, int Rows) ExpectedGrid(TerminalControl terminal)
    {
        SkiaTerminalRenderer renderer = terminal.Renderer!;
        Assert.True(renderer.CellWidth > 0);
        Assert.True(renderer.CellHeight > 0);

        double width = Math.Max(0, terminal.Bounds.Width - terminal.Padding.Left - terminal.Padding.Right);
        double height = Math.Max(0, terminal.Bounds.Height - terminal.Padding.Top - terminal.Padding.Bottom);
        int columns = Math.Max(1, (int)(width / renderer.CellWidth));
        int rows = Math.Max(1, (int)(height / renderer.CellHeight));
        return (columns, rows);
    }

    private static string ReadViewportText(TerminalControl terminal)
    {
        TerminalScreen screen = terminal.Screen!;
        StringBuilder builder = new();
        lock (screen.SyncRoot)
        {
            for (int row = 0; row < screen.ViewportRows; row++)
            {
                AppendRow(builder, screen.GetViewportRow(row));
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private static string ReadBufferText(TerminalControl terminal)
    {
        TerminalScreen screen = terminal.Screen!;
        StringBuilder builder = new();
        lock (screen.SyncRoot)
        {
            for (int row = 0; row < screen.TotalRows; row++)
            {
                AppendRow(builder, screen.GetRow(row));
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, TerminalRow row)
    {
        foreach (TerminalCell cell in row.ReadOnlyCells)
        {
            if (!string.IsNullOrEmpty(cell.Grapheme))
            {
                builder.Append(cell.Grapheme);
                continue;
            }

            if (cell.Codepoint > 0)
            {
                builder.Append(char.ConvertFromUtf32(cell.Codepoint));
            }
        }
    }
}
