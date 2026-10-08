namespace Kei.Term.Tests;

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Kei.Term.App.Models;
using Kei.Term.App.Terminals;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views.Controls;
using Kei.Term.Core.Models.Profiles;
using RoyalTerminal.Avalonia.Controls;
using Xunit;

public class TerminalLineHeightTests
{
    private static (Window Window, TerminalTabViewModel Tab) CreateHost(
        string fontFamily,
        double fontSize,
        double windowHeight = 505)
    {
        var tab = new TerminalTabViewModel(
            "test-tab",
            new TerminalFontSnapshot(fontFamily, Array.Empty<string>(), fontSize, false),
            BuiltInPresets.GetDefaultTerminalProfile(),
            explicitProfileId: null,
            scrollbackLines: 5000);

        var window = new Window
        {
            Width = 800,
            Height = windowHeight,
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
        InstalledMonospace.AssertUsableCellHeight(tab.Terminal, fontFamily);
        return (window, tab);
    }

    [Fact]
    public void TryCalculateTargetCellHeight_InvalidInputs_ReturnsNullWithoutGuessing()
    {
        // 0、负数、NaN 等非法字号必须返回 null，不随意猜测 16 或固定比例
        Assert.Null(TerminalFontMetricAdapter.TryCalculateTargetCellHeight("JetBrainsMono Nerd Font", 0));
        Assert.Null(TerminalFontMetricAdapter.TryCalculateTargetCellHeight("JetBrainsMono Nerd Font", -5));
        Assert.Null(TerminalFontMetricAdapter.TryCalculateTargetCellHeight("JetBrainsMono Nerd Font", double.NaN));
    }

    [MonospaceFact]
    public Task TerminalControl_AppliesCalculatedCellHeight_PreservesWidthAndFontSize() => HeadlessAvalonia.RunAsync(() =>
    {
        Window? window = null;
        try
        {
            string family = InstalledMonospace.Require();
            var host = CreateHost(family, 11.0, 505);
            window = host.Window;
            var tab = host.Tab;
            var terminal = tab.Terminal;
            var renderer = terminal.Renderer!;

            // 记下宿主已经应用的行高，再故意打偏，确认 AdaptCellHeight 会改回去而不是空操作。
            float appliedHeight = renderer.CellHeight;
            float naturalWidth = renderer.CellWidth;
            float naturalFontSize = renderer.FontSize;
            Assert.True(appliedHeight > 0);
            renderer.SetCellSize(renderer.CellWidth, appliedHeight + 1f);

            TerminalFontMetricAdapter.AdaptCellHeight(terminal, family, 11.0);
            Assert.Equal(appliedHeight, renderer.CellHeight);

            // 验证 FontSize 与 CellWidth 保持不变
            Assert.Equal(naturalFontSize, renderer.FontSize);
            Assert.Equal(naturalWidth, renderer.CellWidth);
            Assert.True(renderer.CellWidth > 0);

            // 验证 ScrollData.CellHeight 与 Renderer.CellHeight 严格一致
            Assert.NotNull(terminal.ScrollData);
            Assert.Equal(renderer.CellHeight, terminal.ScrollData!.CellHeight);
        }
        finally
        {
            window?.Close();
        }
    });

    [MonospaceFact]
    public Task TerminalControl_FontSizeRoundtrip_MaintainsStability() => HeadlessAvalonia.RunAsync(() =>
    {
        Window? window = null;
        try
        {
            string family = InstalledMonospace.Require();
            var host = CreateHost(family, 11.0, 505);
            window = host.Window;
            var tab = host.Tab;
            var terminal = tab.Terminal;

            float cellHeight11 = terminal.Renderer!.CellHeight;
            float cellWidth11 = terminal.Renderer!.CellWidth;

            // 切换到 14.0
            tab.ApplyFontSnapshot(new TerminalFontSnapshot(family, Array.Empty<string>(), 14.0, false));
            HeadlessAvalonia.Pump();
            float cellHeight14 = terminal.Renderer!.CellHeight;
            Assert.Equal(cellHeight14, terminal.ScrollData!.CellHeight);
            Assert.True(cellHeight14 > cellHeight11);

            // 往返切换回 11.0
            tab.ApplyFontSnapshot(new TerminalFontSnapshot(family, Array.Empty<string>(), 11.0, false));
            HeadlessAvalonia.Pump();
            Assert.Equal(cellHeight11, terminal.Renderer!.CellHeight);
            Assert.Equal(cellHeight11, terminal.ScrollData!.CellHeight);
            Assert.Equal(cellWidth11, terminal.Renderer!.CellWidth);
        }
        finally
        {
            window?.Close();
        }
    });

    [MonospaceFact]
    public Task TerminalControl_ScrollDataHistoryAndMidBottomOffset_MaintainsPositions() => HeadlessAvalonia.RunAsync(() =>
    {
        Window? window = null;
        try
        {
            var host = CreateHost(InstalledMonospace.Require(), 11.0, 505);
            window = host.Window;
            var tab = host.Tab;
            var terminal = tab.Terminal;
            var scrollData = terminal.ScrollData!;

            // 模拟 100 行历史缓冲
            scrollData.Extent = 100 * scrollData.CellHeight;
            // 滚动到中部（例如第 30 行）
            scrollData.Offset = 30 * scrollData.CellHeight;
            Assert.False(scrollData.IsAtBottom);

            // 应用新行高（例如从现有行高变为 24）
            float oldHeight = (float)scrollData.CellHeight;
            float newHeight = oldHeight + 4f;
            TerminalFontMetricAdapter.ApplyCellHeight(terminal, newHeight);

            // 验证相对历史行数保持在 30 行，extent 保持在 100 行，未被重置或误缩放
            Assert.Equal(newHeight, scrollData.CellHeight);
            Assert.Equal(100 * newHeight, scrollData.Extent);
            Assert.Equal(30 * newHeight, scrollData.Offset);

            // 滚动到底部并开启 AutoScroll
            terminal.AutoScroll = true;
            scrollData.ScrollToBottom();
            Assert.True(scrollData.IsAtBottom);

            // 再次切换行高
            float nextHeight = oldHeight;
            TerminalFontMetricAdapter.ApplyCellHeight(terminal, nextHeight);

            // 验证仍然保持在底部
            Assert.True(scrollData.IsAtBottom);
            Assert.Equal(scrollData.MaxOffset, scrollData.Offset);
            Assert.Equal(100 * nextHeight, scrollData.Extent);
        }
        finally
        {
            window?.Close();
        }
    });

    [MonospaceFact]
    public Task TerminalShellPreviewView_MatchesTabViewModel_CellHeightConsistency() => HeadlessAvalonia.RunAsync(() =>
    {
        Window? windowTab = null;
        Window? windowPreview = null;
        try
        {
            string family = InstalledMonospace.Require();
            var host = CreateHost(family, 11.0, 505);
            windowTab = host.Window;
            var tab = host.Tab;

            var preview = new TerminalShellPreviewView
            {
                Font = new TerminalFontSnapshot(family, Array.Empty<string>(), 11.0, false),
                Profile = BuiltInPresets.GetDefaultTerminalProfile()
            };

            windowPreview = new Window
            {
                Width = 800,
                Height = 505,
                Content = preview
            };
            windowPreview.Show();
            HeadlessAvalonia.Pump();

            var previewTerminal = preview.FindControl<Border>("TerminalContainer")?.Child as TerminalControl;
            Assert.NotNull(previewTerminal);
            Assert.NotNull(previewTerminal.Renderer);
            InstalledMonospace.AssertUsableCellHeight(previewTerminal, family);

            // 验证 Preview 内部的 TerminalControl 与 Tab 的 TerminalControl 计算出的行高一致
            Assert.Equal(tab.Terminal.Renderer!.CellHeight, previewTerminal.Renderer!.CellHeight);
            Assert.Equal(tab.Terminal.Renderer!.FontSize, previewTerminal.Renderer!.FontSize);
            Assert.Equal(tab.Terminal.Renderer!.CellWidth, previewTerminal.Renderer!.CellWidth);
        }
        finally
        {
            windowTab?.Close();
            windowPreview?.Close();
        }
    });
}
