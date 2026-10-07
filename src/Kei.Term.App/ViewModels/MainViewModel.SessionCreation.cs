using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Logging;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.App.ViewModels;

// 空工作区的新建入口在保存后直接打开连接标签，无需先新建一个空标签。
public partial class MainViewModel
{
    [RelayCommand]
    private Task CreateSessionAsync() => Safe.RunAsync(_logger, "新建会话", async () =>
    {
        // 欢迎页按钮和 ⌘N 共用此命令，避免提示的快捷键与实际行为不同。
        bool connectAfterSave = WorkspaceTabs.Count == 0;
        SessionNode? session = await CreateSessionCoreAsync();
        if (connectAfterSave && session != null) await OpenSessionCommand.ExecuteAsync(session);
    });

    private async Task<SessionNode?> CreateSessionCoreAsync()
    {
        Guid? parentId = TreePlacement.ResolveCreationParent(SelectedTreeNode);
        var identities = await _identityRepo.GetAllAsync();
        SessionNode? result = await Interaction.EditSessionAsync(null, parentId, identities);
        if (result == null) return null;

        await _treeRepo.SaveNodeAsync(result);
        await PersistPendingForwardsAsync(result.Id);
        await ReloadTreeAsync();
        return result;
    }
}
