using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Vault;
using Xunit;

namespace Kei.Term.Tests;

public class DataInventoryViewModelTests
{
    [Fact]
    public async Task Purge_WhenConfirmDeclined_DoesNotDelete()
    {
        FakeDataInventory inventory = new();
        inventory.DeletableCounts.Enqueue(3);
        inventory.DeletableCounts.Enqueue(3);
        ScriptedInteraction interaction = new() { ConfirmResult = false };
        DataInventorySettingsViewModel vm = new(inventory, () => interaction);

        await vm.LoadAsync();
        await vm.PurgeLegacyCommand.ExecuteAsync(null);

        Assert.Equal(0, inventory.PurgeCalls);
        Assert.Equal(1, interaction.ConfirmCalls);
    }

    [Fact]
    public async Task Purge_WhenInteractionMissing_DoesNotDelete()
    {
        FakeDataInventory inventory = new();
        inventory.DeletableCounts.Enqueue(3);
        inventory.DeletableCounts.Enqueue(3);
        DataInventorySettingsViewModel vm = new(inventory, () => null);

        await vm.LoadAsync();
        await vm.PurgeLegacyCommand.ExecuteAsync(null);

        Assert.Equal(0, inventory.PurgeCalls);
    }

    [Fact]
    public async Task Purge_WhenConfirmed_PurgesOnceAndReloads()
    {
        FakeDataInventory inventory = new();
        // 测试预加载 -> 可删 2；命令内加载 -> 仍可删 2；清理后重载 -> 可删 0
        inventory.DeletableCounts.Enqueue(2);
        inventory.DeletableCounts.Enqueue(2);
        inventory.DeletableCounts.Enqueue(0);
        inventory.PurgeReturn = 2;
        ScriptedInteraction interaction = new() { ConfirmResult = true };
        DataInventorySettingsViewModel vm = new(inventory, () => interaction);

        await vm.LoadAsync();
        Assert.True(vm.PurgeLegacyCommand.CanExecute(null));

        await vm.PurgeLegacyCommand.ExecuteAsync(null);

        Assert.Equal(1, inventory.PurgeCalls);
        Assert.Equal(1, interaction.ConfirmCalls);
        Assert.Contains("2", interaction.LastConfirmMessage);
        Assert.False(vm.PurgeLegacyCommand.CanExecute(null));
    }

    [Fact]
    public async Task SecurityCategory_LoadsInventory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_data_inventory_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        FakeDataInventory inventory = new();
        SettingsViewModel vm = new(new FixedSettingsService(), directory, dataInventory: inventory);
        try
        {
            vm.SelectedCategory = vm.Categories.Single(c => c.Page is SecuritySettingsPage);
            Assert.Equal(1, inventory.LoadCalls);

            vm.SelectedCategory = vm.Categories.Single(c => c.Page is GeneralSettingsPage);
            Assert.Equal(1, inventory.LoadCalls);
        }
        finally
        {
            vm.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    // 仅内存的假清单：按脚本返回 legacy-secrets 可删行数，并记录 Load/Purge 次数
    private sealed class FakeDataInventory : IDataInventory
    {
        public Queue<int> DeletableCounts { get; } = new();

        public int LoadCalls { get; private set; }

        public int PurgeCalls { get; private set; }

        public int PurgeReturn { get; set; }

        public Task<IReadOnlyList<DataInventoryItem>> LoadAsync(CancellationToken ct = default)
        {
            LoadCalls++;
            int deletable = DeletableCounts.Count > 0 ? DeletableCounts.Dequeue() : 0;
            IReadOnlyList<DataInventoryItem> items =
            [
                new DataInventoryItem(
                    "legacy-secrets",
                    deletable + 1,
                    [],
                    LegacySecretState.Partial,
                    LegacySecretCleanup.MigratedRowsOnly,
                    deletable),
            ];
            return Task.FromResult(items);
        }

        public Task<int> PurgeLegacyIdentitySecretsAsync(CancellationToken ct = default)
        {
            PurgeCalls++;
            return Task.FromResult(PurgeReturn);
        }
    }
}
