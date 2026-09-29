using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Security;
using Kei.Term.Infrastructure.Storage;
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
    public async Task Delete_RemovesSelectedEntry()
    {
        var (repo, vm) = await CreateAsync();
        await vm.ImportCommand.ExecuteAsync(null);

        Assert.False(new KnownHostsManagerViewModel(repo).DeleteCommand.CanExecute(null));
        vm.SelectedEntry = vm.Entries.First(r => r.HostDisplay == "web.example");
        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Equal("db.example", Assert.Single(await repo.GetAllAsync()).Host);
    }

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
