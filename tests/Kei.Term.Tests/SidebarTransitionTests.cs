using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

// 设置测试会通过全局消息换边/调整宽度；动画等待期间也必须隔离这些共享状态。
[Collection(GuiResourceCollection.Name)]
public class SidebarTransitionTests
{
    [Fact]
    public Task SessionSidebar_HideStopsHitTestingThenReleasesSpace_WithoutTakingFocus() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel(pinned: true);
        MainWindow window = new() { DataContext = model };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            Border sidebar = window.FindControl<Border>("SessionManagerBorder")!;
            Border surface = window.FindControl<Border>("SessionManagerSurface")!;
            Grid split = window.FindControl<Grid>("MainSplitGrid")!;
            GridSplitter splitter = window.FindControl<GridSplitter>("SessionManagerSplitter")!;
            Button outside = window.FindControl<Button>("NewTabButton")!;
            outside.Focus();
            ColumnDefinition sidebarColumn = SessionColumn(split, sidebar);
            ColumnDefinition splitterColumn = SessionColumn(split, splitter);
            double width = sidebarColumn.Width.Value;
            Assert.True(width > 1);

            model.IsSessionManagerVisible = false;
            Assert.True(sidebar.IsVisible);
            Assert.False(sidebar.IsHitTestVisible);
            Assert.Equal(width, sidebarColumn.Width.Value);
            Assert.Same(outside, window.FocusManager!.GetFocusedElement());
            await WaitForAnimationAsync(() => !sidebar.IsVisible);
            Assert.False(sidebar.IsVisible);
            Assert.Equal(0, sidebarColumn.Width.Value);
            Assert.Equal(0, splitterColumn.Width.Value);
            Assert.Equal(0, sidebarColumn.ActualWidth, 1);
            Assert.Equal(0, splitterColumn.ActualWidth, 1);
            Grid content = window.FindControl<Grid>("RightContentGrid")!;
            Assert.Equal(split.Bounds.Width, content.Bounds.Width, 2);

