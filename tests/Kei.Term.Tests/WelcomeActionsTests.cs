using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
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
        Button button = fixture.Window.FindControl<Button>("WelcomeQuickConnectButton")!;
        Assert.True(button.IsEffectivelyVisible);
        IAsyncRelayCommand command = Assert.IsAssignableFrom<IAsyncRelayCommand>(button.Command);
        Task operation = command.ExecuteAsync(null);
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
    [InlineData(true)]
    [InlineData(false)]
    public Task NewSession_SavesAndOpensTerminalDirectly_OrLeavesWelcomeOnCancel(bool confirm) => HeadlessAvalonia.RunAsync(async () =>
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Button button = fixture.Window.FindControl<Button>("WelcomeNewSessionButton")!;
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
        AssertWorkspace(fixture, button, confirm, "New LAN");
        IReadOnlyList<TreeNodeBase> saved = await fixture.Tree.GetAllNodesAsync();
        if (confirm)
        {
            SessionNode session = Assert.IsType<SessionNode>(Assert.Single(saved));
            Assert.Equal("lan.example", session.Host);
            Assert.Equal(session.Id, Assert.Single(fixture.Model.Tabs).Config!.SessionId);
        }
        else Assert.Empty(saved);
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
            Assert.False(welcomeButton.IsEffectivelyVisible);
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
