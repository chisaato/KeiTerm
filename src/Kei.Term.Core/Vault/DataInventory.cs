namespace Kei.Term.Core.Vault;

// 旧身份整包的迁移状态。只有 legacy-secrets 一项非 null。
public enum LegacySecretState
{
    NotStarted,
    Partial,
    Complete,
}

// 旧身份密文的清理范围。只有 legacy-secrets 不是 None。
public enum LegacySecretCleanup
{
    None,
    MigratedRowsOnly,
    AllRows,
}

// 数据清单的一项：稳定 Id、主计数、附加计数、迁移状态与本次可删行数。
// ExtraCounts 顺序与说明里点出的附加表一致；其他项 LegacyState 为 null、Cleanup 为 None。
public sealed record DataInventoryItem(
    string Id,
    int Count,
    IReadOnlyList<int> ExtraCounts,
    LegacySecretState? LegacyState,
    LegacySecretCleanup Cleanup,
    int DeletableCount);

// 数据清单只读报告与旧身份密文清理入口。清理前仍须由调用方确认。
public interface IDataInventory
{
    Task<IReadOnlyList<DataInventoryItem>> LoadAsync(CancellationToken ct = default);

    Task<int> PurgeLegacyIdentitySecretsAsync(CancellationToken ct = default);
}
