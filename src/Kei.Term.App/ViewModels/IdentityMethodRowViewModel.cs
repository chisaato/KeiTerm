using System;
using System.Collections.Generic;
using System.Linq;
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

    // Agent 方法与 Vault 方法专有字段显隐
    public bool IsAgent { get; }
    public bool IsVaultPrivateKey { get; }

    // Vault 密码 / 私钥方法：材料来自材料库、按条目引用
    public bool IsVaultMethod => Model is VaultPasswordMethod or VaultPrivateKeyMethod;

    [ObservableProperty]
    private string _keyFilePath = string.Empty;

    // 口令三态下标：0=每次询问 1=本次运行记住 2=永久保存
    [ObservableProperty]
    private int _passphraseModeIndex;

    // 文件私钥指纹回显（读取文件计算，失败留空不阻塞）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileKeyStatus))]
    private string _fileKeyStatus = string.Empty;

    public bool HasFileKeyStatus => !string.IsNullOrEmpty(FileKeyStatus);

    // 材料下拉数据源：由编辑器按方法类型过滤后下发
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSecret))]
    [NotifyPropertyChangedFor(nameof(SelectedSecretStatus))]
    private IReadOnlyList<VaultSecret> _availableSecrets = [];

    // 当前选中的材料条目 Id；回写底层 Model.SecretId
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSecret))]
    [NotifyPropertyChangedFor(nameof(SelectedSecretStatus))]
    private Guid? _selectedSecretId;

    // 下拉框绑定用：当前选中材料对象（映射自 SelectedSecretId）。
    // setter 忽略 null 回写：ItemsSource 变化/过滤时 ComboBox 可能回写 SelectedItem=null，
    // 不能借此静默清掉已绑定 SecretId；解除绑定须显式设置 SelectedSecretId = null。
    public VaultSecret? SelectedSecret
    {
        get => SelectedSecretId is Guid id ? AvailableSecrets.FirstOrDefault(item => item.Id == id) : null;
        set
        {
            if (value != null)
            {
                SelectedSecretId = value.Id;
            }
        }
    }

    // 选中材料状态回显：名称 + 指纹 + 引用数；未绑定显示占位
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSecretStatus))]
    private int? _selectedSecretReferenceCount;

    public string SelectedSecretStatus
    {
        get
        {
            if (SelectedSecretId is not Guid id)
            {
                return Strings.Get("Secret.Status.NotBound");
            }

            VaultSecret? secret = AvailableSecrets.FirstOrDefault(item => item.Id == id);
            if (secret == null)
            {
                return Strings.Get("Secret.Status.NotBound");
            }

            if (SelectedSecretReferenceCount is int count)
            {
                return string.IsNullOrEmpty(secret.Fingerprint)
                    ? string.Format(Strings.Get("Secret.Status.FormatNoFingerprintWithRefs"), secret.Name, count)
                    : string.Format(Strings.Get("Secret.Status.FormatWithRefs"), secret.Name, secret.Fingerprint, count);
            }

            return string.IsNullOrEmpty(secret.Fingerprint)
                ? secret.Name
                : string.Format(Strings.Get("Secret.Status.Format"), secret.Name, secret.Fingerprint);
        }
    }

    // 由窗口注入的文件选择器（StorageProvider FilePicker）
    public Func<Task<string?>>? FilePicker { get; set; }

    // 新建材料条目（布尔 = 私钥）；由编辑器下发
    public Func<bool, Task<VaultSecret?>>? CreateSecretAsync { get; set; }

    // 材料条目引用数读取；由编辑器下发
    public Func<Guid, Task<int>>? ReferenceCountLoader { get; set; }

    public IdentityMethodRowViewModel(AuthMethodEntry model)
    {
        Model = model;
        switch (model)
        {
            case VaultPasswordMethod password:
                KindName = Strings.Get("IdentityEdit.Method.VaultPassword");
                Hint = Strings.Get("IdentityEdit.Method.VaultPasswordHint");
                _selectedSecretId = password.SecretId;
                break;
            case VaultPrivateKeyMethod key:
                KindName = Strings.Get("IdentityEdit.Method.VaultKey");
                Hint = Strings.Get("IdentityEdit.Method.VaultKeyHint");
                IsVaultPrivateKey = true;
                _selectedSecretId = key.SecretId;
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

    // 新建材料：调用编辑器下发的创建流程，成功后加入下拉并选中
    [RelayCommand]
    private async Task CreateNewSecretAsync()
    {
        if (CreateSecretAsync == null)
        {
            return;
        }

        VaultSecret? created = await CreateSecretAsync(IsVaultPrivateKey);
        if (created == null)
        {
            return;
        }

        var list = AvailableSecrets.Where(item => item.Id != created.Id).ToList();
        list.Add(created);
        AvailableSecrets = list;
        SelectedSecretId = created.Id;
        if (ReferenceCountLoader != null)
        {
            try
            {
                SelectedSecretReferenceCount = await ReferenceCountLoader(created.Id);
            }
            catch
            {
                // 引用数读取失败保持现状
            }
        }
    }

    // 选中项变化即回写底层模型的 SecretId（Vault 方法）并刷新引用数
    partial void OnSelectedSecretIdChanged(Guid? value)
    {
        switch (Model)
        {
            case VaultPasswordMethod password:
                password.SecretId = value;
                break;
            case VaultPrivateKeyMethod key:
                key.SecretId = value;
                break;
        }

        // 切换选项先清空旧引用数，避免上一个条目的「引用 N」短暂挂在新条目名称旁
        SelectedSecretReferenceCount = null;

        if (value is Guid secretId && ReferenceCountLoader != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    int count = await ReferenceCountLoader(secretId);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        // 仅当仍选中同一条目时回填，防止切换后旧结果覆盖新条目
                        if (SelectedSecretId == secretId)
                        {
                            SelectedSecretReferenceCount = count;
                        }
                    });
                }
                catch
                {
                    // 引用数读取失败保持清空态
                }
            });
        }
    }

    // 把行内编辑字段写回底层模型
    public void Apply()
    {
        if (Model is FilePrivateKeyMethod file)
        {
            file.KeyFilePath = string.IsNullOrWhiteSpace(KeyFilePath) ? null : KeyFilePath.Trim();
            file.PassphraseMode = (PassphrasePersistence)Math.Clamp(PassphraseModeIndex, 0, 2);
        }
        else if (Model is VaultPasswordMethod password)
        {
            password.SecretId = SelectedSecretId;
        }
        else if (Model is VaultPrivateKeyMethod key)
        {
            key.SecretId = SelectedSecretId;
        }
    }
}

// 可选方法类型（添加方法下拉）
public sealed record AuthMethodTypeOption(string Key, string Label);

// 口令三态下拉选项
public sealed record PassphraseModeOption(int Index, string Label);
