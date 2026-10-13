using System;
using System.Collections.Generic;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.ViewModels;

// 统一认证窗可选择的认证方式
public enum AuthPromptMethod
{
    Password,
    PublicKeyFile,
    PublicKeyVault,
    Interactive
}

// 公钥来源下拉中的「保管库密钥」项（条目 Id + 显示名）
public sealed record VaultKeyOption(Guid SecretId, string DisplayName);

// 统一认证窗完整模式的结果
public sealed record AuthPromptResult(
    AuthPromptMethod Method,
    string Username,
    string? Password,
    string? KeyFilePath,
    string? Passphrase,
    Guid? SecretId);

// 身份编辑结果：领域对象（材料由编辑器在应用时直接写入材料库，不再携带暂存导入）
public sealed record IdentityEditResult(Identity Identity);
