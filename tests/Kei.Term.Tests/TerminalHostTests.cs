using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
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

    [Fact]
    public Task Reparent_PreservesTerminalInstance_AndContinuesOutput() => HeadlessAvalonia.RunAsync(() =>
    {
        Window? firstWindow = null;
        Window? secondWindow = null;
        try
        {
            (firstWindow, TerminalTabViewModel tab) = ProbeHost(800, 505);
            TerminalControl terminal = tab.Terminal;
            ScrollViewer first = Assert.IsType<ScrollViewer>(firstWindow.Content);
            AssertRealCellMetrics(terminal);

            const string kept = "KEEP-BEFORE-REPARENT";
            terminal.WriteOutput(Encoding.UTF8.GetBytes(kept + "\r\n"));
            HeadlessAvalonia.Pump();
            Assert.Contains(kept, ReadViewportText(terminal));

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
            Assert.Same(terminal, tab.Terminal);
            AssertRealCellMetrics(terminal);
            Assert.Contains(second, terminal.GetVisualAncestors());
            Assert.DoesNotContain(first, terminal.GetVisualAncestors());

            const string shown = "SHOW-AFTER-REPARENT";
            terminal.WriteOutput(Encoding.UTF8.GetBytes(shown + "\r\n"));
            HeadlessAvalonia.Pump();

            string viewport = ReadViewportText(terminal);
            Assert.Contains(shown, viewport);
            Assert.Contains(kept, ReadBufferText(terminal));
        }
        finally
        {
            secondWindow?.Close();
            firstWindow?.Close();
        }
    });

    [Fact]
    public Task Reparent_ResizesGrid_AndPreservesHistory() => HeadlessAvalonia.RunAsync(() =>
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

    [Fact]
    public Task Reparent_MissingPreferredFont_UsesFirstInstalledFallback() => HeadlessAvalonia.RunAsync(() =>
    {
        // 选择器若忽略这条链、仍返回写死的 JetBrainsMono Nerd Font，或把缺失族名交给终端，本测试失败。
        const string missing = "KeiTerm Missing Preferred Mono";
        string witness = FindInstalledMonospaceWitness(missing);
        string[] chain = [missing, witness, "JetBrainsMono Nerd Font"];

        string selected = SelectProbeMonospace(chain);
        Assert.Equal(witness, selected);

        Window? fallbackWindow = null;
        Window? missingWindow = null;
        try
        {
            (fallbackWindow, TerminalTabViewModel fallbackTab) = ProbeHost(800, 505, chain);
            Assert.Equal(witness, fallbackTab.CurrentFontSnapshot.PrimaryFontFamily);
            Assert.NotEqual(missing, fallbackTab.CurrentFontSnapshot.PrimaryFontFamily);
            AssertRealCellMetrics(fallbackTab.Terminal);

            (missingWindow, TerminalTabViewModel missingTab) = OpenProbe(800, 505, missing);
            SkiaTerminalRenderer missingRenderer = missingTab.Terminal.Renderer!;
            Assert.True(
                missingRenderer.CellWidth < 4 || missingRenderer.CellHeight < 8,
                $"missing font collapsed to a real face: CellWidth={missingRenderer.CellWidth} CellHeight={missingRenderer.CellHeight}");
        }
        finally
        {
            missingWindow?.Close();
            fallbackWindow?.Close();
        }
    });

    // 对照族名只从 FontManager 已安装列表里取，不经过选择器，避免期望值被待测回退逻辑自己算出来。
    private static string FindInstalledMonospaceWitness(string missingFamily)
    {
        List<string> installed = ListSystemFamilyNames();
        Assert.DoesNotContain(
            installed,
            name => string.Equals(name, missingFamily, StringComparison.OrdinalIgnoreCase));

        string? witness = installed.FirstOrDefault(name =>
            !name.Contains("JetBrains", StringComparison.OrdinalIgnoreCase) &&
            LooksLikeMonospaceFamily(name));
        witness ??= installed.FirstOrDefault(name =>
            !name.Contains("JetBrains", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(witness));
        return witness!;
    }

    private static bool LooksLikeMonospaceFamily(string familyName)
    {
        string[] keys = ["mono", "code", "console", "courier", "cascadia", "hack", "liberation", "menlo", "monaco"];
        foreach (string key in keys)
        {
            if (familyName.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ListSystemFamilyNames()
    {
        var fonts = FontManager.Current.SystemFonts;
        Assert.NotNull(fonts);
        var names = new List<string>();
        foreach (FontFamily font in fonts)
        {
            if (string.IsNullOrWhiteSpace(font.Name) || font.Name.Contains(','))
            {
                continue;
            }

            names.Add(font.Name.Trim());
        }

        return names;
    }

    // 探针不用 Host() 的 DejaVu：未安装的族名会把单元格度量成 1px。
    // 这里走跨平台等宽链，缺哪个就跳到 FontManager 能精确解析的下一个，而不是换成另一台机器上碰巧存在的固定名字。
    private static readonly string[] ProbeMonospaceChain =
    [
        "Cascadia Mono",
        "Cascadia Code",
        "Consolas",
        "Courier New",
        "Menlo",
        "SF Mono",
        "Monaco",
        "DejaVu Sans Mono",
        "Liberation Mono",
        "Nimbus Mono PS",
        "Noto Sans Mono",
        "Ubuntu Mono",
        "Hack",
        "Source Code Pro",
        "FreeMono",
        "Courier 10 Pitch",
        "Adwaita Mono",
        "JetBrains Mono",
        "JetBrainsMono Nerd Font",
        "JetBrainsMono Nerd Font Mono",
    ];

    private static (Window Window, TerminalTabViewModel Tab) ProbeHost(double width, double height)
        => ProbeHost(width, height, ProbeMonospaceChain);

    private static (Window Window, TerminalTabViewModel Tab) ProbeHost(
        double width,
        double height,
        IReadOnlyList<string> chain)
    {
        (Window window, TerminalTabViewModel tab) = OpenProbe(width, height, SelectProbeMonospace(chain));
        AssertRealCellMetrics(tab.Terminal);
        return (window, tab);
    }

    private static string SelectProbeMonospace(IReadOnlyList<string> chain)
    {
        foreach (string candidate in chain)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string family = candidate.Trim();
            if (IsExactInstalledFamily(family))
            {
                return family;
            }
        }

        throw new InvalidOperationException("探针等宽字体链里没有 FontManager 能精确解析的已安装字体。");
    }

    // TryGetGlyphTypeface 在找不到族时可能返回别的脸。必须同时出现在 SystemFonts，且字形族名与请求一致。
    private static bool IsExactInstalledFamily(string familyName)
    {
        var fonts = FontManager.Current?.SystemFonts;
        if (fonts is null)
        {
            return false;
        }

        bool listed = false;
        foreach (FontFamily font in fonts)
        {
            if (string.Equals(font.Name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                listed = true;
                break;
            }
        }

        if (!listed)
        {
            return false;
        }

        if (!fonts.TryGetGlyphTypeface(
                familyName,
                FontStyle.Normal,
                FontWeight.Normal,
                FontStretch.Normal,
                out GlyphTypeface? glyphTypeface)
            || glyphTypeface is null)
        {
            return false;
        }

        if (string.Equals(glyphTypeface.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(glyphTypeface.TypographicFamilyName, familyName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string name in glyphTypeface.FamilyNames.Values)
        {
            if (string.Equals(name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
        Assert.True(renderer.CellHeight >= 8, $"CellHeight={renderer.CellHeight}");
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
