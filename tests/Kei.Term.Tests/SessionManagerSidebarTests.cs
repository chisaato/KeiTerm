using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;
using Xunit;

namespace Kei.Term.Tests;

public class SessionManagerSidebarTests
{
    [Theory]
    [InlineData(PanelVisibilityMode.RememberLastState)]
    [InlineData(PanelVisibilityMode.AlwaysHidden)]
    public Task PinnedStartup_DisplaysSidebarEvenWhenLegacyVisibilityWasHidden(PanelVisibilityMode mode) => HeadlessAvalonia.RunAsync(async () =>
    {
        MainViewModel model = CreateModel(new FixedSettingsService(new AppSettings
        {
            SessionManagerPinned = true,
            LastSessionManagerVisible = false,
            SessionManagerVisibilityMode = mode
        }));
        MainWindow window = new() { DataContext = model };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            Assert.True(window.FindControl<Border>("SessionManagerBorder")!.IsEffectivelyVisible);
            model.ToggleSessionManagerCommand.Execute(null);
            Assert.False(model.IsSessionManagerPinned);
            model.DismissFloatingSessionManager();
            model.ToggleSessionManagerCommand.Execute(null);
            HeadlessAvalonia.Pump();
            Assert.True(model.IsSessionManagerDocked);
            Assert.True(window.FindControl<Border>("SessionManagerBorder")!.IsEffectivelyVisible);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    });

