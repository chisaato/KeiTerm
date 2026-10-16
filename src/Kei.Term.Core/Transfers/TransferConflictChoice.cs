namespace Kei.Term.Core.Transfers;

// 冲突弹窗的结果。Policy 不应再是 Ask。取消弹窗由调用方返回 null，不算失败。
public readonly record struct TransferConflictChoice(TransferConflictPolicy Policy, bool ApplyToRemaining);
