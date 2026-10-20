using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Logging;
using Kei.Term.App.Models;
using Kei.Term.App.ViewModels.BatchEdit;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.ViewModels;

// 批量修改会话入口：按树上当前选中项预勾选（文件夹 = 其下全部会话）
public partial class MainViewModel
{
    [RelayCommand]
    private Task OpenBatchEditAsync() => Safe.RunAsync(_logger, "批量修改会话", async () =>
    {
        IReadOnlyList<Identity> identities = await _identityRepo.GetAllAsync();
        var batchVm = new BatchSessionEditViewModel(
            _allNodesCache,
            PreselectedSessionIds(SelectedTreeNode),
            new BatchEditContext(identities, _settingsService.Current),
            nodes => _treeRepo.SaveNodesAsync(nodes));

        await Interaction.OpenBatchEditAsync(batchVm);

        // 仅在实际发生变更时才弹出成功提示；取消或变更数为 0 时静默重载，避免虚假成功
        if (batchVm.ApplyAttempted && batchVm.ChangedCount > 0)
        {
            await ReloadTreeAsync();
            await Interaction.NotifyAsync(
                Strings.Get("BatchEdit.Title"),
                Strings.Format("BatchEdit.Done", batchVm.ChangedCount),
                NotificationLevel.Success);
        }
        else if (batchVm.ApplyAttempted)
        {
            await ReloadTreeAsync();
        }
    });

    private IEnumerable<Guid> PreselectedSessionIds(TreeNodeBase? selected)
    {
        switch (selected)
        {
            case SessionNode session:
                return [session.Id];
            case FolderNode folder:
                return TreeTraversal.DescendantSessionIds(folder.Id, _allNodesCache);
            default:
                return [];
        }
    }
}
