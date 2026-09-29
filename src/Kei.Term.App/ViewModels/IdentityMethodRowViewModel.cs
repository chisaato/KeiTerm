using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;
using Kei.Term.Ssh.Services;

namespace Kei.Term.App.ViewModels;

// 身份编辑器中的单个认证方法行：包装 Core 的 AuthMethodEntry 并暴露行内可编辑字段
public partial class IdentityMethodRowViewModel : ViewModelBase
{
    // 底层领域对象（保存时按行序整体回写）
    public AuthMethodEntry Model { get; }

    // 方法类型显示名（行标题）
    public string KindName { get; }

    // 每种方法的单行说明文字
    public string Hint { get; }

    // 文件私钥专有字段显隐
    public bool IsFilePrivateKey { get; }

    // Agent 方法与 Vault 私钥专有字段显隐
    public bool IsAgent { get; }
    public bool IsVaultPrivateKey { get; }

    [ObservableProperty]
    private string _keyFilePath = string.Empty;

    // 口令三态下标：0=每次询问 1=本次运行记住 2=永久保存
    [ObservableProperty]
    private int _passphraseModeIndex;

    // Vault 私钥状态回显：未导入 / 已导入 · N 字节 / 待保存 … / 待移除
    [ObservableProperty]
    private string _vaultKeyStatus = Strings.Get("IdentityEdit.VaultKey.NotImported");

    // Vault 私钥指纹回显（无指纹时留空）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVaultKeyFingerprint))]
    private string _vaultKeyFingerprint = string.Empty;

    public bool HasVaultKeyFingerprint => !string.IsNullOrEmpty(VaultKeyFingerprint);

    // 文件私钥指纹回显（读取文件计算，失败留空不阻塞）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileKeyStatus))]
    private string _fileKeyStatus = string.Empty;

    public bool HasFileKeyStatus => !string.IsNullOrEmpty(FileKeyStatus);

    // 由窗口注入的文件选择器（StorageProvider FilePicker）
    public Func<Task<string?>>? FilePicker { get; set; }

    // Vault 私钥导入时的口令输入（一次性）；由编辑器下发
    public Func<string, Task<PassphrasePromptResult?>>? VaultKeyPassphrasePrompt { get; set; }

    // Vault 私钥已存材料信息读取（字节数 + 指纹）；由编辑器下发
    public Func<Guid, Guid, Task<VaultKeyInfo?>>? VaultKeyInfoLoader { get; set; }

    // 暂存的导入内容（「应用」前不落库）
    public string? StagedPrivateKeyContent { get; private set; }
    public string? StagedPassphrase { get; private set; }

    // 已有 Vault 材料时的待删除标记
    public bool IsRemovalRequested { get; private set; }

    private int? _persistedSize;
    private string? _fingerprint;
    private string? _stagedFingerprint;

    public IdentityMethodRowViewModel(AuthMethodEntry model)
    {
        Model = model;
        switch (model)
        {
            case VaultPasswordMethod:
                KindName = Strings.Get("IdentityEdit.Method.VaultPassword");
                Hint = Strings.Get("IdentityEdit.Method.VaultPasswordHint");
                break;
            case VaultPrivateKeyMethod:
                KindName = Strings.Get("IdentityEdit.Method.VaultKey");
                Hint = Strings.Get("IdentityEdit.Method.VaultKeyHint");
                IsVaultPrivateKey = true;
                break;
            case FilePrivateKeyMethod file:
                KindName = Strings.Get("IdentityEdit.Method.FileKey");
                Hint = Strings.Get("IdentityEdit.Method.FileKeyHint");
                IsFilePrivateKey = true;
                KeyFilePath = file.KeyFilePath ?? string.Empty;
                PassphraseModeIndex = (int)file.PassphraseMode;
                break;
            case AgentMethod:
                KindName = Strings.Get("IdentityEdit.Method.Agent");
                Hint = Strings.Get("IdentityEdit.Method.AgentHint");
                IsAgent = true;
                break;
            case InteractiveMethod:
                KindName = Strings.Get("IdentityEdit.Method.Interactive");
                Hint = Strings.Get("IdentityEdit.Method.InteractiveHint");
                break;
            default:
                KindName = model.GetType().Name;
                Hint = string.Empty;
                break;
        }
    }

    // 浏览私钥文件：选择结果写回 KeyFilePath，随后尝试计算指纹
    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        if (FilePicker == null)
        {
            return;
        }

