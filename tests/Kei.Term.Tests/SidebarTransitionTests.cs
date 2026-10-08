using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
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
            Button outside = window.FindControl<Button>("FileManagerToggleButton")!;
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
            await AdvanceAnimationAsync(240);
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
            await AdvanceAnimationAsync(240);
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
            Grid split = window.FindControl<Grid>("MainSplitGrid")!;
            model.ShowFloatingSessionManager();
            HeadlessAvalonia.Pump();
            TextBox filter = window.FindControl<TextBox>("SessionManagerFilter")!;
            Point point = filter.TranslatePoint(new Point(filter.Bounds.Width / 2, filter.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point, RawInputModifiers.None);
            await AdvanceAnimationAsync(240);
            Assert.True(model.IsSessionManagerVisible);
            model.DismissFloatingSessionManager();
            Assert.False(sidebar.IsHitTestVisible);
            await AdvanceAnimationAsync(48);
            model.ShowFloatingSessionManager();
            model.ToggleSessionManagerPinCommand.Execute(null);
            await AdvanceAnimationAsync(260);
            Assert.True(sidebar.IsVisible);
            Assert.True(sidebar.IsHitTestVisible);
            Assert.True(model.IsSessionManagerDocked);
            GridSplitter splitter = window.FindControl<GridSplitter>("SessionManagerSplitter")!;
            ColumnDefinition sidebarColumn = SessionColumn(split, sidebar);
            Assert.True(sidebarColumn.Width.Value > 1);
            Assert.True(sidebar.Bounds.Width > 1);
            ColumnDefinition splitterColumn = SessionColumn(split, splitter);
            Assert.Equal(4, splitterColumn.Width.Value);
            Assert.Equal(4, splitterColumn.ActualWidth, 1);
            Grid content = window.FindControl<Grid>("RightContentGrid")!;
            double railWidth = split.ColumnDefinitions[Grid.GetColumn(window.FindControl<Border>("ModernActivityRail")!)].ActualWidth;
            Assert.Equal(split.Bounds.Width - railWidth - sidebarColumn.ActualWidth - splitterColumn.ActualWidth, content.Bounds.Width, 2);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FileSidebar_AnimatesBothDirections_AndReclaimsOnlyItsColumn(bool onLeft) => HeadlessAvalonia.RunAsync(async () =>
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
            await AdvanceAnimationAsync(240);
            Assert.True(hasIntermediateOpacity);
            Assert.True(hasOutwardOffset);
            Assert.Equal(1, surface.Opacity);
            Assert.Equal(0, transform.X);
            layout.ColumnDefinitions[column].Width = new GridLength(310);
            files.Focus();

            hasIntermediateOpacity = false;
            tab.IsFileManagerVisible = false;
            Assert.True(files.IsVisible);
            Assert.False(files.IsHitTestVisible);
            Assert.False(view.FindControl<GridSplitter>("FileManagerSplitter")!.IsHitTestVisible);
            Assert.Equal(310, layout.ColumnDefinitions[column].Width.Value);
            Assert.Same(tab.Terminal, window.FocusManager!.GetFocusedElement());
            await AdvanceAnimationAsync(240);
            Assert.True(hasIntermediateOpacity);
            Assert.False(files.IsVisible);
            Assert.False(view.FindControl<GridSplitter>("FileManagerSplitter")!.IsVisible);
            Assert.Equal(0, layout.ColumnDefinitions[column].Width.Value);
            Assert.True(layout.ColumnDefinitions[onLeft ? 2 : 0].Width.IsStar);

            tab.IsFileManagerVisible = true;
            await AdvanceAnimationAsync(240);
            Assert.Equal(310, layout.ColumnDefinitions[column].Width.Value);
        }
        finally { window.Close(); await tab.DisposeAsync(); }
    });

    [Fact]
    public Task FileSidebar_ReversalDoesNotHideAnotherConnection_OrRunAfterWindowClose() => HeadlessAvalonia.RunAsync(async () =>
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
            Border files = left.FindControl<Border>("SftpHost")!;
            Grid layout = left.FindControl<Grid>("ConnectionLayout")!;
            first.IsFileManagerVisible = false;
            await AdvanceAnimationAsync(48);
            first.IsFileManagerVisible = true;
            await AdvanceAnimationAsync(260);
            Assert.True(files.IsVisible);
            Assert.True(files.IsHitTestVisible);
            Assert.True(right.FindControl<Border>("SftpHost")!.IsVisible);
            Assert.True(layout.ColumnDefinitions[2].Width.Value > 0);

            first.IsFileManagerVisible = false;
            window.Close();
            double widthAtClose = layout.ColumnDefinitions[2].Width.Value;
            await AdvanceAnimationAsync(260);
            Assert.Equal(widthAtClose, layout.ColumnDefinitions[2].Width.Value);
            Assert.True(files.IsVisible);
        }
        finally { window.Close(); await first.DisposeAsync(); await second.DisposeAsync(); }
    });

    // 推进真实渲染时钟，验证中间画面与完成后的实际布局，不依赖内部动画实现。
    private static ColumnDefinition SessionColumn(Grid split, Control child)
        => split.ColumnDefinitions[Grid.GetColumn(child)];

    private static async Task AdvanceAnimationAsync(int milliseconds)
    {
        HeadlessAvalonia.Pump();
        for (int elapsed = 0; elapsed < milliseconds; elapsed += 16)
        {
            await Task.Delay(16);
            HeadlessAvalonia.Pump();
        }
    }

    private static TerminalTabViewModel CreateTab(string title) => new(title,
        new TerminalFontSnapshot("Menlo, DejaVu Sans Mono, monospace", Array.Empty<string>(), 14, false),
        BuiltInPresets.GetDefaultTerminalProfile(), explicitProfileId: null);

    private static MainViewModel CreateModel(bool pinned)
    {
        SqliteConnectionFactory database = new("Data Source=:memory:");
        InternalVaultManager vault = new(database);
        MainViewModel model = new(new SqliteTreeRepository(database), new SqliteIdentityRepository(database), vault, vault,
            new FixedSettingsService(new AppSettings { SessionManagerPinned = pinned, ConfirmBeforeClose = false }), new SshSessionFactory());
        model.ApplySessionManagerSettings();
        return model;
    }
}
