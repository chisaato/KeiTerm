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
using SkiaSharp;
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

    [Fact]
    public void TryCalculateTargetCellHeight_MatchesDirectSkiaMeasurement_AndExact20WhenFontInstalled()
    {
        // 验证 TryCalculateTargetCellHeight 的计算值与独立 SKTypeface/SKFont(15px) 严格一致
        const string family = "JetBrainsMono Nerd Font";
        string normalized = TerminalFontSnapshot.NormalizePrimaryFontFamily(family);
        using var typeface = SKTypeface.FromFamilyName(normalized);
        if (typeface is null || typeface.FamilyName != normalized)
        {
            // 环境未安装该字体（CI 无字体），不强求精确 20，避免假阳性
            return;
        }

        using var font15 = new SKFont(typeface, 15f)
        {
            LinearMetrics = true,
            Subpixel = true
        };
        var metrics = font15.Metrics;
        float expected = MathF.Round(metrics.Descent - metrics.Ascent + metrics.Leading, MidpointRounding.AwayFromZero);

        float? calculated = TerminalFontMetricAdapter.TryCalculateTargetCellHeight(family, 11.0);
        Assert.NotNull(calculated);
        Assert.Equal(expected, calculated.Value);

        // 环境实验：当真实安装 JetBrainsMono Nerd Font 时，11pt 在 96DPI(DPR=1) 下目标行高必须为精确 20
        Assert.Equal(20f, calculated.Value);
    }

    [Fact]
    public Task TerminalControl_AppliesCalculatedCellHeight_PreservesWidthAndFontSize() => HeadlessAvalonia.RunAsync(() =>
    {
        Window? window = null;
        try
        {
            var host = CreateHost("JetBrainsMono Nerd Font", 11.0, 505);
            window = host.Window;
            var tab = host.Tab;
            var terminal = tab.Terminal;
            var renderer = terminal.Renderer!;

            // 独立度量 11pt 对应的整数像素字号，验证真实宿主已接入适配。
            using SKFont referenceFont = renderer.GlyphCache.CreateFont(15f);
            SKFontMetrics metrics = referenceFont.Metrics;
            float expectedHeight = MathF.Round(metrics.Descent - metrics.Ascent + metrics.Leading, MidpointRounding.AwayFromZero);
            Assert.Equal(expectedHeight, renderer.CellHeight);

            // 主动制造高度不一致，确保后续调用实际调整行高而非幂等空操作。
            renderer.SetCellSize(renderer.CellWidth, expectedHeight + 1f);
            float naturalWidth = renderer.CellWidth;
            float naturalFontSize = renderer.FontSize;

            // 重新通过 AdaptCellHeight 应用
            TerminalFontMetricAdapter.AdaptCellHeight(terminal, "JetBrainsMono Nerd Font", 11.0);
            Assert.Equal(expectedHeight, renderer.CellHeight);

            // 验证 FontSize 与 CellWidth 保持不变
            Assert.Equal(naturalFontSize, renderer.FontSize);
            Assert.Equal(naturalWidth, renderer.CellWidth);
            Assert.True(renderer.CellWidth > 0);

            // 验证 ScrollData.CellHeight 与 Renderer.CellHeight 严格一致
            Assert.NotNull(terminal.ScrollData);
            Assert.Equal(renderer.CellHeight, terminal.ScrollData!.CellHeight);

            // 验证网格贴底仍然成立
            double height = terminal.Bounds.Height;
            double cell = renderer.CellHeight;
            Assert.Equal((int)(height / cell), terminal.Rows);
            Assert.InRange(height - (terminal.Padding.Top + terminal.Rows * cell), 0, 0.05);
        }
        finally
        {
            window?.Close();
        }
    });

    [Fact]
    public Task TerminalControl_FontSizeRoundtrip_AndParentLayout_MaintainsStability() => HeadlessAvalonia.RunAsync(() =>
    {
        Window? window = null;
        try
        {
            var host = CreateHost("JetBrainsMono Nerd Font", 11.0, 505);
            window = host.Window;
            var tab = host.Tab;
            var terminal = tab.Terminal;

            float cellHeight11 = terminal.Renderer!.CellHeight;
            float cellWidth11 = terminal.Renderer!.CellWidth;

            // 切换到 14.0
            tab.ApplyFontSnapshot(new TerminalFontSnapshot("JetBrainsMono Nerd Font", Array.Empty<string>(), 14.0, false));
            HeadlessAvalonia.Pump();
            float cellHeight14 = terminal.Renderer!.CellHeight;
            Assert.Equal(cellHeight14, terminal.ScrollData!.CellHeight);
            Assert.True(cellHeight14 > cellHeight11);

            // 往返切换回 11.0
            tab.ApplyFontSnapshot(new TerminalFontSnapshot("JetBrainsMono Nerd Font", Array.Empty<string>(), 11.0, false));
            HeadlessAvalonia.Pump();
            Assert.Equal(cellHeight11, terminal.Renderer!.CellHeight);
            Assert.Equal(cellHeight11, terminal.ScrollData!.CellHeight);
            Assert.Equal(cellWidth11, terminal.Renderer!.CellWidth);

            // 父级容器尺寸变动时行高不累积不被重置
            window.Height = 600;
            HeadlessAvalonia.Pump();
            window.Height = 520;
            HeadlessAvalonia.Pump();
            Assert.Equal(cellHeight11, terminal.Renderer!.CellHeight);
            Assert.Equal(cellHeight11, terminal.ScrollData!.CellHeight);
        }
        finally
        {
            window?.Close();
        }
    });

    [Fact]
    public Task TerminalControl_ScrollDataHistoryAndMidBottomOffset_MaintainsPositions() => HeadlessAvalonia.RunAsync(() =>
    {
        Window? window = null;
        try
        {
            var host = CreateHost("JetBrainsMono Nerd Font", 11.0, 505);
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

    [Fact]
    public Task TerminalShellPreviewView_MatchesTabViewModel_CellHeightConsistency() => HeadlessAvalonia.RunAsync(() =>
    {
        Window? windowTab = null;
        Window? windowPreview = null;
        try
        {
            var host = CreateHost("JetBrainsMono Nerd Font", 11.0, 505);
            windowTab = host.Window;
            var tab = host.Tab;

            var preview = new TerminalShellPreviewView
            {
                Font = new TerminalFontSnapshot("JetBrainsMono Nerd Font", Array.Empty<string>(), 11.0, false),
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
