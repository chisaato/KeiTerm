using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kei.Term.Tests;

public class WelcomeActionsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task QuickConnect_OpensTerminalDirectly_OrLeavesWelcomeOnCancel(bool confirm) => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Button button = fixture.Window.FindControl<NewTabView>("WelcomeView")!.FindControl<Button>("QuickConnectButton")!;
        Assert.True(button.IsEffectivelyVisible);
        IAsyncRelayCommand command = Assert.IsAssignableFrom<IAsyncRelayCommand>(button.Command);
        Task operation = command.ExecuteAsync(null);
        HeadlessAvalonia.Pump();
        CommandPaletteView picker = Assert.IsType<CommandPaletteView>(fixture.Window.FindControl<ContentControl>("CommandPaletteHost")!.Content);
        CommandPaletteViewModel choices = Assert.IsType<CommandPaletteViewModel>(picker.DataContext);
        Assert.Equal(PaletteAction.NewConnection, choices.SelectedResult!.Action);
        Assert.Empty(fixture.Window.OwnedWindows);
        choices.ExecuteSelectedCommand.Execute(null);
        HeadlessAvalonia.Pump();
        QuickConnectWindow dialog = Assert.Single(fixture.Window.OwnedWindows.OfType<QuickConnectWindow>());
        QuickConnectViewModel edit = Assert.IsType<QuickConnectViewModel>(dialog.DataContext);
        edit.Host = "quick.example";
        edit.Username = "ops";
        edit.Password = "test-only";
        (confirm ? edit.ConnectCommand : edit.CancelCommand).Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        HeadlessAvalonia.Pump();
        AssertWorkspace(fixture, button, confirm, "ops@quick.example");
        Assert.Empty(await fixture.Tree.GetAllNodesAsync());
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public Task NewSession_SavesAndOpensTerminalDirectly_OrLeavesPageOnCancel(bool confirm, bool fromNewTab) => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        if (fromNewTab)
        {
            fixture.Model.NewTabCommand.Execute(null);
            HeadlessAvalonia.Pump();
        }
        NewTabView page = fixture.Window.GetVisualDescendants().OfType<NewTabView>().Single(view => view.IsEffectivelyVisible);
        Button button = page.FindControl<Button>("CreateSessionButton")!;
        Assert.True(button.IsEffectivelyVisible);
        IAsyncRelayCommand command = Assert.IsAssignableFrom<IAsyncRelayCommand>(button.Command);
        Task operation = command.ExecuteAsync(null);
        HeadlessAvalonia.Pump();
        SessionEditWindow dialog = Assert.Single(fixture.Window.OwnedWindows.OfType<SessionEditWindow>());
        SessionEditViewModel edit = Assert.IsType<SessionEditViewModel>(dialog.DataContext);
        edit.Name = "New LAN";
        edit.Host = "lan.example";
        edit.Username = "ops";
        (confirm ? edit.SaveCommand : edit.CancelCommand).Execute(null);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        HeadlessAvalonia.Pump();
        if (fromNewTab && !confirm)
        {
            Assert.Single(fixture.Model.NewTabs);
            Assert.Empty(fixture.Model.Tabs);
            Assert.True(button.IsEffectivelyVisible);
        }
        else AssertWorkspace(fixture, button, confirm, "New LAN");
        IReadOnlyList<TreeNodeBase> saved = await fixture.Tree.GetAllNodesAsync();
        if (confirm)
        {
            SessionNode session = Assert.IsType<SessionNode>(Assert.Single(saved));
            Assert.Equal("lan.example", session.Host);
            Assert.Equal(session.Id, Assert.Single(fixture.Model.Tabs).Config!.SessionId);
        }
        else Assert.Empty(saved);
    });

    [Fact]
    public Task QuickConnect_SelectsSavedSessionBelowNewConnection_AndReplacesStartPage() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SessionNode saved = new() { Name = "LAN server", Host = "lan.example", Username = "ops" };
        await fixture.Tree.SaveNodeAsync(saved);
        await fixture.Model.ReloadTreeAsync();
        fixture.Model.NewTabCommand.Execute(null);
        HeadlessAvalonia.Pump();
        NewTabView starter = fixture.Window.GetVisualDescendants().OfType<NewTabView>().Single(view => view.IsEffectivelyVisible);
        Button quick = starter.FindControl<Button>("QuickConnectButton")!;
        Task operation = ((IAsyncRelayCommand)quick.Command!).ExecuteAsync(null);
        HeadlessAvalonia.Pump();
        CommandPaletteView picker = Assert.IsType<CommandPaletteView>(fixture.Window.FindControl<ContentControl>("CommandPaletteHost")!.Content);
        CommandPaletteViewModel choices = Assert.IsType<CommandPaletteViewModel>(picker.DataContext);
        Assert.Equal(PaletteAction.NewConnection, choices.Results[0].Action);
        Assert.Equal(saved.Id, choices.Results[1].SessionId);
        fixture.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
        fixture.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        HeadlessAvalonia.Pump();
        Assert.Empty(fixture.Model.NewTabs);
        Assert.Equal(saved.Id, Assert.Single(fixture.Model.Tabs).Config!.SessionId);
        Assert.True(fixture.Window.GetVisualDescendants().OfType<TerminalConnectionView>().Single().IsEffectivelyVisible);
        Assert.DoesNotContain(fixture.Window.OwnedWindows, window => window is QuickConnectWindow);
    });

    [Fact]
    public Task EmptyWorkspaceAndNewTab_ShareActions() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        NewTabView welcome = fixture.Window.FindControl<NewTabView>("WelcomeView")!;
        fixture.Model.NewTabCommand.Execute(null);
        HeadlessAvalonia.Pump();
        NewTabView starter = fixture.Window.GetVisualDescendants().OfType<NewTabView>().Single(view => view.IsEffectivelyVisible);
        Assert.IsType<NewTabViewModel>(welcome.DataContext);
        Assert.IsType<NewTabViewModel>(starter.DataContext);
        Assert.False(welcome.IsEffectivelyVisible);
        Assert.True(starter.IsEffectivelyVisible);
        Assert.Equal("QuickConnectButton", ((Control)fixture.Window.FocusManager!.GetFocusedElement()!).Name);
    });

    [Fact]
    public Task CommandPalette_OffersOnlyActionsForTheActiveDocument() => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.DoesNotContain(fixture.Model.CreateCommandPalette().Results, item => item.Action == PaletteAction.ToggleComposeBar);
        fixture.Model.NewTabCommand.Execute(null);
        NewTabViewModel starter = Assert.Single(fixture.Model.NewTabs);
        Assert.Contains(fixture.Model.CreateCommandPalette().Results, item => item.Action == PaletteAction.CloseTab);
        Assert.DoesNotContain(fixture.Model.CreateCommandPalette().Results, item => item.Action == PaletteAction.FindTerminal);
        Task connect = fixture.Model.NewConnectionCommand.ExecuteAsync(null);
        HeadlessAvalonia.Pump();
        QuickConnectWindow dialog = Assert.Single(fixture.Window.OwnedWindows.OfType<QuickConnectWindow>());
        QuickConnectViewModel edit = Assert.IsType<QuickConnectViewModel>(dialog.DataContext);
        edit.Host = "palette.example";
        edit.Password = "test-only";
        edit.ConnectCommand.Execute(null);
        await connect.WaitAsync(TimeSpan.FromSeconds(5));
        HeadlessAvalonia.Pump();
        CommandPaletteViewModel terminalActions = fixture.Model.CreateCommandPalette();
        Assert.Contains(terminalActions.Results, item => item.Action == PaletteAction.ToggleComposeBar);
        Assert.Contains(terminalActions.Results, item => item.Action == PaletteAction.FindTerminal);
        Assert.DoesNotContain(terminalActions.Results, item => item.Action == PaletteAction.Disconnect);
        Assert.DoesNotContain(terminalActions.Results, item => item.Action == PaletteAction.ToggleFileManager);
        // 即便面板打开后目标变成了启动页，也不能执行旧结果中的终端操作。
        fixture.Model.NewTabCommand.Execute(null);
        bool composeVisible = fixture.Model.IsComposeBarVisible;
        await fixture.Model.ExecutePaletteItemAsync(terminalActions.Results.Single(item => item.Action == PaletteAction.ToggleComposeBar));
        Assert.Equal(composeVisible, fixture.Model.IsComposeBarVisible);
        Assert.DoesNotContain(fixture.Model.CreateCommandPalette().Results, item => item.Action == PaletteAction.ToggleComposeBar);
    });

    private static void AssertWorkspace(Fixture fixture, Button welcomeButton, bool opened, string title)
    {
        Assert.Equal(opened, fixture.Model.HasTabs);
        Assert.Empty(fixture.Model.NewTabs);
        if (opened)
        {
            TerminalTabViewModel terminal = Assert.Single(fixture.Model.Tabs);
            Assert.Equal(title, terminal.Config!.SessionName);
            Assert.Same(terminal, Assert.Single(fixture.Model.WorkspaceTabs));
            Assert.Same(terminal, fixture.Model.ActiveWorkspaceTab);
            // Dock 会移除启动页；已脱离窗口的控件自身可见标志不代表仍被显示。
            Assert.DoesNotContain(fixture.Window.GetVisualDescendants().OfType<Button>(),
                button => ReferenceEquals(button, welcomeButton) && button.IsEffectivelyVisible);
            Assert.Single(fixture.Window.GetVisualDescendants().OfType<TerminalConnectionView>(), view => view.IsEffectivelyVisible);
        }
        else
        {
            Assert.Empty(fixture.Model.WorkspaceTabs);
            Assert.True(welcomeButton.IsEffectivelyVisible);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Directory { get; init; }
        public required MainWindow Window { get; init; }
        public required MainViewModel Model { get; init; }
        public required SqliteTreeRepository Tree { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            UiDesignSystemService.Apply();
            string directory = Path.Combine(Path.GetTempPath(), "keiterm_welcome_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            SqliteConnectionFactory database = new($"Data Source={Path.Combine(directory, "test.db")}");
            await SchemaMigrator.MigrateAsync(database);
            SqliteTreeRepository tree = new(database);
            SqliteIdentityRepository identities = new(database);
            InternalVaultManager vault = new(database);
            FixedSettingsService settings = new(new AppSettings { PreferSystemAgent = false, ConfirmBeforeClose = false });
            MainViewModel model = new(tree, identities, vault, vault, settings, new OfflineSshFactory());
            MainWindow window = new() { DataContext = model };
            model.Interaction = new MainWindowInteractionService(window, model, new IdentityManagerViewModel(identities, vault, vault),
                new SettingsViewModel(settings), null, NullLogger.Instance);
            window.Show();
            HeadlessAvalonia.Pump();
            return new Fixture { Directory = directory, Window = window, Model = model, Tree = tree };
        }

        public async ValueTask DisposeAsync()
        {
            await Model.DisposeAsync();
            Window.Close();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    // 只替换外部 SSH 边界；视图、会话保存、连接编排与工作区标签均使用生产实现。
    private sealed class OfflineSshFactory : ISshSessionFactory
    {
        public Task<ISshSession> CreateSessionAsync(ResolvedSessionConfig config, IReadOnlyList<MaterializedAuthMethod> methods,
            SshConnectOptions? options = null, CancellationToken ct = default)
            => Task.FromException<ISshSession>(new IOException("Offline test connection"));

        public Task<IRemoteFileSystem> CreateFileSystemAsync(ResolvedSessionConfig config, IReadOnlyList<MaterializedAuthMethod> methods,
            ISshSession? activeSession = null, SshConnectOptions? options = null, CancellationToken ct = default)
            => Task.FromException<IRemoteFileSystem>(new IOException("Offline test connection"));
    }
}
