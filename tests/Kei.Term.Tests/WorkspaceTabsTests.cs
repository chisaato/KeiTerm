using Kei.Term.App.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Dock.Model.Controls;
using Dock.Model.Core;
using Kei.Term.App.Workspaces;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Services;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;
using Xunit;

namespace Kei.Term.Tests;

public class WorkspaceTabsTests
{
    [Fact]
    public Task ReopenAfterClosingAllTabs_RendersTerminalAndStartPage() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        MainWindow window = new() { DataContext = model, Width = 1200, Height = 800 };
        try
        {
            window.Show();
            model.NewTabCommand.Execute(null);
            await model.CloseWorkspaceTabCommand.ExecuteAsync(Assert.Single(model.NewTabs));
            HeadlessAvalonia.Pump();
            Assert.Empty(model.WorkspaceTabs);

            // 关闭最后一页后取消退出，再连接仍需有实际可见的文档内容。
            ((IConnectionHost)model).OpenTab(Config("reopened"));
            HeadlessAvalonia.Pump();
            TerminalConnectionView connection = Assert.Single(window.GetVisualDescendants().OfType<TerminalConnectionView>());
            Assert.True(connection.IsEffectivelyVisible);
            Assert.True(connection.Bounds.Width > 0 && connection.Bounds.Height > 0);
            Assert.Same(Assert.Single(model.Tabs), connection.DataContext);

            await model.CloseWorkspaceTabCommand.ExecuteAsync(Assert.Single(model.Tabs));
            model.NewTabCommand.Execute(null);
            HeadlessAvalonia.Pump();
            NewTabView starter = Assert.Single(window.GetVisualDescendants().OfType<NewTabView>(), view => view.IsEffectivelyVisible);
            Assert.True(starter.IsEffectivelyVisible);
            Assert.True(starter.Bounds.Width > 0 && starter.Bounds.Height > 0);
        }
        finally
        {
            foreach (TerminalTabViewModel terminal in model.Tabs.ToArray()) await model.CloseTabCommand.ExecuteAsync(terminal);
            window.Close();
            await model.DisposeAsync();
        }
    });

    [Fact]
    public Task CloseToRight_FollowsDockOrder_AndPreservesOtherGroups() => HeadlessAvalonia.RunAsync(async () =>
    {
        MainViewModel model = CreateModel();
        try
        {
            IConnectionHost host = model;
            host.OpenTab(Config("left"));
            host.OpenTab(Config("right"));
            host.OpenTab(Config("reordered"));
            TerminalTabViewModel left = model.Tabs[0];
            TerminalTabViewModel right = model.Tabs[1];
            TerminalTabViewModel reordered = model.Tabs[2];
            IDocumentDock group = Assert.IsAssignableFrom<IDocumentDock>(model.Workspace.FindDocument(left)!.Owner);
            model.Workspace.SplitTab(left, group, DockOperation.Left);
            model.Workspace.ReorderTab(reordered, 0);
            reordered.RequestCloseToRightCommand.Execute(null);
            Assert.DoesNotContain(right, model.WorkspaceTabs);
            Assert.Contains(left, model.WorkspaceTabs);
            Assert.Contains(reordered, model.WorkspaceTabs);
        }
        finally
        {
            foreach (TerminalTabViewModel terminal in model.Tabs.ToArray()) await model.CloseTabCommand.ExecuteAsync(terminal);
            await model.DisposeAsync();
        }
    });

    [Fact]
    public Task MainWindow_MixedDockGroups_KeepStartPageVisibleAndReplaceItsOwnSlot() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        MainWindow window = new() { DataContext = model, Width = 1200, Height = 800 };
        try
        {
            window.Show();
            ((IConnectionHost)model).OpenTab(Config("existing"));
            TerminalTabViewModel existing = Assert.Single(model.Tabs);
            model.NewTabCommand.Execute(null);
            NewTabViewModel starter = Assert.Single(model.NewTabs);
            IDocumentDock firstGroup = Assert.IsAssignableFrom<IDocumentDock>(model.Workspace.FindDocument(existing)!.Owner);
            model.Workspace.SplitTab(starter, firstGroup, DockOperation.Right);
            HeadlessAvalonia.Pump();
            IDocumentDock starterGroup = Assert.IsAssignableFrom<IDocumentDock>(model.Workspace.FindDocument(starter)!.Owner);
            Assert.NotSame(firstGroup, starterGroup);
            Assert.Single(window.GetVisualDescendants().OfType<TerminalWorkspaceView>());
            model.SelectTabCommand.Execute(existing);
            HeadlessAvalonia.Pump();
            Assert.Single(window.GetVisualDescendants().OfType<NewTabView>(), view => view.IsEffectivelyVisible);
            Assert.True(window.GetVisualDescendants().OfType<TerminalConnectionView>().Single().IsEffectivelyVisible);

            model.Interaction = new ScriptedInteraction
            {
                PaletteSelection = palette => palette.Results.First(item => item.Action == PaletteAction.NewConnection),
                QuickConnectAction = () =>
                {
                    // 对话框打开期间用户切到左组；新连接必须仍替换右组的启动页。
                    model.SelectTabCommand.Execute(existing);
                    ((IConnectionHost)model).OpenTab(Config("replacement"));
                    return Task.CompletedTask;
                }
            };
            await starter.QuickConnectCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();
            TerminalTabViewModel replacement = model.Tabs.Single(tab => tab != existing);
            Assert.Same(starterGroup, model.Workspace.FindDocument(replacement)!.Owner);
            Assert.Same(firstGroup, model.Workspace.FindDocument(existing)!.Owner);
            Assert.Empty(model.NewTabs);
            Assert.Same(replacement, model.SelectedTab);
            Assert.Equal(2, window.GetVisualDescendants().OfType<TerminalConnectionView>().Count());
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<NewTabView>(), view => view.IsEffectivelyVisible);
        }
        finally
        {
            foreach (TerminalTabViewModel terminal in model.Tabs.ToArray()) await model.CloseTabCommand.ExecuteAsync(terminal);
            window.Close();
            await model.DisposeAsync();
        }
    });

    [Fact]
    public Task DockCloseStartPage_ReturnsToWelcome_AndOnlyNextShortcutRequestsExit() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        MainWindow window = new() { DataContext = model };
        int closeRequests = 0;
        model.Interaction = new ScriptedInteraction { CloseWindowAction = () => closeRequests++ };
        try
        {
            window.Show();
            model.NewTabCommand.Execute(null);
            NewTabViewModel first = Assert.Single(model.NewTabs);
            model.NewTabCommand.Execute(null);
            NewTabViewModel second = model.NewTabs[1];
            IDocumentDock group = Assert.IsAssignableFrom<IDocumentDock>(model.Workspace.FindDocument(first)!.Owner);
            model.Workspace.SplitTab(second, group, DockOperation.Bottom);
            HeadlessAvalonia.Pump();
            model.Workspace.Factory.CloseDockable(model.Workspace.FindDocument(second)!);
            HeadlessAvalonia.Pump();
            Assert.Same(first, Assert.Single(model.WorkspaceTabs));
            Assert.Same(first, model.ActiveWorkspaceTab);
            Assert.Single(model.NewTabs);
            Assert.Equal(0, closeRequests);
            await model.CloseCurrentWorkspaceTabCommand.ExecuteAsync(null);
            Assert.Empty(model.WorkspaceTabs);
            Assert.False(model.HasTabs);
            HeadlessAvalonia.Pump();
            Assert.True(window.IsVisible);
            Assert.Empty(window.OwnedWindows);
            Assert.Contains(window.GetVisualDescendants().OfType<NewTabView>(), view => view.IsEffectivelyVisible);
            Assert.Equal(0, closeRequests);
            await model.CloseCurrentWorkspaceTabCommand.ExecuteAsync(null);
            Assert.Equal(1, closeRequests);
        }
        finally { window.Close(); await model.DisposeAsync(); }
    });

    [Fact]
    public Task CloseLastTerminal_ReleasesSession_AndKeepsWelcomeWindowOpen() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        MainWindow window = new() { DataContext = model };
        model.Interaction = new ScriptedInteraction { CloseWindowAction = window.Close };
        DeferredSession session = new();
        try
        {
            window.Show();
            IConnectionTarget target = ((IConnectionHost)model).OpenTab(Config("connected"));
            target.AttachSession(session);
            target.MarkConnected();
            HeadlessAvalonia.Pump();
            Task close = model.CloseCurrentWorkspaceTabCommand.ExecuteAsync(null);
            await session.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(model.WorkspaceTabs);
            Assert.True(window.IsVisible);
            Assert.False(close.IsCompleted);
            session.AllowDisposal.TrySetResult();
            await close.WaitAsync(TimeSpan.FromSeconds(5));
            HeadlessAvalonia.Pump();
            Assert.True(window.IsVisible);
            Assert.False(model.HasTabs);
            Assert.Contains(window.GetVisualDescendants().OfType<NewTabView>(), view => view.IsEffectivelyVisible);
            Assert.False(session.IsConnected);
        }
        finally { session.AllowDisposal.TrySetResult(); window.Close(); await model.DisposeAsync(); }
    });

    [Fact]
    public Task NewTab_ConnectionCancelKeepsPage_AndConnectionStartReplacesItsSlot() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        ScriptedInteraction interaction = new();
        model.Interaction = interaction;
        try
        {
            model.NewTabCommand.Execute(null);
            NewTabViewModel starter = Assert.Single(model.NewTabs);
            await starter.QuickConnectCommand.ExecuteAsync(null);
            Assert.Same(starter, Assert.Single(model.WorkspaceTabs));
            Assert.Empty(model.Tabs);
            interaction.QuickConnectAction = () =>
            {
                // 模拟选连接时另开一页；连接仍需替换发起操作的那一页。
                model.NewTabCommand.Execute(null);
                ((IConnectionHost)model).OpenTab(Config("connected"));
                return Task.CompletedTask;
            };
            interaction.PaletteSelection = palette => palette.Results.First(item => item.Action == PaletteAction.NewConnection);
            await starter.QuickConnectCommand.ExecuteAsync(null);
            TerminalTabViewModel terminal = Assert.Single(model.Tabs);
            Assert.Same(terminal, model.WorkspaceTabs[0]);
            Assert.Same(terminal, model.ActiveWorkspaceTab);
            Assert.DoesNotContain(starter, model.NewTabs);
            Assert.Single(model.NewTabs);
        }
        finally
        {
            foreach (TerminalTabViewModel terminal in model.Tabs.ToArray()) await model.CloseTabCommand.ExecuteAsync(terminal);
            await model.DisposeAsync();
        }
    });

    [Fact]
    public Task MenuGestures_CustomPaletteShortcutUpdates() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        MainWindow window = new() { DataContext = model };
        try
        {
            window.Show();
            NativeMenuItem[] items = Menus(NativeMenu.GetMenu(window)!).ToArray();
            NativeMenuItem palette = items.Single(item => ReferenceEquals(item.Command, model.OpenCommandPaletteCommand));
            model.CurrentSettings.CommandPaletteShortcut = "Primary+Alt+P";
            model.RefreshShortcuts();
            Assert.Equal(AppShortcuts.CommandPalette("Primary+Alt+P"), palette.Gesture);
        }
        finally { window.Close(); model.DisposeAsync().GetAwaiter().GetResult(); }
    });

    private static IEnumerable<NativeMenuItem> Menus(NativeMenu menu)
    {
        foreach (NativeMenuItem item in menu.Items.OfType<NativeMenuItem>())
        {
            yield return item;
            if (item.Menu != null)
                foreach (NativeMenuItem child in Menus(item.Menu)) yield return child;
        }
    }

    [Fact]
    public Task NewTabsAndTerminals_SelectReorderClose_KeepOrderWithoutExtraTerminals() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        MainWindow window = new() { DataContext = model };
        try
        {
            window.Show();
            model.NewTabCommand.Execute(null);
            NewTabViewModel first = Assert.Single(model.NewTabs);
            model.NewTabCommand.Execute(null);
            NewTabViewModel second = model.NewTabs[1];
            window.UpdateLayout();
            // Dock 在调度队列内创建活动文档的模板，等布局/渲染完成再检查实际视图。
            HeadlessAvalonia.Pump();
            Assert.Empty(model.Tabs);
            Assert.True(model.HasTabs);
            Assert.Same(second, model.ActiveWorkspaceTab);
            Assert.False(first.IsSelected);
            Assert.True(second.IsSelected);
            Assert.Single(window.GetVisualDescendants().OfType<NewTabView>(), view => view.IsEffectivelyVisible);
            model.SelectWorkspaceTabCommand.Execute(first);
            model.MoveWorkspaceTab(0, 1);
            Assert.Same(first, model.ActiveWorkspaceTab);
            Assert.Same(first, model.WorkspaceTabs[1]);
            model.CloseWorkspaceTabCommand.ExecuteAsync(first).GetAwaiter().GetResult();
            Assert.Same(second, model.ActiveWorkspaceTab);
            model.CloseWorkspaceTabCommand.ExecuteAsync(second).GetAwaiter().GetResult();
            Assert.Null(model.ActiveWorkspaceTab);
            Assert.False(model.HasTabs);

            model.NewTabCommand.Execute(null);
            NewTabViewModel starter = Assert.Single(model.NewTabs);
            IConnectionHost host = model;
            host.OpenTab(Config("first"));
            host.OpenTab(Config("second"));
            TerminalTabViewModel terminalFirst = model.Tabs[0];
            TerminalTabViewModel terminalSecond = model.Tabs[1];
            model.SelectWorkspaceTabCommand.Execute(starter);
            Assert.Null(model.SelectedTab);
            Assert.False(terminalFirst.IsSelected);
            Assert.False(terminalSecond.IsSelected);
            model.MoveWorkspaceTab(2, 0);
            Assert.Same(terminalSecond, model.Tabs[0]);
            Assert.Same(terminalFirst, model.Tabs[1]);
            model.SelectWorkspaceTabCommand.Execute(terminalSecond);
            Assert.Same(terminalSecond, model.SelectedTab);
            await model.CloseWorkspaceTabCommand.ExecuteAsync(terminalSecond);
            Assert.Same(starter, model.ActiveWorkspaceTab);
            Assert.Null(model.SelectedTab);
            Assert.False(terminalFirst.IsSelected);
            terminalFirst.RequestCloseOthersCommand.Execute(null);
            Assert.Same(terminalFirst, Assert.Single(model.WorkspaceTabs));
            Assert.Empty(model.NewTabs);
            Assert.Same(terminalFirst, model.SelectedTab);
        }
        finally
        {
            foreach (TerminalTabViewModel terminal in model.Tabs.ToArray()) await model.CloseTabCommand.ExecuteAsync(terminal);
            window.Close();
            await model.DisposeAsync();
        }
    });

    private static MainViewModel CreateModel()
    {
        SqliteConnectionFactory database = new("Data Source=:memory:");
        InternalVaultManager vault = new(database);
        MainViewModel model = new(new SqliteTreeRepository(database), new SqliteIdentityRepository(database), vault, vault,
            new FixedSettingsService(), new SshSessionFactory());
        model.CurrentSettings.ConfirmBeforeClose = false;
        return model;
    }

    private static ResolvedSessionConfig Config(string name) => new(Guid.NewGuid(), name, name + ".example", 22, "ops", null,
        "xterm-256color", null, null, new Dictionary<string, string>());

    private sealed class DeferredSession : ITerminalSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected { get; private set; } = true;
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CS0067
        public event Action<byte[]>? OutputReceived;
        public event Action<Exception?>? Disconnected;
#pragma warning restore CS0067
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            DisposalStarted.TrySetResult();
            await AllowDisposal.Task;
            IsConnected = false;
        }
    }
}
