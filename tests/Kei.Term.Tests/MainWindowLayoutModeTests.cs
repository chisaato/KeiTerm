using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;
using RoyalTerminal.Avalonia.Controls;
using Xunit;

namespace Kei.Term.Tests;

// 验证主窗口 Classic 与 Modern 模式启动锁定、菜单与 Rail 显隐、单宿主及生命周期契约
public sealed class MainWindowLayoutModeTests
{
    [Fact]
    public Task Startup_ProductionSequence_ModernOnDisk_FinalizesToModernLayout() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string tempDir = Path.Combine(Path.GetTempPath(), "KeiTermTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            // 真实 disk 写入 Modern 模式配置
            await File.WriteAllTextAsync(settingsPath, """
            {
                "LayoutMode": "Modern"
            }
            """);

            // 生产启动时序模拟：JsonSettingsService 在 new 时并未 LoadSettingsAsync，Current 为默认 Classic
            JsonSettingsService settingsService = new(settingsPath);
            Assert.Equal(LayoutMode.Classic, settingsService.Current.LayoutMode);

            MainViewModel mainVm = CreateModelWithSettingsService(settingsService);

            // 生产 App.axaml.cs 步骤：new MainWindow(deferStartupLayout: true) { DataContext = mainVm }
            MainWindow mainWindow = new(deferStartupLayout: true)
            {
                DataContext = mainVm,
            };

            // 模拟 desktop.MainWindow 赋值在 await 之前
            Assert.NotNull(mainWindow);

            // 之后才进入异步加载阶段：await settingsService.LoadSettingsAsync()
            await settingsService.LoadSettingsAsync();
            Assert.Equal(LayoutMode.Modern, settingsService.Current.LayoutMode);

            // 生产 App.axaml.cs 在 LoadSettingsAsync 后进行 finalize / init
            mainWindow.FinalizeStartupLayout();

            mainWindow.Show();
            HeadlessAvalonia.Pump(20);

            // 验证最终锁定为 Modern 且对应的外壳元素可见
            Assert.Equal(LayoutMode.Modern, mainWindow.EffectiveLayoutMode);
            Border? rail = mainWindow.FindControl<Border>("ModernActivityRail");
            Border? modernToolbar = mainWindow.FindControl<Border>("ModernToolbarBar");
            Assert.NotNull(rail);
            Assert.NotNull(modernToolbar);
            Assert.True(rail.IsVisible);
            Assert.True(modernToolbar.IsVisible);

            NativeMenuBar? menuBar = mainWindow.FindControl<NativeMenuBar>("ClassicMenuBar");
            Border? classicToolbar = mainWindow.FindControl<Border>("ClassicToolbarBorder");
            Assert.NotNull(menuBar);
            Assert.NotNull(classicToolbar);
            Assert.False(menuBar.IsVisible);
            Assert.False(classicToolbar.IsVisible);

            mainWindow.Close();
            await mainVm.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    });

    [Fact]
    public Task MainWindow_DefaultsToClassic_WithNativeMenuBarAndToolbar() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel(new AppSettings()); // 默认 Classic
        MainWindow window = new() { DataContext = model, Width = 1100, Height = 750 };

        try
        {
            window.Show();
            HeadlessAvalonia.Pump(20);

            Assert.Equal(LayoutMode.Classic, window.EffectiveLayoutMode);

            // Classic 元素可见
            NativeMenuBar? menuBar = window.FindControl<NativeMenuBar>("ClassicMenuBar");
            Border? classicToolbar = window.FindControl<Border>("ClassicToolbarBorder");
            Border? classicSessionToolbar = window.FindControl<Border>("ClassicSessionToolbar");
            Assert.NotNull(menuBar);
            Assert.NotNull(classicToolbar);
            Assert.NotNull(classicSessionToolbar);
            Assert.True(menuBar.IsVisible);
            Assert.True(classicToolbar.IsVisible);
            Assert.True(classicSessionToolbar.IsVisible);

            // Modern 元素隐藏
            Border? rail = window.FindControl<Border>("ModernActivityRail");
            Border? modernToolbar = window.FindControl<Border>("ModernToolbarBar");
            Border? modernSessionTreeHeader = window.FindControl<Border>("ModernSessionTreeHeader");
            Assert.NotNull(rail);
            Assert.NotNull(modernToolbar);
            Assert.NotNull(modernSessionTreeHeader);
            Assert.False(rail.IsVisible);
            Assert.False(modernToolbar.IsVisible);
            Assert.False(modernSessionTreeHeader.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task MainWindow_InitializesModern_WithActivityRailAndCompactToolbar() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        AppSettings settings = new() { LayoutMode = LayoutMode.Modern };
        MainViewModel model = CreateModel(settings);
        MainWindow window = new() { DataContext = model, Width = 1100, Height = 750 };

        try
        {
            window.Show();
            HeadlessAvalonia.Pump(20);

            Assert.Equal(LayoutMode.Modern, window.EffectiveLayoutMode);

            // Modern 元素可见
            Border? rail = window.FindControl<Border>("ModernActivityRail");
            Border? modernToolbar = window.FindControl<Border>("ModernToolbarBar");
            Border? modernSessionTreeHeader = window.FindControl<Border>("ModernSessionTreeHeader");
            Assert.NotNull(rail);
            Assert.NotNull(modernToolbar);
            Assert.NotNull(modernSessionTreeHeader);
            Assert.True(rail.IsVisible);
            Assert.True(modernToolbar.IsVisible);
            Assert.True(modernSessionTreeHeader.IsVisible);

            // Classic 元素隐藏
            NativeMenuBar? menuBar = window.FindControl<NativeMenuBar>("ClassicMenuBar");
            Border? classicToolbar = window.FindControl<Border>("ClassicToolbarBorder");
            Border? classicSessionToolbar = window.FindControl<Border>("ClassicSessionToolbar");
            Assert.NotNull(menuBar);
            Assert.NotNull(classicToolbar);
            Assert.NotNull(classicSessionToolbar);
            Assert.False(menuBar.IsVisible);
            Assert.False(classicToolbar.IsVisible);
            Assert.False(classicSessionToolbar.IsVisible);

            // 验证单一 TerminalWorkspaceView 宿主且无多重挂载
            TerminalWorkspaceView[] hosts = window.GetVisualDescendants()
                .OfType<TerminalWorkspaceView>()
                .ToArray();
            Assert.Single(hosts);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task MainWindow_EffectiveModeLocksAtStartup_DoesNotHotSwapOnSettingsChange() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        AppSettings settings = new() { LayoutMode = LayoutMode.Classic };
        FixedSettingsService settingsService = new(settings);
        MainViewModel model = CreateModelWithSettingsService(settingsService);
        MainWindow window = new() { DataContext = model, Width = 1100, Height = 750 };

        try
        {
            window.Show();
            HeadlessAvalonia.Pump(20);

            Assert.Equal(LayoutMode.Classic, window.EffectiveLayoutMode);

            // 模拟在运行期将配置修改为 Modern 并持久化
            await settingsService.SaveSettingsAsync(new AppSettings { LayoutMode = LayoutMode.Modern });
            HeadlessAvalonia.Pump(20);

            // 当前活动窗口的 EffectiveLayoutMode 保持 Classic，外壳不发生半重组
            Assert.Equal(LayoutMode.Classic, window.EffectiveLayoutMode);
            Assert.True(window.FindControl<NativeMenuBar>("ClassicMenuBar")!.IsVisible);
            Assert.False(window.FindControl<Border>("ModernActivityRail")!.IsVisible);

            // 新打开的第二个窗口读取新配置，生效 Modern
            MainViewModel newModel = CreateModelWithSettingsService(settingsService);
            MainWindow newWindow = new() { DataContext = newModel, Width = 1100, Height = 750 };
            try
            {
                newWindow.Show();
                HeadlessAvalonia.Pump(20);
                Assert.Equal(LayoutMode.Modern, newWindow.EffectiveLayoutMode);
                Assert.True(newWindow.FindControl<Border>("ModernActivityRail")!.IsVisible);
            }
            finally
            {
                newWindow.Close();
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Modern_ScopesTreeActionsToHeader_AndWorkspaceActionsToTopToolbar() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        AppSettings settings = new() { LayoutMode = LayoutMode.Modern };
        MainViewModel model = CreateModel(settings);
        MainWindow window = new() { DataContext = model, Width = 1100, Height = 750 };

        try
        {
            window.Show();
            HeadlessAvalonia.Pump(20);

            // 侧栏树 Header 包含树管理操作（新建会话、新建文件夹、剪切、复制、粘贴、折叠全部）
            Border header = window.FindControl<Border>("ModernSessionTreeHeader")!;
            Assert.True(header.IsVisible);

            Button newSessionBtn = header.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, model.CreateSessionCommand));
            Button newFolderBtn = header.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, model.CreateFolderCommand));
            Button collapseBtn = header.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, model.CollapseAllCommand));
            Button cutBtn = header.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, model.CutNodeCommand));
            Button copyBtn = header.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, model.CopyNodeCommand));
            Button pasteBtn = header.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, model.PasteNodeCommand));

            Assert.NotNull(newSessionBtn);
            Assert.NotNull(newFolderBtn);
            Assert.NotNull(collapseBtn);
            Assert.NotNull(cutBtn);
            Assert.NotNull(copyBtn);
            Assert.NotNull(pasteBtn);

            // 顶部工作区工具栏仅承载工作区全局操作（新建标签、快连、连接所选项、断开、命令面板、文件管理）
            Border topToolbar = window.FindControl<Border>("ModernToolbarBar")!;
            Assert.True(topToolbar.IsVisible);

            Button newTabBtn = topToolbar.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, model.NewTabCommand));
            Button quickConnectBtn = topToolbar.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, model.QuickConnectCommand));
            Button connectSelectedBtn = topToolbar.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, model.ConnectSelectedSessionCommand));
            Button disconnectTabBtn = topToolbar.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, model.DisconnectCurrentTabCommand));
            Button cmdPaletteBtn = topToolbar.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, model.OpenCommandPaletteCommand));
            Button fileManagerBtn = topToolbar.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Command, model.ToggleFileManagerCommand));

            Assert.NotNull(newTabBtn);
            Assert.NotNull(quickConnectBtn);
            Assert.NotNull(connectSelectedBtn);
            Assert.NotNull(disconnectTabBtn);
            Assert.NotNull(cmdPaletteBtn);
            Assert.NotNull(fileManagerBtn);

            // 验证顶部工具栏中绝不包含树专用的操作（CreateFolderCommand、CollapseAllCommand等）
            var topCommands = topToolbar.GetVisualDescendants().OfType<Button>().Select(b => b.Command).ToArray();
            Assert.DoesNotContain(model.CreateFolderCommand, topCommands);
            Assert.DoesNotContain(model.CollapseAllCommand, topCommands);
            Assert.DoesNotContain(model.CutNodeCommand, topCommands);

            // 导出真实 Headless 渲染位图截图到 /tmp/opencode/kei-toolbar-before-or-after/
            string outputDir = "/tmp/opencode/kei-toolbar-before-or-after";
            Directory.CreateDirectory(outputDir);

            using (var modernFrame = window.CaptureRenderedFrame())
            {
                if (modernFrame != null)
                {
                    modernFrame.Save(Path.Combine(outputDir, "modern-sidebar-open.png"));
                }
            }

            // 测试收起侧栏时的渲染效果（验证侧栏收起时全局顶工具不突兀空大片）
            model.IsSessionManagerPinned = false;
            model.IsSessionManagerVisible = false;
            HeadlessAvalonia.Pump(20);
            using (var modernCollapsedFrame = window.CaptureRenderedFrame())
            {
                if (modernCollapsedFrame != null)
                {
                    modernCollapsedFrame.Save(Path.Combine(outputDir, "modern-sidebar-collapsed.png"));
                }
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Classic_RendersAndCapturesScreenshot() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        AppSettings settings = new() { LayoutMode = LayoutMode.Classic };
        MainViewModel model = CreateModel(settings);
        MainWindow window = new() { DataContext = model, Width = 1100, Height = 750 };

        try
        {
            window.Show();
            HeadlessAvalonia.Pump(20);

            string outputDir = "/tmp/opencode/kei-toolbar-before-or-after";
            Directory.CreateDirectory(outputDir);

            using var classicFrame = window.CaptureRenderedFrame();
            if (classicFrame != null)
            {
                classicFrame.Save(Path.Combine(outputDir, "classic-window.png"));
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ModernHamburgerMenu_OpensContextMenu_WithAllNativeCommands() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        AppSettings settings = new() { LayoutMode = LayoutMode.Modern };
        MainViewModel model = CreateModel(settings);
        MainWindow window = new() { DataContext = model, Width = 1100, Height = 750 };

        try
        {
            window.Show();
            HeadlessAvalonia.Pump(20);

            Button menuButton = window.FindControl<Button>("ModernAppMenuButton")!;
            Assert.NotNull(menuButton);

            // 触发汉堡按钮点击
            menuButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            HeadlessAvalonia.Pump(20);

            // 找到弹出的 ContextMenu
            ContextMenu? contextMenu = menuButton.ContextMenu;
            Assert.NotNull(contextMenu);
            Assert.True(contextMenu.IsOpen);
            Assert.True(contextMenu.Items.Count > 0);

            // 验证包含所有主菜单一级节点（文件、编辑、查看、外观、工具、帮助）
            var headers = contextMenu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToArray();
            Assert.Contains(headers, h => h == Strings.Get("Menu.File"));
            Assert.Contains(headers, h => h == Strings.Get("Menu.Edit"));
            Assert.Contains(headers, h => h == Strings.Get("Menu.Tools"));

            // 验证子菜单完整保留了命令绑定（例如文件菜单中的新建标签与快速连接）
            MenuItem fileItem = contextMenu.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == Strings.Get("Menu.File"));
            Assert.NotEmpty(fileItem.Items);
            MenuItem newTabItem = fileItem.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == Strings.Get("Menu.File.NewTab"));
            Assert.NotNull(newTabItem.Command);
            Assert.Same(model.NewTabCommand, newTabItem.Command);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ModernMenu_ClickRoutesExitAndAbout_ClosesOwnedAboutWindow() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        FixedSettingsService settings = new(new AppSettings { LayoutMode = LayoutMode.Modern, ConfirmBeforeClose = false });
        MainViewModel model = CreateModelWithSettingsService(settings);
        MainWindow window = new() { DataContext = model, Width = 1100, Height = 750 };
        bool exited = false;
        window.SetQuitConfirmationService(new QuitConfirmationService(new ScriptedInteraction(), () =>
        {
            exited = true;
            return Task.CompletedTask;
        }, settings));

        try
        {
            window.Show();
            HeadlessAvalonia.Pump(20);
            ContextMenu menu = OpenModernMenu(window);

            ClickMenu(FindMenu(menu, Strings.Get("Menu.File.Exit")));
            await Task.Delay(30);
            HeadlessAvalonia.Pump(20);
            Assert.True(exited);
            Assert.Empty(window.OwnedWindows);

            ClickMenu(FindMenu(menu, Strings.Get("Menu.Help.About")));
            HeadlessAvalonia.Pump(30);
            Window about = Assert.Single(window.OwnedWindows);
            Assert.Equal(Strings.Get("About.Title"), about.Title);
            Assert.True(about.IsVisible);
            about.Close();
            HeadlessAvalonia.Pump(20);
            Assert.False(about.IsVisible);
            Assert.Empty(window.OwnedWindows);
        }
        finally
        {
            foreach (Window owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            await model.DisposeAsync();
        }
    });

    [Fact]
    public Task ModernMenu_CheckItemsOperateAndFollowSourceWhileOpen_ThenRelease() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel(new AppSettings { LayoutMode = LayoutMode.Modern });
        model.ApplySessionManagerSettings();
        MainWindow window = new() { DataContext = model, Width = 1100, Height = 750 };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump(20);
            ContextMenu menu = OpenModernMenu(window);
            MenuItem sessions = FindMenu(menu, Strings.Get("Menu.View.SessionManager"));
            MenuItem composeShow = FindMenu(menu, Strings.Get("Menu.View.Compose.Show"));
            MenuItem singleLine = FindMenu(menu, Strings.Get("Menu.View.Compose.SingleLine"));
            MenuItem multiLine = FindMenu(menu, Strings.Get("Menu.View.Compose.MultiLine"));

            Assert.Equal(MenuItemToggleType.CheckBox, sessions.ToggleType);
            Assert.Equal(model.IsSessionManagerPinned, sessions.IsChecked);
            Assert.Equal(MenuItemToggleType.CheckBox, composeShow.ToggleType);
            Assert.Equal(model.IsComposeBarVisible, composeShow.IsChecked);
            Assert.Equal(model.IsSingleLineCompose, singleLine.IsChecked);
            Assert.Equal(model.IsMultiLineCompose, multiLine.IsChecked);

            sessions.IsChecked = !model.IsSessionManagerPinned;
            Assert.Equal(sessions.IsChecked, model.IsSessionManagerPinned);
            composeShow.IsChecked = !model.IsComposeBarVisible;
            Assert.Equal(composeShow.IsChecked, model.IsComposeBarVisible);
            multiLine.IsChecked = true;
            Assert.True(model.IsMultiLineCompose);
            Assert.False(model.IsSingleLineCompose);
            Assert.False(singleLine.IsChecked);

            model.IsSessionManagerPinned = !model.IsSessionManagerPinned;
            Assert.Equal(model.IsSessionManagerPinned, sessions.IsChecked);

            NativeMenuItem exitSource = FindNative(window, Strings.Get("Menu.File.Exit"));
            NativeMenuItem newTabSource = FindNative(window, Strings.Get("Menu.File.NewTab"));
            MenuItem exitItem = FindMenu(menu, Strings.Get("Menu.File.Exit"));
            MenuItem newTabItem = FindMenu(menu, Strings.Get("Menu.File.NewTab"));
            exitSource.Header = "Exit renamed";
            exitSource.IsEnabled = false;
            newTabSource.CommandParameter = "fresh-parameter";
            Assert.Equal("Exit renamed", exitItem.Header?.ToString());
            Assert.False(exitItem.IsEnabled);
            Assert.Equal("fresh-parameter", newTabItem.CommandParameter);

            menu.Close();
            HeadlessAvalonia.Pump();
            exitSource.Header = "After close";
            exitSource.IsEnabled = true;
            newTabSource.CommandParameter = "stale";
            Assert.Equal("Exit renamed", exitItem.Header?.ToString());
            Assert.False(exitItem.IsEnabled);
            Assert.Equal("fresh-parameter", newTabItem.CommandParameter);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ModernRailHover_OpensUnpinnedSidebar_AndEscapeRestoresFocus() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel(new AppSettings
        {
            LayoutMode = LayoutMode.Modern,
            SessionManagerPinned = false
        });
        model.ApplySessionManagerSettings();
        MainWindow window = new() { DataContext = model, Width = 1100, Height = 750 };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump(20);
            Assert.False(model.IsSessionManagerVisible);
            Button rail = window.FindControl<Button>("ModernSessionsRailButton")!;
            Button focusTarget = window.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Command, model.NewTabCommand) && button.IsEffectivelyVisible);
            focusTarget.Focus();
            HeadlessAvalonia.Pump();
            Assert.Same(focusTarget, window.FocusManager!.GetFocusedElement());

            Move(window, rail);
            Assert.True(model.IsSessionManagerVisible);
            Assert.False(model.IsSessionManagerPinned);
            Assert.Same(focusTarget, window.FocusManager.GetFocusedElement());

            TextBox filter = window.FindControl<TextBox>("SessionManagerFilter")!;
            filter.Focus();
            HeadlessAvalonia.Pump();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            HeadlessAvalonia.Pump();
            Assert.False(model.IsSessionManagerVisible);
            Assert.Same(focusTarget, window.FocusManager.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task EffectiveLayout_InitializesOnce_NullContextDoesNotBlock_RebindDoesNotHotSwap() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        FixedSettingsService settings = new(new AppSettings { LayoutMode = LayoutMode.Classic });
        MainViewModel classic = CreateModelWithSettingsService(settings);
        MainViewModel? rebound = null;
        MainViewModel? freshModel = null;
        MainWindow window = new() { Width = 1100, Height = 750 };
        try
        {
            window.DataContext = null;
            window.DataContext = classic;
            window.Show();
            HeadlessAvalonia.Pump(20);
            Assert.Equal(LayoutMode.Classic, window.EffectiveLayoutMode);
            TerminalWorkspaceView host = window.FindControl<TerminalWorkspaceView>("WorkspaceHost")!;
            ((IConnectionHost)classic).OpenTab(new ResolvedSessionConfig(
                Guid.NewGuid(), "kept", "kept.example", 22, "ops", null, "xterm-256color", null, null, new System.Collections.Generic.Dictionary<string, string>()));
            HeadlessAvalonia.Pump(20);
            TerminalTabViewModel tab = Assert.Single(classic.Tabs);
            TerminalControl terminal = tab.Terminal;
            TerminalConnectionView connection = Assert.Single(window.GetVisualDescendants().OfType<TerminalConnectionView>());

            await settings.SaveSettingsAsync(new AppSettings { LayoutMode = LayoutMode.Modern, ConfirmBeforeClose = false });
            HeadlessAvalonia.Pump(20);

            // 同一 VM 保存只改下次启动值，当前活动连接必须仍挂在同一宿主上。
            Assert.Equal(LayoutMode.Classic, window.EffectiveLayoutMode);
            Assert.Same(classic, window.DataContext);
            Assert.True(window.FindControl<NativeMenuBar>("ClassicMenuBar")!.IsVisible);
            Assert.False(window.FindControl<Border>("ModernActivityRail")!.IsVisible);
            AssertAttachedWorkspace(window, host, connection, tab, terminal);

            rebound = CreateModelWithSettingsService(settings);
            window.DataContext = null;
            window.DataContext = rebound;
            HeadlessAvalonia.Pump(20);
            ((IConnectionHost)rebound).OpenTab(new ResolvedSessionConfig(
                Guid.NewGuid(), "next", "next.example", 22, "ops", null, "xterm-256color", null, null, new System.Collections.Generic.Dictionary<string, string>()));
            HeadlessAvalonia.Pump(20);

            // 换 VM 不热切外壳。活动内容必须换成新 VM 的连接，旧视图卸下，新连接不丢。
            Assert.Equal(LayoutMode.Classic, window.EffectiveLayoutMode);
            Assert.True(window.FindControl<NativeMenuBar>("ClassicMenuBar")!.IsVisible);
            Assert.False(window.FindControl<Border>("ModernActivityRail")!.IsVisible);
            Assert.Same(rebound, window.DataContext);
            Assert.Same(host, window.FindControl<TerminalWorkspaceView>("WorkspaceHost"));
            Assert.True(host.IsAttachedToVisualTree());
            Assert.Same(rebound, host.DataContext);
            Assert.False(connection.IsAttachedToVisualTree());
            Assert.Same(terminal, tab.Terminal);
            TerminalTabViewModel reboundTab = Assert.Single(rebound.Tabs);
            TerminalConnectionView reboundConnection = Assert.Single(window.GetVisualDescendants().OfType<TerminalConnectionView>());
            Assert.True(reboundConnection.IsAttachedToVisualTree());
            Assert.Same(reboundTab, reboundConnection.DataContext);
            Assert.NotSame(connection, reboundConnection);
            Assert.Contains(rebound.WorkspaceTabs, item => ReferenceEquals(item, reboundTab));
            Assert.Single(window.GetVisualDescendants().OfType<TerminalWorkspaceView>());

            freshModel = CreateModelWithSettingsService(settings);
            MainWindow fresh = new() { DataContext = freshModel, Width = 1100, Height = 750 };
            try
            {
                fresh.Show();
                HeadlessAvalonia.Pump(20);
                Assert.Equal(LayoutMode.Modern, fresh.EffectiveLayoutMode);
                Assert.True(fresh.FindControl<Border>("ModernActivityRail")!.IsVisible);
            }
            finally
            {
                fresh.Close();
            }
        }
        finally
        {
            window.Close();
            await classic.DisposeAsync();
            if (rebound != null) await rebound.DisposeAsync();
            if (freshModel != null) await freshModel.DisposeAsync();
        }
    });

    [Fact]
    public Task ModernToolbar_NarrowWindow_ScrollReachesActions_AndKeepsWorkspace() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel(new AppSettings
        {
            LayoutMode = LayoutMode.Modern,
            SessionManagerPinned = false
        });
        model.ApplySessionManagerSettings();
        MainWindow window = new() { DataContext = model, MinWidth = 220, Width = 240, Height = 700 };
        try
        {
            ((IConnectionHost)model).OpenTab(new ResolvedSessionConfig(
                Guid.NewGuid(), "narrow", "narrow.example", 22, "ops", null, "xterm-256color", null, null, new System.Collections.Generic.Dictionary<string, string>()));
            window.Show();
            HeadlessAvalonia.Pump(20);
            Border toolbar = window.FindControl<Border>("ModernToolbarBar")!;
            ScrollViewer scroll = Assert.Single(toolbar.GetVisualDescendants().OfType<ScrollViewer>());
            ScrollContentPresenter viewport = Assert.Single(scroll.GetVisualDescendants().OfType<ScrollContentPresenter>());
            Button files = toolbar.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Command, model.ToggleFileManagerCommand));
            TerminalWorkspaceView host = window.FindControl<TerminalWorkspaceView>("WorkspaceHost")!;
            TerminalTabViewModel tab = Assert.Single(model.Tabs);
            TerminalControl terminal = tab.Terminal;
            TerminalConnectionView connection = Assert.Single(window.GetVisualDescendants().OfType<TerminalConnectionView>());
            AssertAttachedWorkspace(window, host, connection, tab, terminal);
            Assert.False(tab.IsFileManagerVisible);
            Assert.True(scroll.Extent.Width > scroll.Viewport.Width + 1);
            AssertNotFullyInside(files, viewport);

            files.BringIntoView();
            HeadlessAvalonia.Pump();
            AssertFullyInside(files, viewport);
            Point localCenter = new(files.Bounds.Width / 2, files.Bounds.Height / 2);
            Point centerInViewport = files.TranslatePoint(localCenter, viewport)!.Value;
            object? hit = viewport.InputHitTest(centerInViewport);
            Assert.True(hit is Visual visual && (ReferenceEquals(visual, files) || files.IsVisualAncestorOf(visual)),
                $"hit={hit?.GetType().FullName} viewportPoint={centerInViewport}");
            Point? click = null;
            for (double x = 1; x < files.Bounds.Width - 1 && click == null; x += 2)
            {
                for (double y = 1; y < files.Bounds.Height - 1 && click == null; y += 2)
                {
                    Point candidate = files.TranslatePoint(new Point(x, y), window)!.Value;
                    object? candidateHit = window.InputHitTest(candidate);
                    if (candidateHit is Visual candidateVisual
                        && (ReferenceEquals(candidateVisual, files) || files.IsVisualAncestorOf(candidateVisual)))
                    {
                        click = candidate;
                    }
                }
            }

            Assert.True(click.HasValue, "滚动后的文件按钮没有可命中的窗口坐标");
            window.MouseMove(click.Value, RawInputModifiers.None);
            window.MouseDown(click.Value, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(click.Value, MouseButton.Left, RawInputModifiers.None);
            HeadlessAvalonia.Pump();
            Assert.True(tab.IsFileManagerVisible);
            AssertAttachedWorkspace(window, host, connection, tab, terminal);
        }
        finally
        {
            window.Close();
        }
    });

    private static void AssertAttachedWorkspace(
        MainWindow window,
        TerminalWorkspaceView host,
        TerminalConnectionView connection,
        TerminalTabViewModel tab,
        TerminalControl terminal)
    {
        Assert.Same(host, window.FindControl<TerminalWorkspaceView>("WorkspaceHost"));
        Assert.True(host.IsAttachedToVisualTree());
        Assert.Same(window.DataContext, host.DataContext);
        Assert.Same(connection, Assert.Single(window.GetVisualDescendants().OfType<TerminalConnectionView>()));
        Assert.True(connection.IsAttachedToVisualTree());
        Assert.Same(tab, connection.DataContext);
        Assert.Same(terminal, tab.Terminal);
        Assert.Single(window.GetVisualDescendants().OfType<TerminalWorkspaceView>());
    }

    private static void AssertFullyInside(Control target, Control viewport)
    {
        Rect rect = BoundsIn(target, viewport);
        Rect visible = new(viewport.Bounds.Size);
        Assert.True(visible.Contains(rect), $"{rect} outside viewport {visible}");
    }

    private static void AssertNotFullyInside(Control target, Control viewport)
    {
        Rect rect = BoundsIn(target, viewport);
        Rect visible = new(viewport.Bounds.Size);
        Assert.False(visible.Contains(rect), $"{rect} unexpectedly inside viewport {visible}");
    }

    private static Rect BoundsIn(Control target, Control viewport)
    {
        Point origin = target.TranslatePoint(default, viewport)!.Value;
        return new Rect(origin, target.Bounds.Size);
    }

    private static ContextMenu OpenModernMenu(MainWindow window)
    {
        Button menuButton = window.FindControl<Button>("ModernAppMenuButton")!;
        menuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        HeadlessAvalonia.Pump(20);
        ContextMenu menu = menuButton.ContextMenu!;
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static void ClickMenu(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        HeadlessAvalonia.Pump();
    }

    private static MenuItem FindMenu(Control root, string header)
        => FindMenuOrNull(root, header) ?? throw new InvalidOperationException("未找到菜单项 " + header);

    private static MenuItem? FindMenuOrNull(Control root, string header)
    {
        System.Collections.IEnumerable items = root switch
        {
            ContextMenu menu => menu.Items,
            MenuItem item => item.Items,
            _ => Array.Empty<object>()
        };
        foreach (object? entry in items)
        {
            if (entry is not MenuItem child) continue;
            if (child.Header?.ToString() == header) return child;
            MenuItem? nested = FindMenuOrNull(child, header);
            if (nested != null) return nested;
        }

        return null;
    }

    private static NativeMenuItem FindNative(MainWindow window, string header)
    {
        NativeMenu menu = NativeMenu.GetMenu(window)!;
        return Enumerate(menu.Items).First(item => item.Header == header);

        static System.Collections.Generic.IEnumerable<NativeMenuItem> Enumerate(System.Collections.IEnumerable entries)
        {
            foreach (object? entry in entries)
            {
                if (entry is not NativeMenuItem item) continue;
                yield return item;
                if (item.Menu != null)
                {
                    foreach (NativeMenuItem child in Enumerate(item.Menu.Items)) yield return child;
                }
            }
        }
    }

    private static void Move(Window window, Control control)
    {
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        HeadlessAvalonia.Pump();
    }

    private static MainViewModel CreateModel(AppSettings settings) => CreateModelWithSettingsService(new FixedSettingsService(settings));

    private static MainViewModel CreateModelWithSettingsService(ISettingsService settingsService)
    {
        SqliteConnectionFactory database = new("Data Source=:memory:");
        InternalVaultManager vault = new(database);
        MainViewModel model = new(
            new SqliteTreeRepository(database),
            new SqliteIdentityRepository(database),
            vault,
            vault,
            settingsService,
            new SshSessionFactory());
        model.CurrentSettings.ConfirmBeforeClose = false;
        return model;
    }
}
