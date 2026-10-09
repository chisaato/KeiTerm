using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;
using Xunit;

namespace Kei.Term.Tests;

public class SessionDuplicateTests
{
    [Fact]
    public async Task Duplicate_PreservesConfigurationAndForwards_AndChangesStayIndependent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        MainViewModel model = fixture.Model;
        await model.ReloadTreeAsync();
        model.SelectedTreeNode = fixture.Source;
        model.FilterText = "LAN";
        await model.DuplicateSelectedSessionCommand.ExecuteAsync(null);
        SessionNode copy = Assert.IsType<SessionNode>(model.SelectedTreeNode);
        Assert.NotEqual(fixture.Source.Id, copy.Id);
        Assert.Equal(fixture.Source.ParentId, copy.ParentId);
        Assert.Equal(string.Format(Strings.Get("Common.CopySuffix"), fixture.Source.Name), copy.Name);
        Assert.Equal("", model.FilterText);
        Assert.Equal(fixture.Source.IdentityId, copy.IdentityId);
        Assert.Equal(fixture.Source.JumpHostSessionId, copy.JumpHostSessionId);
        Assert.Equal(fixture.Source.Host, copy.Host);
        Assert.Equal(fixture.Source.Port, copy.Port);
        Assert.Equal(fixture.Source.Username, copy.Username);
        Assert.Equal(fixture.Source.TerminalProfileId, copy.TerminalProfileId);
        Assert.Equal(fixture.Source.StartupScript, copy.StartupScript);
        Assert.Equal(fixture.Source.Overrides.ConnectTimeoutSeconds, copy.Overrides.ConnectTimeoutSeconds);
        Assert.Equal(fixture.Source.EnvironmentVariables, copy.EnvironmentVariables);
        PortForward originalForward = Assert.Single(await fixture.Forwards.GetBySessionAsync(fixture.Source.Id));
        PortForward copiedForward = Assert.Single(await fixture.Forwards.GetBySessionAsync(copy.Id));
        Assert.NotEqual(originalForward.Id, copiedForward.Id);
        Assert.Equal(originalForward.DestinationHost, copiedForward.DestinationHost);
        Assert.Equal(originalForward.ListenPort, copiedForward.ListenPort);
        Assert.Equal(originalForward.Mode, copiedForward.Mode);

        copy.Host = "192.168.1.99";
        copy.EnvironmentVariables["LANG"] = "en_US.UTF-8";
        copy.Overrides.ConnectTimeoutSeconds = 90;
        await fixture.Tree.SaveNodeAsync(copy);
        copiedForward.DestinationHost = "192.168.1.99";
        await fixture.Forwards.SaveAsync(copiedForward);
        SessionNode persistedOriginal = Assert.IsType<SessionNode>(await fixture.Tree.GetNodeByIdAsync(fixture.Source.Id));
        Assert.Equal("192.168.1.42", persistedOriginal.Host);
        Assert.Equal("zh_CN.UTF-8", persistedOriginal.EnvironmentVariables["LANG"]);
        Assert.Equal(45, persistedOriginal.Overrides.ConnectTimeoutSeconds);
        Assert.Equal(originalForward.DestinationHost, Assert.Single(await fixture.Forwards.GetBySessionAsync(fixture.Source.Id)).DestinationHost);

        model.SelectedTreeNode = fixture.Source;
        await model.DuplicateSelectedSessionCommand.ExecuteAsync(null);
        SessionNode secondCopy = Assert.IsType<SessionNode>(model.SelectedTreeNode);
        Assert.NotEqual(copy.Name, secondCopy.Name);
    }

    [Fact]
    public async Task FailedForwardCopy_RemovesPartialSession_AndFolderCannotBeDuplicated()
    {
        await using Fixture fixture = await Fixture.CreateAsync(failForwardCopy: true);
        await fixture.Model.ReloadTreeAsync();
        fixture.Model.SelectedTreeNode = fixture.Source;
        await fixture.Model.DuplicateSelectedSessionCommand.ExecuteAsync(null);
        Assert.Single((await fixture.Tree.GetAllNodesAsync()).OfType<SessionNode>());
        Assert.Single(await fixture.Forwards.GetBySessionAsync(fixture.Source.Id));
        fixture.Model.SelectedTreeNode = new FolderNode();
        Assert.False(fixture.Model.DuplicateSelectedSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task SavedSessionSearch_UsesAllSavedNodes_EvenWhenSidebarFilterHidesThem()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Model.ReloadTreeAsync();
        fixture.Model.FilterText = "no match";
        CommandPaletteViewModel palette = fixture.Model.CreateCommandPalette(savedSessionsOnly: true);
        palette.Query = "局域网 192.168.1.42";
        CommandPaletteItem item = Assert.Single(palette.Results);
        Assert.Equal(fixture.Source.Id, item.SessionId);
        Assert.Equal(PaletteAction.ConnectSession, item.Action);
        palette.ExecuteSelectedCommand.Execute(null);
        Assert.Same(item, palette.Result);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Directory { get; init; }
        public required SqliteTreeRepository Tree { get; init; }
        public required SqlitePortForwardRepository Forwards { get; init; }
        public required SessionNode Source { get; init; }
        public required MainViewModel Model { get; init; }

        public static async Task<Fixture> CreateAsync(bool failForwardCopy = false)
        {
            string directory = Path.Combine(Path.GetTempPath(), "keiterm_duplicate_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            SqliteConnectionFactory database = new($"Data Source={Path.Combine(directory, "test.db")}");
            await SchemaMigrator.MigrateAsync(database);
            SqliteTreeRepository tree = new(database);
            SqliteIdentityRepository identities = new(database);
            Identity identity = new() { Name = "LAN auth", Username = "ops", Methods = [new AgentMethod()] };
            await identities.SaveAsync(identity);
            FolderNode folder = new() { Name = "局域网" };
            await tree.SaveNodeAsync(folder);
            SessionNode session = new()
            {
                Name = "LAN server", ParentId = folder.Id, Host = "192.168.1.42", Port = 2200,
                Username = "ops", IdentityId = identity.Id, TerminalProfileId = "custom-terminal",
                StartupScript = "cd /srv", EnvironmentVariables = new() { ["LANG"] = "zh_CN.UTF-8" },
                Overrides = new() { FollowRemoteTitle = false, ConnectTimeoutSeconds = 45 }
            };
            await tree.SaveNodeAsync(session);
            SqlitePortForwardRepository forwards = new(database);
            await forwards.SaveAsync(new PortForward { SessionId = session.Id, ListenPort = 15432, DestinationHost = "192.168.1.42", DestinationPort = 5432 });
            InternalVaultManager vault = VaultTestDb.CreateVault(database);
            MainViewModel model = new(tree, identities, vault, vault, new FixedSettingsService(), new SshSessionFactory(),
                uiDispatch: action => action(), portForwards: failForwardCopy ? new FailingForwards(forwards) : forwards);
            return new Fixture { Directory = directory, Tree = tree, Forwards = forwards, Source = session, Model = model };
        }

        public async ValueTask DisposeAsync()
        {
            await Model.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class FailingForwards(IPortForwardRepository inner) : IPortForwardRepository
    {
        public Task<IReadOnlyList<PortForward>> GetBySessionAsync(Guid id, CancellationToken ct = default) => inner.GetBySessionAsync(id, ct);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
        public Task SaveAsync(PortForward forward, CancellationToken ct = default) => throw new IOException("cannot save copied forward");
    }
}
