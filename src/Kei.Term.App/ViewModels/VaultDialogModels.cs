namespace Kei.Term.App.ViewModels;

// Vault 首次初始化的用户选择
public enum VaultSetupChoice
{
    // 设置主密码（推荐）
    SetMasterPassword,
    // 不加密存储（明文警示）
    PlainMode,
    // 取消
    Cancel
}

// Vault 初始化对话框结果：choice=SetMasterPassword 时 MasterPassword 有效
public sealed record VaultSetupResult(VaultSetupChoice Choice, string? MasterPassword);

// 快捷验证只是一种解锁选择；对话框不持有或调用本机密钥后端。
public sealed record VaultUnlockPrompt(
    string? Error = null,
    bool CanQuickUnlock = false,
    string? QuickUnlockDisplayName = null,
    string? Notice = null);

public enum VaultUnlockKind
{
    Cancelled,
    Password,
    QuickUnlock
}

public sealed record VaultUnlockResponse(VaultUnlockKind Kind, string? Password = null);