            model.IsSessionManagerVisible = true;
            Assert.True(sidebar.IsVisible);
            Assert.True(sidebar.IsHitTestVisible);
            await WaitForAnimationAsync(() => surface.Opacity == 1
                && Assert.IsType<TranslateTransform>(surface.RenderTransform).X == 0);
            Assert.Equal(width, sidebarColumn.Width.Value);
            Assert.Equal(width, sidebar.Bounds.Width, 2);
            Assert.Equal(4, splitterColumn.Width.Value);
            Assert.Equal(1, surface.Opacity);
            Assert.Equal(0, Assert.IsType<TranslateTransform>(surface.RenderTransform).X);
            Assert.Same(outside, window.FocusManager.GetFocusedElement());
        }
        finally { window.Close(); await model.DisposeAsync(); }
    });

    [Fact]
    public Task FloatingSidebar_ReopeningAndPinningCancelsPendingHide() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel(pinned: false);
        MainWindow window = new() { DataContext = model };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            Border sidebar = window.FindControl<Border>("SessionManagerBorder")!;
            Border surface = window.FindControl<Border>("SessionManagerSurface")!;
            model.ShowFloatingSessionManager();
            HeadlessAvalonia.Pump();
            TextBox filter = window.FindControl<TextBox>("SessionManagerFilter")!;
            Point point = filter.TranslatePoint(new Point(filter.Bounds.Width / 2, filter.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point, RawInputModifiers.None);
            await WaitForAnimationAsync(() => surface.Opacity == 1
                && Assert.IsType<TranslateTransform>(surface.RenderTransform).X == 0);
            Assert.True(model.IsSessionManagerVisible);
            model.DismissFloatingSessionManager();
            Assert.False(sidebar.IsHitTestVisible);
            await AdvanceAnimationAsync(48);
            model.ShowFloatingSessionManager();
            model.ToggleSessionManagerPinCommand.Execute(null);
            await WaitForAnimationAsync(() => surface.Opacity == 1
                && Assert.IsType<TranslateTransform>(surface.RenderTransform).X == 0, minimumMilliseconds: 260);
            Assert.True(sidebar.IsVisible);
            Assert.True(sidebar.IsHitTestVisible);
            Assert.True(model.IsSessionManagerDocked);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    });

    [MonospaceTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FileManagerSidebar_AnimatesBothDirections_AndReclaimsOnlyItsColumn(bool onLeft) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        TerminalTabViewModel tab = CreateTab("files");
        tab.IsFileManagerOnLeft = onLeft;
        TerminalConnectionView view = new() { DataContext = tab };
        Window window = new() { Content = view, Width = 1000, Height = 600 };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            InstalledMonospace.AssertUsableCellHeight(tab.Terminal, tab.Terminal.FontFamilyName);
            Border files = view.FindControl<Border>("SftpHost")!;
            Border surface = view.FindControl<Border>("SftpSurface")!;
            Grid layout = view.FindControl<Grid>("ConnectionLayout")!;
            int column = onLeft ? 0 : 2;
            bool hasIntermediateOpacity = false;
            bool hasOutwardOffset = false;
            surface.PropertyChanged += (_, change) =>
            {
                if (change.Property == Visual.OpacityProperty && surface.Opacity is > 0 and < 1)
                    hasIntermediateOpacity = true;
            };
            TranslateTransform transform = Assert.IsType<TranslateTransform>(surface.RenderTransform);
            transform.PropertyChanged += (_, change) =>
            {
                if (change.Property == TranslateTransform.XProperty && (onLeft ? transform.X < 0 : transform.X > 0))
                    hasOutwardOffset = true;
            };
            tab.IsFileManagerVisible = true;
            Assert.True(files.IsVisible);
            Assert.True(files.IsHitTestVisible);
            await WaitForAnimationAsync(() => hasIntermediateOpacity && hasOutwardOffset
                && surface.Opacity == 1 && transform.X == 0,
                diagnostic: () => $"中间透明度={hasIntermediateOpacity}，向外偏移={hasOutwardOffset}，Opacity={surface.Opacity}，X={transform.X}，onLeft={tab.IsFileManagerOnLeft}");
            Assert.True(hasIntermediateOpacity);
            Assert.True(hasOutwardOffset);
            Assert.Equal(1, surface.Opacity);
            Assert.Equal(0, transform.X);
            // 模拟一次有效拖动；新规则以拖动完成事件保存比例，且侧栏最小为 320px。
            layout.ColumnDefinitions[column].Width = new GridLength(350);
            window.UpdateLayout();
            view.FindControl<GridSplitter>("FileManagerSplitter")!.RaiseEvent(new VectorEventArgs
            {
                RoutedEvent = Thumb.DragCompletedEvent,
                Vector = new Vector(0, 0)
            });
            files.Focus();

            hasIntermediateOpacity = false;
            tab.IsFileManagerVisible = false;
            Assert.True(files.IsVisible);
            Assert.False(files.IsHitTestVisible);
            Assert.False(view.FindControl<GridSplitter>("FileManagerSplitter")!.IsHitTestVisible);
            Assert.Equal(350, layout.ColumnDefinitions[column].Width.Value);
            Assert.Same(tab.Terminal, window.FocusManager!.GetFocusedElement());
            await WaitForAnimationAsync(() => !files.IsVisible);
            Assert.True(hasIntermediateOpacity);
            Assert.False(files.IsVisible);
            Assert.False(view.FindControl<GridSplitter>("FileManagerSplitter")!.IsVisible);
            Assert.Equal(0, layout.ColumnDefinitions[column].Width.Value);
            Assert.True(layout.ColumnDefinitions[onLeft ? 2 : 0].Width.IsStar);

            tab.IsFileManagerVisible = true;
            await WaitForAnimationAsync(() => surface.Opacity == 1 && transform.X == 0);
            Assert.Equal(350, layout.ColumnDefinitions[column].Width.Value);
        }
        finally { window.Close(); await tab.DisposeAsync(); }
    });

    [MonospaceFact]
    public Task FileSidebar_ReversalDoesNotHideAnotherConnection() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        TerminalTabViewModel first = CreateTab("first");
        TerminalTabViewModel second = CreateTab("second");
        first.IsFileManagerVisible = second.IsFileManagerVisible = true;
        TerminalConnectionView left = new() { DataContext = first };
        TerminalConnectionView right = new() { DataContext = second };
        Grid panes = new() { ColumnDefinitions = new ColumnDefinitions("*,*") };
        panes.Children.Add(left);
        panes.Children.Add(right);
        Grid.SetColumn(right, 1);
        Window window = new() { Content = panes, Width = 1500, Height = 600 };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            InstalledMonospace.AssertUsableCellHeight(first.Terminal, first.Terminal.FontFamilyName);
            Border files = left.FindControl<Border>("SftpHost")!;
            Border surface = left.FindControl<Border>("SftpSurface")!;
            Grid layout = left.FindControl<Grid>("ConnectionLayout")!;
            first.IsFileManagerVisible = false;
            await AdvanceAnimationAsync(48);
            first.IsFileManagerVisible = true;
            await WaitForAnimationAsync(() => surface.Opacity == 1
                && Assert.IsType<TranslateTransform>(surface.RenderTransform).X == 0, minimumMilliseconds: 260);
            Assert.True(files.IsVisible);
            Assert.True(files.IsHitTestVisible);
            Assert.True(right.FindControl<Border>("SftpHost")!.IsVisible);
            Assert.True(layout.ColumnDefinitions[2].Width.Value > 0);
        }
        finally { window.Close(); await first.DisposeAsync(); await second.DisposeAsync(); }
    });

    private static ColumnDefinition SessionColumn(Grid split, Control child)
        => split.ColumnDefinitions[Grid.GetColumn(child)];

    private static async Task AdvanceAnimationAsync(int milliseconds)
    {
        // 反向用例需要先播放一段时间，而不是等待动画结束；按真实流逝时间计时。
        Stopwatch watch = Stopwatch.StartNew();
        HeadlessAvalonia.Pump();
        do
        {
            await Task.Delay(16);
            HeadlessAvalonia.Pump();
        }
        while (watch.ElapsedMilliseconds < milliseconds);
    }

    private static async Task WaitForAnimationAsync(Func<bool> completed, int minimumMilliseconds = 0, Func<string>? diagnostic = null)
    {
        // 完成回调和渲染可能跨调度轮次；等待实际画面/布局，且保留严格的最终断言。
        Stopwatch watch = Stopwatch.StartNew();
        do
        {
            HeadlessAvalonia.Pump();
            // 反向用例仍观察完整旧动画窗口，防止过早返回而漏掉迟到的隐藏回调。
            if (watch.ElapsedMilliseconds >= minimumMilliseconds && completed()) return;
            await Task.Delay(16);
        }
        while (watch.ElapsedMilliseconds < 2000);
        HeadlessAvalonia.Pump();
        Assert.True(completed(), $"动画最终状态在 2s 内未达到；{diagnostic?.Invoke()}");
    }

    private static TerminalTabViewModel CreateTab(string title) => new(title,
        new TerminalFontSnapshot(InstalledMonospace.Require(), [], 14, false),
        BuiltInPresets.GetDefaultTerminalProfile(), explicitProfileId: null);

    private static MainViewModel CreateModel(bool pinned)
    {
        SqliteConnectionFactory database = new("Data Source=:memory:");
        InternalVaultManager vault = VaultTestDb.CreateVault(database);
        MainViewModel model = new(new SqliteTreeRepository(database), new SqliteIdentityRepository(database), vault, vault,
            new FixedSettingsService(new AppSettings { SessionManagerPinned = pinned, ConfirmBeforeClose = false }), new SshSessionFactory());
        model.ApplySessionManagerSettings();
        return model;
    }
}
