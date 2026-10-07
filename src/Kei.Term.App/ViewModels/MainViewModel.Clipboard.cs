namespace Kei.Term.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Logging;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

public partial class MainViewModel
{
    // 剪贴板保存独立的完整配置，不引用被搜索过滤重建的显示节点。
    private ClipboardSnapshot? _cutSnapshot;
    private ClipboardSnapshot? _copySnapshot;

    private sealed record ClipboardSnapshot(TreeNodeBase Root, IReadOnlyList<PortForward> Forwards);

    // 保留现有复制行为：立即在原位置创建副本，同时可继续粘贴到其他目录。
    [RelayCommand(CanExecute = nameof(CanUseSelectedNode))]
    private Task CopyNodeAsync() => Safe.RunAsync(_logger, "复制节点", async () =>
    {
        if (SelectedTreeNode == null) return;

        ClipboardSnapshot? snapshot = await SnapshotSubtreeAsync(SelectedTreeNode.Id);
        if (snapshot == null) return;

        await CloneSubtreeIntoAsync(snapshot, snapshot.Root.ParentId);
        _copySnapshot = snapshot;
        _cutSnapshot = null;
        PasteNodeCommand.NotifyCanExecuteChanged();
    });

    // 保留立即移除的剪切语义；级联删除前保存所有子会话的独立转发记录。
    [RelayCommand(CanExecute = nameof(CanUseSelectedNode))]
    private Task CutNodeAsync() => Safe.RunAsync(_logger, "剪切节点", async () =>
    {
        if (SelectedTreeNode == null) return;

        ClipboardSnapshot? snapshot = await SnapshotSubtreeAsync(SelectedTreeNode.Id);
        if (snapshot == null) return;

        await _treeRepo.DeleteNodeAsync(snapshot.Root.Id);
        _cutSnapshot = snapshot;
        _copySnapshot = null;
        PasteNodeCommand.NotifyCanExecuteChanged();
        SelectedTreeNode = null;
        await ReloadTreeAsync();
    });

    [RelayCommand(CanExecute = nameof(CanPasteNode))]
    private Task PasteNodeAsync() => Safe.RunAsync(_logger, "粘贴节点", async () =>
    {
        Guid? targetId = SelectedTreeNode is FolderNode targetFolder ? targetFolder.Id : null;

        if (_cutSnapshot != null)
        {
            _cutSnapshot.Root.ParentId = targetId;
            await SaveSubtreeAsync(_cutSnapshot);
            ClearClipboard();
            await ReloadTreeAsync();
            return;
        }

        if (_copySnapshot != null)
        {
            if (!IsValidCloneTarget(_copySnapshot.Root, targetId))
            {
                StatusMessage = Strings.Get("Status.Paste.InvalidTarget");
                return;
            }

            await CloneSubtreeIntoAsync(_copySnapshot, targetId);
            ClearClipboard();
        }
    });

    private bool CanPasteNode() => _cutSnapshot != null || _copySnapshot != null;

    private void ClearClipboard()
    {
        _cutSnapshot = null;
        _copySnapshot = null;
        PasteNodeCommand.NotifyCanExecuteChanged();
    }

    private async Task<ClipboardSnapshot?> SnapshotSubtreeAsync(Guid rootId)
    {
        IReadOnlyList<TreeNodeBase> all = await _treeRepo.GetAllNodesAsync();
        var byId = all.ToDictionary(node => node.Id);
        if (!byId.TryGetValue(rootId, out TreeNodeBase? root)) return null;

        foreach (FolderNode folder in all.OfType<FolderNode>())
            folder.Children.Clear();
        foreach (TreeNodeBase node in all)
        {
            if (node.ParentId is Guid parentId && byId.GetValueOrDefault(parentId) is FolderNode folder)
                folder.Children.Add(node);
        }

        List<PortForward> forwards = [];
        foreach (SessionNode session in FlattenSubtree(root).OfType<SessionNode>())
            forwards.AddRange(await LoadPortForwardsAsync(session.Id));

        return new ClipboardSnapshot(root, forwards);
    }

    private async Task CloneSubtreeIntoAsync(ClipboardSnapshot source, Guid? targetParentId)
    {
        TreeNodeBase clone = TreeNodeCloner.DeepClone(source.Root);
        clone.ParentId = targetParentId;
        clone.Name = string.Format(Strings.Get("Common.CopySuffix"), source.Root.Name);

        // DeepClone 保持前序顺序；所有转发绑定新会话 Id，并生成独立的转发 Id。
        var sessionIds = FlattenSubtree(source.Root).Zip(FlattenSubtree(clone))
            .ToDictionary(pair => pair.First.Id, pair => pair.Second.Id);
        var forwards = source.Forwards.Select(forward => new PortForward
        {
            SessionId = sessionIds[forward.SessionId],
            Name = forward.Name,
            Mode = forward.Mode,
            BindAddress = forward.BindAddress,
            ListenPort = forward.ListenPort,
            DestinationHost = forward.DestinationHost,
            DestinationPort = forward.DestinationPort
        }).ToList();

        await SaveSubtreeAsync(new ClipboardSnapshot(clone, forwards));
        await ReloadTreeAsync();
    }

    private async Task SaveSubtreeAsync(ClipboardSnapshot snapshot)
    {
        // 节点先按父子顺序在一个事务中落库；转发失败则撤销已恢复的子树，快照保留供重试。
        await _treeRepo.SaveNodesAsync(FlattenSubtree(snapshot.Root).ToList());
        try
        {
            if (_portForwards != null)
                foreach (PortForward forward in snapshot.Forwards)
                    await _portForwards.SaveAsync(forward);
        }
        catch
        {
            await _treeRepo.DeleteNodeAsync(snapshot.Root.Id);
            throw;
        }
    }

    private static IEnumerable<TreeNodeBase> FlattenSubtree(TreeNodeBase root)
    {
        yield return root;
        if (root is FolderNode folder)
            foreach (TreeNodeBase child in folder.Children)
                foreach (TreeNodeBase descendant in FlattenSubtree(child))
                    yield return descendant;
    }

    private bool IsValidCloneTarget(TreeNodeBase source, Guid? targetId)
    {
        var byId = _allNodesCache.ToDictionary(node => node.Id);
        HashSet<Guid> visited = [];
        while (targetId is Guid id && visited.Add(id) && byId.TryGetValue(id, out TreeNodeBase? node))
        {
            if (node.Id == source.Id) return false;
            targetId = node.ParentId;
        }

        return true;
    }
}
