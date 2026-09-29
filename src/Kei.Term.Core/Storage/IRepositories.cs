namespace Kei.Term.Core.Storage;

using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Core.Vault;

public interface ITreeRepository
{
    Task<IReadOnlyList<TreeNodeBase>> GetAllNodesAsync(CancellationToken ct = default);
    Task<TreeNodeBase?> GetNodeByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveNodeAsync(TreeNodeBase node, CancellationToken ct = default);

    // 批量保存（批量修改会话属性用）；实现应保证全部成功或全部不生效
    async Task SaveNodesAsync(IReadOnlyCollection<TreeNodeBase> nodes, CancellationToken ct = default)
    {
        foreach (TreeNodeBase node in nodes)
        {
            await SaveNodeAsync(node, ct);
        }
    }
    Task DeleteNodeAsync(Guid id, CancellationToken ct = default);
    Task MoveNodeAsync(Guid nodeId, Guid? newParentId, int sortOrder, CancellationToken ct = default);
    Task UpdateFolderExpandedAsync(Guid folderId, bool isExpanded, CancellationToken ct = default);
}

public interface IIdentityRepository
{
    Task<IReadOnlyList<Identity>> GetAllAsync(CancellationToken ct = default);
    Task<Identity?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(Identity identity, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IExternalEditorRepository
{
    Task<IReadOnlyList<ExternalEditor>> GetAllEditorsAsync(CancellationToken ct = default);
    Task<ExternalEditor?> GetEditorByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveEditorAsync(ExternalEditor editor, CancellationToken ct = default);
    Task DeleteEditorAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<FileAssociationRule>> GetAllAssociationsAsync(CancellationToken ct = default);
    Task SaveAssociationAsync(FileAssociationRule association, CancellationToken ct = default);
    Task DeleteAssociationAsync(Guid id, CancellationToken ct = default);
}

public interface IKnownHostRepository
{
    Task<IReadOnlyList<KnownHostEntry>> GetAllAsync(CancellationToken ct = default);

    // 候选集：该端点的精确条目 + 全部模式条目；最终匹配由 KnownHostMatcher 在内存中判定
    Task<IReadOnlyList<KnownHostEntry>> GetCandidatesAsync(string host, int port, CancellationToken ct = default);

    // 按 (host, port, public_key) 幂等写入：已存在则更新状态/来源/备注，保留原 Id 与创建时间
    Task SaveAsync(KnownHostEntry entry, CancellationToken ct = default);

    Task TouchAsync(Guid id, DateTime seenAtUtc, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
