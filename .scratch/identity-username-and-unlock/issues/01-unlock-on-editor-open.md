# 打开身份编辑器即提示解锁

Type: task
Status: resolved
Blocked by: 无

## 现象

刚启动应用（加密模式、保管库锁定）就去新建或编辑身份，打开编辑器时不提示解锁，等到导入私钥或点「应用」的那一刻才弹主密码框，打断已填好的表单。

## 诊断

- `IdentityManagerWindow` 打开编辑器前只调用 `InitializeVaultKeyStatusAsync` / `InitializeVaultPasswordStatusAsync`。
- 前者仅在已有身份且含 Vault 私钥方法时才经 `VaultSessionService.GetVaultKeyInfoAsync` 间接触发解锁；后者在锁定时直接返回「未知」，按设计不解锁。
- 新建身份或已有身份只含密码方法时，解锁被推迟到 `SaveCoreAsync` / `PersistVaultKeysAsync`。

## Answer

已修复，代码随 `7e641a6` 提交：

- `IdentityEditViewModel.PrepareAsync()`：显示编辑器前先 `VaultSession.EnsureUnlockedAsync()`，再回显 Vault 私钥与密码状态。明文模式或已解锁时直通。
- 用户取消解锁：编辑器照常打开，非 Vault 字段可编辑；不再为私钥预览二次弹框，Vault 私钥行标为「保管库已锁定，无法确认」（`IdentityMethodRowViewModel.MarkVaultKeyStatusUnknown`，新文案 `IdentityEdit.VaultKey.Unknown`），避免误显示「未导入」诱导重复导入。之后应用涉及凭据时仍会再次要求解锁。
- `IdentityManagerWindow.axaml.cs`：改为调用 `PrepareAsync()`。

## 验证记录

- `IdentityEditPasswordTests.Prepare_LockedVault_PromptsOnOpen_SoApplyDoesNotInterrupt`：打开时弹一次，之后确认保存不再弹。
- `IdentityEditPasswordTests.Prepare_CancelledUnlock_PromptsOnce_AndMarksVaultKeyUnknown`：取消后只弹一次，状态为未知。
- 原有 `ExistingPassword_BlankPreservesSecret_WithoutLoadingItIntoEditor` 不受影响（它不经过 `PrepareAsync`）。

## Comments
