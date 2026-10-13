using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.ViewModels.Settings;

// 「数据清单」：列出各表用途与条数。仅旧身份密文一项可清理。
// 交互服务经 Func 读取安全页当前的 Interaction，不新开弹窗属性。
public partial class DataInventorySettingsViewModel : ViewModelBase
{
    private readonly IDataInventory _inventory;
    private readonly Func<IInteractionService?> _interaction;

    // 本次 Load 得到的 legacy-secrets 可删行数，决定清理命令可用性
    private int _deletableCount;

    public DataInventorySettingsViewModel(IDataInventory inventory, Func<IInteractionService?> interaction)
    {
        _inventory = inventory;
        _interaction = interaction;
    }

    public ObservableCollection<DataInventoryRowViewModel> Items { get; } = [];

    public async Task LoadAsync()
    {
        IReadOnlyList<DataInventoryItem> items = await _inventory.LoadAsync();
        Items.Clear();
        foreach (DataInventoryItem item in items)
        {
            Items.Add(new DataInventoryRowViewModel(item));
        }

        _deletableCount = 0;
        foreach (DataInventoryItem item in items)
        {
            if (item.Id == DataInventoryRowViewModel.LegacySecretsId)
            {
                _deletableCount = item.DeletableCount;
            }
        }

        PurgeLegacyCommand.NotifyCanExecuteChanged();
    }

    private bool CanPurge() => _deletableCount > 0;

    [RelayCommand(CanExecute = nameof(CanPurge))]
    private async Task PurgeLegacyAsync()
    {
        // 打开页面或上一次清理后重判：没有可删行直接停
        await LoadAsync();
        if (_deletableCount <= 0)
        {
            return;
        }

        IInteractionService? interaction = _interaction();
        if (interaction == null)
        {
            return;
        }

        // 用本次 Load 的可删行数作为确认文案参数
        int deletable = _deletableCount;
        bool confirmed = await interaction.ConfirmAsync(
            Strings.Get("Settings.Data.Legacy.ConfirmTitle"),
            Strings.Format("Settings.Data.Legacy.ConfirmMessage", deletable));
        if (!confirmed)
        {
            return;
        }

        await _inventory.PurgeLegacyIdentitySecretsAsync();
        // 无论删除结果如何都重新加载，条数按删除后的实际值刷新
        await LoadAsync();
    }
}

// 单条清单行：本地化标题、说明、条数，以及是否显示清理按钮。
public sealed class DataInventoryRowViewModel
{
    public const string LegacySecretsId = "legacy-secrets";

    public DataInventoryRowViewModel(DataInventoryItem item)
    {
        ShowPurge = item.Id == LegacySecretsId;
        Title = Strings.Get($"Settings.Data.{Segment(item.Id)}.Title");
        Description = ResolveDescription(item);
        CountText = Strings.Format("Settings.Data.Count", item.Count);
    }

    public string Title { get; }

    public string Description { get; }

    public string CountText { get; }

    public bool ShowPurge { get; }

    private static string ResolveDescription(DataInventoryItem item) => item.Id switch
    {
        LegacySecretsId => ResolveLegacyDescription(item),
        "proxies" => Strings.Format("Settings.Data.Proxies.Description", Extra(item, 0)),
        "editors" => Strings.Format("Settings.Data.Editors.Description", Extra(item, 0), Extra(item, 1)),
        _ => Strings.Get($"Settings.Data.{Segment(item.Id)}.Description"),
    };

    private static string ResolveLegacyDescription(DataInventoryItem item) => item.LegacyState switch
    {
        // {0} 可删行数，{1} 留下的行数（尚未迁走或解密失败）
        LegacySecretState.Partial => Strings.Format(
            "Settings.Data.Legacy.Partial",
            item.DeletableCount,
            item.Count - item.DeletableCount),
        LegacySecretState.Complete => Strings.Get("Settings.Data.Legacy.Complete"),
        _ => Strings.Get("Settings.Data.Legacy.NotStarted"),
    };

    private static int Extra(DataInventoryItem item, int index)
        => index < item.ExtraCounts.Count ? item.ExtraCounts[index] : 0;

    private static string Segment(string id) => id switch
    {
        "sessions" => "Sessions",
        "identities" => "Identities",
        "secrets" => "Secrets",
        LegacySecretsId => "Legacy",
        "proxies" => "Proxies",
        "known-hosts" => "KnownHosts",
        "editors" => "Editors",
        _ => id,
    };
}
