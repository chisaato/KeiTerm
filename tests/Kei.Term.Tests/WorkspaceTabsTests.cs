using Kei.Term.App.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.App.Views.Controls;
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
    public Task CloseLastTerminal_WaitsForSessionRelease_BeforeRequestingWindowClose() => HeadlessAvalonia.RunAsync(async () =>
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
            Assert.False(window.IsVisible);
            Assert.False(session.IsConnected);
        }
        finally { session.AllowDisposal.TrySetResult(); window.Close(); await model.DisposeAsync(); }
    });

    [Fact]
    public Task SessionInRealTree_ExposesDuplicateCommand() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        SessionNode session = new() { Name = "LAN", Host = "192.168.1.42" };
        model.TreeNodes.Add(new VirtualRootNode { IsExpanded = true, Children = [session] });
        model.HasNodes = true;
        model.SelectedTreeNode = session;
        MainWindow window = new() { DataContext = model };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            SessionTreeItemView view = window.GetVisualDescendants().OfType<SessionTreeItemView>().Single(item => ReferenceEquals(item.DataContext, session));
            Control owner = view.GetVisualAncestors().OfType<Control>().First(control => control.ContextMenu != null);
            Assert.Same(session, owner.DataContext);
            owner.ContextMenu!.Open(owner);
            HeadlessAvalonia.Pump();
            Assert.Contains(owner.ContextMenu.Items.OfType<MenuItem>(), item => ReferenceEquals(item.Command, model.DuplicateSelectedSessionCommand));
            owner.ContextMenu.Close();
        }
        finally { window.Close(); model.DisposeAsync().GetAwaiter().GetResult(); }
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
    public Task MenuGestures_MatchActions_AndCustomPaletteShortcutUpdates() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        MainWindow window = new() { DataContext = model };
        try
        {
            window.Show();
            NativeMenuItem[] items = Menus(NativeMenu.GetMenu(window)!).ToArray();
            Assert.Equal(AppShortcuts.NewTab, items.Single(item => ReferenceEquals(item.Command, model.NewTabCommand)).Gesture);
            Assert.Equal(AppShortcuts.CloseTab, items.Single(item => ReferenceEquals(item.Command, model.CloseCurrentWorkspaceTabCommand)).Gesture);
            Assert.Equal(AppShortcuts.ConnectSavedSession, items.Single(item => ReferenceEquals(item.Command, model.ConnectSavedSessionCommand)).Gesture);
            NativeMenuItem palette = items.Single(item => ReferenceEquals(item.Command, model.OpenCommandPaletteCommand));
            Assert.Equal(AppShortcuts.CommandPalette(model.CurrentSettings.CommandPaletteShortcut), palette.Gesture);
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
    public Task NewTabs_SelectReorderClose_WithoutCreatingTerminals() => HeadlessAvalonia.RunAsync(() =>
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
            Assert.Empty(model.Tabs);
            Assert.True(model.HasTabs);
            Assert.Same(second, model.ActiveWorkspaceTab);
            Assert.False(first.IsSelected);
            Assert.True(second.IsSelected);
            Assert.Single(window.GetVisualDescendants().OfType<NewTabView>(), view => view.IsVisible);
            HeadlessAvalonia.Pump();
            Assert.Equal("ConnectSavedSessionButton", ((Control)window.FocusManager!.GetFocusedElement()!).Name);
            model.SelectWorkspaceTabCommand.Execute(first);
            model.MoveWorkspaceTab(0, 1);
            Assert.Same(first, model.ActiveWorkspaceTab);
            Assert.Same(first, model.WorkspaceTabs[1]);
            model.CloseWorkspaceTabCommand.ExecuteAsync(first).GetAwaiter().GetResult();
            Assert.Same(second, model.ActiveWorkspaceTab);
            model.CloseWorkspaceTabCommand.ExecuteAsync(second).GetAwaiter().GetResult();
            Assert.Null(model.ActiveWorkspaceTab);
            Assert.False(model.HasTabs);
        }
        finally { window.Close(); model.DisposeAsync().GetAwaiter().GetResult(); }
    });

    [Fact]
    public Task TerminalSelectionAndClose_KeepStartPagesAndTerminalOrderConsistent() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        MainViewModel model = CreateModel();
        try
        {
            model.NewTabCommand.Execute(null);
            NewTabViewModel starter = Assert.Single(model.NewTabs);
            IConnectionHost host = model;
            host.OpenTab(Config("first"));
            host.OpenTab(Config("second"));
            TerminalTabViewModel first = model.Tabs[0];
            TerminalTabViewModel second = model.Tabs[1];
            model.SelectWorkspaceTabCommand.Execute(starter);
            Assert.Null(model.SelectedTab);
            Assert.False(first.IsSelected);
            Assert.False(second.IsSelected);
            model.MoveWorkspaceTab(2, 0);
            Assert.Same(second, model.Tabs[0]);
            Assert.Same(first, model.Tabs[1]);
            model.SelectWorkspaceTabCommand.Execute(second);
            Assert.Same(second, model.SelectedTab);
            await model.CloseWorkspaceTabCommand.ExecuteAsync(second);
            Assert.Same(starter, model.ActiveWorkspaceTab);
            Assert.Null(model.SelectedTab);
            Assert.False(first.IsSelected);
            first.RequestCloseOthersCommand.Execute(null);
            Assert.Same(first, Assert.Single(model.WorkspaceTabs));
            Assert.Empty(model.NewTabs);
            Assert.Same(first, model.SelectedTab);
        }
        finally
        {
            foreach (TerminalTabViewModel terminal in model.Tabs.ToArray()) await model.CloseTabCommand.ExecuteAsync(terminal);
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