    [Fact]
    public Task SidebarButton_TogglesPinning_WhileHoverPreservesTreeWidthAndFocus() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel(new FixedSettingsService());
        MainWindow window = new() { DataContext = model };
        try
        {
            model.NewTabCommand.Execute(null);
            window.Show();
            HeadlessAvalonia.Pump();
            Grid split = window.FindControl<Grid>("MainSplitGrid")!;
            Grid content = window.FindControl<Grid>("RightContentGrid")!;
            Border sidebar = window.FindControl<Border>("SessionManagerBorder")!;
            TreeView tree = window.FindControl<TreeView>("SessionTree")!;
            split.ColumnDefinitions[0].Width = new GridLength(340);
            HeadlessAvalonia.Pump();
            double dockedWidth = content.Bounds.Width;
            Button toggle = window.FindControl<Button>("SessionManagerToggleButton")!;
            Button outside = window.FindControl<Button>("FileManagerToggleButton")!;
            Click(window, toggle);
            Assert.False(model.IsSessionManagerPinned);
            Assert.True(sidebar.IsVisible);
            Assert.Equal(split.Bounds.Width, content.Bounds.Width, 2);
            Move(window, outside);
            await WaitForHoverHideAsync();
            Assert.False(sidebar.IsVisible);
            Assert.Equal(0, split.ColumnDefinitions[1].Width.Value);
            Assert.Equal(split.Bounds.Width, content.Bounds.Width, 2);

            outside.Focus();
            Move(window, toggle);
            Assert.False(model.IsSessionManagerPinned);
            Assert.True(sidebar.IsVisible);
            Assert.Same(outside, window.FocusManager!.GetFocusedElement());
            Assert.Equal(split.Bounds.Width, content.Bounds.Width, 2);
            Assert.Equal(340, sidebar.Bounds.Width, 2);
            Assert.False(window.FindControl<GridSplitter>("SessionManagerSplitter")!.IsVisible);
            Assert.Same(tree, window.FindControl<TreeView>("SessionTree"));
            // 按钮到面板的过渡和内部点击都不会收起浮层。
            Move(window, window.FindControl<TextBox>("SessionManagerFilter")!);
            await WaitForHoverHideAsync();
            Click(window, window.FindControl<TextBox>("SessionManagerFilter")!);
            Assert.True(sidebar.IsVisible);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            HeadlessAvalonia.Pump();
            Assert.False(model.IsSessionManagerVisible);
            Assert.False(sidebar.IsHitTestVisible);
            await WaitForTransitionAsync(240);
            Assert.False(sidebar.IsVisible);
            Move(window, toggle);
            Assert.True(sidebar.IsVisible);
            Assert.Equal(split.Bounds.Width, content.Bounds.Width, 2);
            Click(window, toggle);
            Assert.True(model.IsSessionManagerDocked);
            Assert.Equal(dockedWidth, content.Bounds.Width, 2);
            Move(window, outside);
            await WaitForHoverHideAsync();
            Assert.True(sidebar.IsVisible);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    });

    [Fact]
    public Task OutsideClick_DismissesFloat_AndStillExecutesClickedAction() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel(new FixedSettingsService(new AppSettings { SessionManagerPinned = false }));
        MainWindow window = new() { DataContext = model };
        try
        {
            model.NewTabCommand.Execute(null);
            window.Show();
            HeadlessAvalonia.Pump();
            Move(window, window.FindControl<Button>("SessionManagerToggleButton")!);
            Assert.True(model.IsSessionManagerVisible);
            Button newTab = window.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, model.NewTabCommand));
            Click(window, newTab);
            Assert.False(model.IsSessionManagerVisible);
            Assert.Equal(2, model.WorkspaceTabs.Count);
            // 同一个按钮的点击负责固定状态；悬停只负责临时展开。
            Move(window, window.FindControl<Button>("SessionManagerToggleButton")!);
            Assert.True(model.IsSessionManagerVisible);
            Click(window, window.FindControl<Button>("SessionManagerToggleButton")!);
            Assert.True(model.IsSessionManagerDocked);
            Click(window, window.FindControl<Button>("SessionManagerToggleButton")!);
            Assert.False(model.IsSessionManagerPinned);
            Move(window, window.FindControl<Button>("FileManagerToggleButton")!);
            await WaitForHoverHideAsync();
            Assert.False(model.IsSessionManagerVisible);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    });

    [Fact]
    public Task OpeningTerminal_DismissesOnlyFloatingTree_AndPreservesSelection() => HeadlessAvalonia.RunAsync(async () =>
    {
        MainViewModel model = CreateModel(new FixedSettingsService(new AppSettings { SessionManagerPinned = false }));
        SessionNode session = new() { Name = "LAN", Host = "lan.example" };
        model.SelectedTreeNode = session;
        try
        {
            model.ShowFloatingSessionManager();
            ((IConnectionHost)model).OpenTab(Config(session));
            Assert.False(model.IsSessionManagerVisible);
            Assert.Same(session, model.SelectedTreeNode);
            Assert.IsType<TerminalTabViewModel>(Assert.Single(model.WorkspaceTabs));
            model.IsSessionManagerPinned = true;
            ((IConnectionHost)model).OpenTab(Config(session));
            Assert.True(model.IsSessionManagerVisible);
        }
        finally { await model.DisposeAsync(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task PinChoice_Persists_WhileTemporaryOpeningDoesNotChangeStartupVisibility(bool lastVisible) => HeadlessAvalonia.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_sidebar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        JsonSettingsService service = new(path);
        await service.SaveSettingsAsync(new AppSettings { LastSessionManagerVisible = lastVisible });
        TrackedSettings tracked = new(service);
        MainViewModel model = CreateModel(tracked);
        try
        {
            model.ToggleSessionManagerPinCommand.Execute(null);
            await tracked.LastSave;
            model.DismissFloatingSessionManager();
            model.ShowFloatingSessionManager();
            model.DismissFloatingSessionManager();
            await tracked.LastSave;
            JsonSettingsService reader = new(path);
            await reader.LoadSettingsAsync();
            Assert.False(reader.Current.SessionManagerPinned);
            Assert.Equal(lastVisible, reader.Current.LastSessionManagerVisible);
            MainViewModel restarted = CreateModel(reader);
            try
            {
                Assert.False(restarted.IsSessionManagerPinned);
                Assert.False(restarted.IsSessionManagerVisible);
            }
            finally { await restarted.DisposeAsync(); }
            model.ToggleSessionManagerPinCommand.Execute(null);
            await tracked.LastSave;
            await reader.LoadSettingsAsync();
            Assert.True(reader.Current.SessionManagerPinned);
            Assert.True(reader.Current.LastSessionManagerVisible);
        }
        finally { await model.DisposeAsync(); Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public Task LeftEdgeHover_RevealsWithoutTakingFocus_AndKeepsChildDialogUsable() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel(new FixedSettingsService(new AppSettings { SessionManagerPinned = false }));
        MainWindow window = new() { DataContext = model };
        Window child = new();
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            Border zone = window.FindControl<Border>("SessionManagerRevealZone")!;
            Border sidebar = window.FindControl<Border>("SessionManagerBorder")!;
            Button outside = window.FindControl<Button>("FileManagerToggleButton")!;
            outside.Focus();
            Move(window, zone);
            Assert.True(sidebar.IsVisible);
            Assert.False(model.IsSessionManagerPinned);
            Assert.Same(outside, window.FocusManager!.GetFocusedElement());
            Move(window, window.FindControl<TextBox>("SessionManagerFilter")!);
            await WaitForHoverHideAsync();
            Assert.True(sidebar.IsVisible);

            Task childClosed = child.ShowDialog(window);
            Move(window, outside);
            await WaitForHoverHideAsync();
            Assert.True(sidebar.IsVisible);
            Assert.True(child.IsVisible);
            child.Close();
            await childClosed;
            await WaitForHoverHideAsync();
            Assert.False(sidebar.IsVisible);
            Assert.True(zone.IsVisible);
        }
        finally { child.Close(); window.Close(); await model.DisposeAsync(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SettingsCheckbox_SavesPinChoice_AndAppliesItToWorkspace(bool pinned) => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_sidebar_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        JsonSettingsService service = new(path);
        await service.SaveSettingsAsync(new AppSettings { SessionManagerPinned = !pinned });
        SettingsViewModel settings = new(service, directory);
        SettingsWindow window = new(settings);
        MainViewModel model = CreateModel(service);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            CheckBox check = window.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "SessionManagerPinnedCheckBox");
            check.IsChecked = pinned;
            Assert.True(await settings.ApplyChangesAsync());
            JsonSettingsService reader = new(path);
            await reader.LoadSettingsAsync();
            Assert.Equal(pinned, reader.Current.SessionManagerPinned);
            model.ApplySessionManagerSettings();
            Assert.Equal(pinned, model.IsSessionManagerPinned);
            Assert.Equal(pinned, model.IsSessionManagerDocked);
        }
        finally
        {
            await settings.CancelCommand.ExecuteAsync(null);
            await model.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    });

    private static void Click(Window window, Control control)
    {
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        HeadlessAvalonia.Pump();
    }

    private static void Move(Window window, Control control)
    {
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        HeadlessAvalonia.Pump();
    }

    private static async Task WaitForHoverHideAsync()
    {
        // 等待悬停宽限和退出动画，持续推进真实渲染时钟。
        await WaitForTransitionAsync(460);
    }

    private static async Task WaitForTransitionAsync(int milliseconds)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(16);
            HeadlessAvalonia.Pump();
        }
    }

    private static ResolvedSessionConfig Config(SessionNode session) => new(session.Id, session.Name, session.Host, 22, "ops", null,
        "xterm-256color", null, null, new Dictionary<string, string>());

    private static MainViewModel CreateModel(ISettingsService settings)
    {
        SqliteConnectionFactory database = new("Data Source=:memory:");
        InternalVaultManager vault = new(database);
        MainViewModel model = new(new SqliteTreeRepository(database), new SqliteIdentityRepository(database), vault, vault, settings, new SshSessionFactory());
        model.ApplySessionManagerSettings();
        return model;
    }

    // 等待实际 JSON 写入完成；断言的是新实例读到的配置，不是保存调用次数。
    private sealed class TrackedSettings(JsonSettingsService inner) : ISettingsService
    {
        public AppSettings Current => inner.Current;
        public Task LastSave { get; private set; } = Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => inner.LoadSettingsAsync(ct);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
            => LastSave = inner.SaveSettingsAsync(settings, ct);
    }
}
