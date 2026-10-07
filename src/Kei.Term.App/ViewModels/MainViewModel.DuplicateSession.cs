using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Logging;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.App.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanConnectSelectedSession))]
    private Task DuplicateSelectedSessionAsync() => Safe.RunAsync(_logger, "复制会话副本", async () =>
    {
        if (SelectedTreeNode is not SessionNode source) return;
        SessionNode copy = (SessionNode)TreeNodeCloner.DeepClone(source);
        string baseName = string.Format(Strings.Get("Common.CopySuffix"), source.Name);
        copy.Name = baseName;
        int suffix = 2;
        while (_allNodesCache.Any(node => node.ParentId == source.ParentId && node.Name == copy.Name))
            copy.Name = $"{baseName} {suffix++}";
        copy.SortOrder = _allNodesCache.Where(node => node.ParentId == source.ParentId).Select(node => node.SortOrder).DefaultIfEmpty(0).Max() + 1;

        // 身份只复用原引用；独立的端口转发记录必须换 Id，避免编辑副本影响原会话。
        var forwards = await LoadPortForwardsAsync(source.Id);
        await _treeRepo.SaveNodeAsync(copy);
        try
        {
            if (_portForwards != null)
                foreach (PortForward forward in forwards)
                    await _portForwards.SaveAsync(new PortForward
                    {
                        SessionId = copy.Id, Name = forward.Name, Mode = forward.Mode,
                        BindAddress = forward.BindAddress, ListenPort = forward.ListenPort,
                        DestinationHost = forward.DestinationHost, DestinationPort = forward.DestinationPort
                    });
        }
        catch
        {
            // 外键级联清除已写入的转发，失败不留下半份配置。
            await _treeRepo.DeleteNodeAsync(copy.Id);
            throw;
        }
        await ReloadTreeAsync();
        // 清除过滤以展示并选中新生成的副本。
        FilterText = string.Empty;
        SelectedTreeNode = _allNodesCache.FirstOrDefault(node => node.Id == copy.Id);
        StatusMessage = string.Format(Strings.Get("Status.SessionDuplicated"), copy.Name);
    });
}
