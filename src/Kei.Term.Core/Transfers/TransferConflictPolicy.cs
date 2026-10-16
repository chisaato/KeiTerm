namespace Kei.Term.Core.Transfers;

// 冲突只针对正式文件名。数值会写入 JSON，已发布的取值不能重排。
public enum TransferConflictPolicy
{
    Overwrite = 0,
    Skip = 1,
    OverwriteIfOlder = 2,
    AutoRename = 3,
    Ask = 4,
}

// 判定结果。询问策略在这里只表示需要询问，不包含用户最终选择。
public enum ConflictResolution
{
    Overwrite,
    Skip,
    Rename,
    NeedsPrompt,
}
