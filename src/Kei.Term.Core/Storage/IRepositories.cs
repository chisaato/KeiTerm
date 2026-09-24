namespace Kei.Term.Core.Storage;

using Kei.Term.Core.Models;
using Kei.Term.Core.Vault;

public interface ITreeRepository
{
    Task<IReadOnlyList<TreeNodeBase>> GetAllNodesAsync(CancellationToken ct = default);
    Task<TreeNodeBase?> GetNodeByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveNodeAsync(TreeNodeBase node, CancellationToken ct = default);
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