        var picked = await FilePicker();
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        KeyFilePath = picked;
        await RefreshFileKeyFingerprintAsync();
    }

    // 读取私钥文件计算指纹；文件不存在/加密/不支持时留空（不阻塞编辑）
    public async Task RefreshFileKeyFingerprintAsync()
    {
        if (!IsFilePrivateKey || string.IsNullOrWhiteSpace(KeyFilePath))
        {
            FileKeyStatus = string.Empty;
            return;
        }

        var read = await PrivateKeyImport.ReadAsync(KeyFilePath.Trim());
        var fingerprint = read == null ? null : SshKeyFingerprint.Compute(read.Content);
        FileKeyStatus = string.IsNullOrEmpty(fingerprint) ? string.Empty : string.Format(Strings.Get("IdentityEdit.FingerprintFormat"), fingerprint);
    }

    // 回显已存 Vault 私钥材料（编辑已存在身份时）；文件私钥行顺带计算指纹
    public async Task InitializeVaultKeyStatusAsync(Guid identityId)
    {
        if (IsVaultPrivateKey && VaultKeyInfoLoader != null)
        {
            var info = await VaultKeyInfoLoader(identityId, Model.Id);
            _persistedSize = info?.Size;
            _fingerprint = info?.Fingerprint;
            if (info != null)
            {
                var bytesText = string.Format(Strings.Get("IdentityEdit.BytesFormat"), info.Size);
                VaultKeyStatus = $"{Strings.Get("IdentityEdit.VaultKey.Imported")} · {bytesText}";
                VaultKeyFingerprint = info.Fingerprint ?? string.Empty;
            }
            else
            {
                VaultKeyStatus = Strings.Get("IdentityEdit.VaultKey.NotImported");
                VaultKeyFingerprint = string.Empty;
            }
        }

        if (IsFilePrivateKey && !string.IsNullOrWhiteSpace(KeyFilePath))
        {
            await RefreshFileKeyFingerprintAsync();
        }
    }

    // 导入私钥：选文件 → 读内容 → 需口令则弹一次性口令框 → 暂存于内存（「应用」时落库）
    [RelayCommand]
    private async Task ImportVaultKeyAsync()
    {
        if (FilePicker == null)
        {
            return;
        }

        var path = await FilePicker();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var read = await PrivateKeyImport.ReadAsync(path);
        if (read == null)
        {
            VaultKeyStatus = Strings.Get("IdentityEdit.VaultKey.ReadFailed");
            VaultKeyFingerprint = string.Empty;
            return;
        }

        string? passphrase = null;
        if (read.RequiresPassphrase)
        {
            if (VaultKeyPassphrasePrompt == null)
            {
                VaultKeyStatus = Strings.Get("IdentityEdit.VaultKey.PassphrasePromptUnavailable");
                VaultKeyFingerprint = string.Empty;
                return;
            }

            var prompt = await VaultKeyPassphrasePrompt(path);
            if (prompt == null)
            {
                // 用户取消：保持原状态
                return;
            }

            passphrase = prompt.Passphrase;
        }

        StagedPrivateKeyContent = read.Content;
        StagedPassphrase = passphrase;
        _stagedFingerprint = SshKeyFingerprint.Compute(read.Content, passphrase);
        IsRemovalRequested = false;
        var stagedBytesText = string.Format(Strings.Get("IdentityEdit.BytesFormat"), PrivateKeyImport.ByteCount(read.Content));
        VaultKeyStatus = $"{Strings.Get("IdentityEdit.VaultKey.PendingSave")} · {stagedBytesText}";
        VaultKeyFingerprint = _stagedFingerprint ?? string.Empty;
    }

    // 移除私钥：清空暂存；已有 Vault 材料则标记待删除
    [RelayCommand]
    private void RemoveVaultKey()
    {
        StagedPrivateKeyContent = null;
        StagedPassphrase = null;
        _stagedFingerprint = null;
        VaultKeyFingerprint = string.Empty;

        if (_persistedSize.HasValue)
        {
            IsRemovalRequested = true;
            VaultKeyStatus = Strings.Get("IdentityEdit.VaultKey.PendingRemove");
        }
        else
        {
            IsRemovalRequested = false;
            VaultKeyStatus = Strings.Get("IdentityEdit.VaultKey.NotImported");
        }
    }

    // 「应用」成功后调用：把暂存材料固化为已导入状态，并清空暂存标记
    public void MarkApplied()
    {
        if (!IsVaultPrivateKey)
        {
            return;
        }

        if (StagedPrivateKeyContent != null)
        {
            _persistedSize = PrivateKeyImport.ByteCount(StagedPrivateKeyContent);
            _fingerprint = _stagedFingerprint;
            var bytesText = string.Format(Strings.Get("IdentityEdit.BytesFormat"), _persistedSize.Value);
            VaultKeyStatus = $"{Strings.Get("IdentityEdit.VaultKey.Imported")} · {bytesText}";
            VaultKeyFingerprint = _fingerprint ?? string.Empty;
        }
        else if (IsRemovalRequested)
        {
            _persistedSize = null;
            _fingerprint = null;
            VaultKeyStatus = Strings.Get("IdentityEdit.VaultKey.NotImported");
            VaultKeyFingerprint = string.Empty;
        }

        StagedPrivateKeyContent = null;
        StagedPassphrase = null;
        _stagedFingerprint = null;
        IsRemovalRequested = false;
    }

    // 把行内编辑字段写回底层模型
    public void Apply()
    {
        if (Model is FilePrivateKeyMethod file)
        {
            file.KeyFilePath = string.IsNullOrWhiteSpace(KeyFilePath) ? null : KeyFilePath.Trim();
            file.PassphraseMode = (PassphrasePersistence)Math.Clamp(PassphraseModeIndex, 0, 2);
        }
    }
}

// 可选方法类型（添加方法下拉）
public sealed record AuthMethodTypeOption(string Key, string Label);

// 口令三态下拉选项
public sealed record PassphraseModeOption(int Index, string Label);
