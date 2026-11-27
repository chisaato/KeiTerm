using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Security;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

public class KnownHostsManagerViewModelTests : IDisposable
{
    private const string KeyA = "AAAAC3NzaC1lZDI1NTE5AAAAINNOCjYfYNvN56zScDjq/dDohnNs4wdauZTYtC54QykV";
    private const string KeyB = "AAAAC3NzaC1lZDI1NTE5AAAAIN/OYsUEL5deLJb9X+lVVMyeUpEed4y/0VWHOn369umv";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"keiterm_khvm_{Guid.NewGuid():N}");

    public KnownHostsManagerViewModelTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task<(SqliteKnownHostRepository Repo, KnownHostsManagerViewModel Vm)> CreateAsync()
    {
        var repo = new SqliteKnownHostRepository($"Data Source={Path.Combine(_dir, "k.db")}");
        await repo.InitializeAsync();
        string importPath = Path.Combine(_dir, "known_hosts");
        await File.WriteAllTextAsync(importPath, $"""
            web.example ssh-ed25519 {KeyA} web
            [db.example]:2222 ssh-ed25519 {KeyB}
            """);
        var vm = new KnownHostsManagerViewModel(repo) { ImportPathProvider = () => importPath };
        return (repo, vm);
    }

    [Fact]
    public async Task Import_ThenFilter_ShowsMatchingRowsOnly()
    {
        var (_, vm) = await CreateAsync();

        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Entries.Count);

        vm.FilterText = "2222";
        var row = Assert.Single(vm.Entries);
        Assert.Equal("[db.example]:2222", row.HostDisplay);

        // 备注同样参与筛选
        vm.FilterText = "WEB";
        Assert.Equal("web.example", Assert.Single(vm.Entries).HostDisplay);
    }

    [Fact]
    public async Task ToggleRevoke_PersistsAndRestores()
    {
        var (repo, vm) = await CreateAsync();
        await vm.ImportCommand.ExecuteAsync(null);

        vm.SelectedEntry = vm.Entries.First(r => r.HostDisplay == "web.example");
        await vm.ToggleRevokeCommand.ExecuteAsync(null);
        Assert.Equal(KnownHostStatus.Revoked, (await repo.GetAllAsync()).Single(e => e.Host == "web.example").Status);

        vm.SelectedEntry = vm.Entries.First(r => r.HostDisplay == "web.example");
        await vm.ToggleRevokeCommand.ExecuteAsync(null);
        Assert.Equal(KnownHostStatus.Trusted, (await repo.GetAllAsync()).Single(e => e.Host == "web.example").Status);
    }

    [Fact]
    public async Task Delete_RequiresConfirmationAndRemovesOnlyTheConfirmedHost()
    {
        var (repo, vm) = await CreateAsync();
        await vm.ImportCommand.ExecuteAsync(null);

        Assert.False(new KnownHostsManagerViewModel(repo).DeleteCommand.CanExecute(null));
        var target = vm.Entries.First(r => r.HostDisplay == "web.example");
        vm.SelectedEntry = target;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(2, (await repo.GetAllAsync()).Count);

        var interaction = new ScriptedInteraction();
        vm.Interaction = interaction;
        interaction.DeleteAnswers.Enqueue(false);
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(2, (await repo.GetAllAsync()).Count);

        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        interaction.ConfirmDeleteHandler = _ => answer.Task;
        Task delete = vm.DeleteCommand.ExecuteAsync(null);
        vm.SelectedEntry = vm.Entries.Single(r => r.Entry.Id != target.Entry.Id);
        answer.SetResult(true);
        await delete;
        Assert.Equal(new[] { "web.example", "web.example" }, interaction.DeletePrompts);
        Assert.Equal("db.example", Assert.Single(await repo.GetAllAsync()).Host);
    }

    [Fact]
    public Task DesktopDeletion_ShowsConfirmationOwnedByTheManager() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (repo, vm) = await CreateAsync();
        await vm.ImportCommand.ExecuteAsync(null);
        string database = $"Data Source={Path.Combine(_dir, "k.db")}";
        InternalVaultManager vault = new(database);
        SqliteIdentityRepository identities = new(database);
        FixedSettingsService settings = new();
        await using MainViewModel main = new(new SqliteTreeRepository(database), identities, vault, vault, settings, new SshSessionFactory());
        UiDesignSystemService.Apply();
        MainWindow owner = new() { DataContext = main };
        owner.WireDialogs(main, new IdentityManagerViewModel(identities, vault, vault), new SettingsViewModel(settings), knownHostsVm: vm);
        var manager = new KnownHostsManagerWindow(vm);
        try
        {
            owner.Show();
            Task closed = manager.ShowDialog(owner);
            vm.SelectedEntry = vm.Entries.First(row => row.HostDisplay == "web.example");
            Task deletion = vm.DeleteCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();
            Window confirmation = Assert.Single(manager.OwnedWindows);
            Assert.True(confirmation.IsVisible);
            confirmation.Close(false);
            await deletion;
            Assert.Equal(2, (await repo.GetAllAsync()).Count);
            manager.Close();
            await closed;
        }
        finally { owner.Close(); }
    });

    [Fact]
    public async Task Export_WritesFileThatReimportsIdentically()
    {
        var (repo, vm) = await CreateAsync();
        await vm.ImportCommand.ExecuteAsync(null);
        string exportPath = Path.Combine(_dir, "exported");
        vm.PickExportPathAsync = () => Task.FromResult<string?>(exportPath);

        await vm.ExportCommand.ExecuteAsync(null);

        var reparsed = OpenSshKnownHostsParser.Parse(await File.ReadAllTextAsync(exportPath));
        Assert.Equal(
            (await repo.GetAllAsync()).Select(e => (e.Host, e.Port, e.PublicKeyBase64)).OrderBy(x => x.Host),
            reparsed.Entries.Select(e => (e.Host, e.Port, e.PublicKeyBase64)).OrderBy(x => x.Host));
    }
}
