using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Logging;
using Kei.Term.App.Services;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kei.Term.App.ViewModels;

// 材料库窗口：列出材料元数据（含未解锁）、导入私钥/新建密码/重命名/删除。
// 引用计数与删除保护均基于 SecretReferences 纯函数扫描身份 methods_json，不落库计数器。
public partial class VaultSecretLibraryViewModel : ViewModelBase
{
    private readonly VaultSessionService _session;
    private readonly IIdentityRepository _identities;
    private readonly IInteractionService _interaction;
    private readonly ILogger _logger;

    // 未经筛选的完整快照：无引用筛选在内存中切换，不重新查库
    private IReadOnlyList<VaultSecretListItemViewModel> _allItems = [];

    public VaultSecretLibraryViewModel(
        VaultSessionService session,
        IIdentityRepository identities,
        IInteractionService interaction,
        ILogger? logger = null)
    {
        _session = session;
        _identities = identities;
        _interaction = interaction;
        _logger = logger ?? NullLogger.Instance;
    }

    [ObservableProperty]
    private ObservableCollection<VaultSecretListItemViewModel> _items = [];

    // 「仅显示无引用」筛选开关：变更即时重算展示集合
    [ObservableProperty]
    private bool _unreferencedOnly;

    partial void OnUnreferencedOnlyChanged(bool value) => ApplyFilter();

    // 加载元数据（未解锁也可列出）+ 惰性补算迁移遗留条目的指纹/密钥类型。
    public async Task LoadAsync()
    {
        IReadOnlyList<VaultSecret> secrets = await _session.ListLibrarySecretsAsync();
        // 仅已解锁（或明文模式）时补算；锁定时该调用内部直接返回，不读载荷、不弹主密码框。
        await _session.BackfillLibraryMetadataAsync(secrets);
        // 补算会回写元数据，重新读取以拿到最新指纹/密钥类型。
        secrets = await _session.ListLibrarySecretsAsync();

        IReadOnlyList<Identity> identities = await _identities.GetAllAsync();
        var mapped = new List<VaultSecretListItemViewModel>(secrets.Count);
        foreach (VaultSecret secret in secrets)
        {
            int references = SecretReferences.Find(identities, secret.Id).Count;
            mapped.Add(new VaultSecretListItemViewModel(secret, references));
        }

        _allItems = mapped;
        ApplyFilter();
        // 只记录条目数与无引用数，绝不记录任何载荷内容
        _logger.LogInformation(
            "材料库加载完成 条目数={Count} 无引用={Unreferenced}",
            mapped.Count,
            mapped.Count(item => item.ReferenceCount == 0));
    }

    private void ApplyFilter()
    {
        Items = new ObservableCollection<VaultSecretListItemViewModel>(
            UnreferencedOnly ? _allItems.Where(item => item.ReferenceCount == 0) : _allItems);
    }

    // 导入私钥：弹出新建材料输入（文件 + 口令），成功后落库并刷新
    [RelayCommand]
    public Task CreatePrivateKeyAsync() => Safe.RunAsync(_logger, "新建私钥材料", async () =>
    {
        VaultSecretDraft? draft = await _interaction.PromptNewSecretAsync(privateKey: true);
        if (draft == null)
        {
            return;
        }

        VaultSecret? secret = await _session.CreateLibrarySecretFromDraftAsync(draft);
        if (secret == null)
        {
            _logger.LogInformation("材料库新建私钥取消或未落库 名称={Name}", draft.Name);
            return;
        }

        _logger.LogInformation("材料库新建私钥条目 名称={Name} 类型={Kind}", secret.Name, secret.Kind);
        await LoadAsync();
    });

    // 新建密码：弹出新建材料输入（名称 + 密码），成功后落库并刷新
    [RelayCommand]
    public Task CreatePasswordAsync() => Safe.RunAsync(_logger, "新建密码材料", async () =>
    {
        VaultSecretDraft? draft = await _interaction.PromptNewSecretAsync(privateKey: false);
        if (draft == null)
        {
            return;
        }

        VaultSecret? secret = await _session.CreateLibrarySecretFromDraftAsync(draft);
        if (secret == null)
        {
            _logger.LogInformation("材料库新建密码取消或未落库 名称={Name}", draft.Name);
            return;
        }

        _logger.LogInformation("材料库新建密码条目 名称={Name} 类型={Kind}", secret.Name, secret.Kind);
        await LoadAsync();
    });

    // 重命名：只改元数据（payload = null），不触碰密文载荷；基于库内最新条目，避免旧快照覆盖并发改名
    [RelayCommand]
    public Task RenameAsync(VaultSecretListItemViewModel item) => Safe.RunAsync(_logger, "重命名材料", async () =>
    {
        string? newName = await _interaction.PromptTextAsync(
            Strings.Get("VaultSecretLibrary.RenameTitle"),
            Strings.Get("VaultSecretLibrary.NameLabel"),
            item.Name);
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        string trimmed = newName.Trim();
        VaultSecret? latest = (await _session.ListLibrarySecretsAsync()).FirstOrDefault(secret => secret.Id == item.Id);
        if (latest == null)
        {
            _logger.LogInformation("材料库重命名跳过：条目已不存在 条目Id={SecretId}", item.Id);
            await LoadAsync();
            return;
        }

        if (trimmed == latest.Name)
        {
            return;
        }

        bool saved = await _session.SaveLibrarySecretAsync(latest with { Name = trimmed }, null);
        if (!saved)
        {
            _logger.LogInformation("材料库重命名取消（保管库未解锁）条目Id={SecretId}", item.Id);
            return;
        }

        _logger.LogInformation("材料库条目重命名 条目Id={SecretId} 类型={Kind}", item.Id, latest.Kind);
        await LoadAsync();
    });

    // 删除：存在引用则拒绝并列出引用它的身份名；无引用才真正删除
    [RelayCommand]
    public Task DeleteAsync(VaultSecretListItemViewModel item) => Safe.RunAsync(_logger, "删除材料", async () =>
    {
        // 首次引用扫描：多数情况下直接拒绝，无需解锁
        IReadOnlyList<SecretReference> refs = SecretReferences.Find(await _identities.GetAllAsync(), item.Id);
        if (refs.Count > 0)
        {
            await RejectDeleteAsync(item, refs);
            return;
        }

        // 确保写就绪：加密模式下可能弹出主密码框并等待用户输入。
        // 等待期间别处可能新增对该材料的引用，因此在真正删除前必须再扫描一次引用。
        if (!await _session.EnsureReadyForWriteAsync())
        {
            _logger.LogInformation("材料库删除取消（保管库未解锁）条目Id={SecretId}", item.Id);
            return;
        }

        refs = SecretReferences.Find(await _identities.GetAllAsync(), item.Id);
        if (refs.Count > 0)
        {
            await RejectDeleteAsync(item, refs);
            return;
        }

        bool deleted = await _session.DeleteLibrarySecretAsync(item.Id);
        if (!deleted)
        {
            _logger.LogInformation("材料库删除取消（保管库未解锁）条目Id={SecretId}", item.Id);
            return;
        }

        _logger.LogInformation("材料库条目删除 条目Id={SecretId} 类型={Kind}", item.Id, item.Secret.Kind);
        await LoadAsync();
    });

    // 引用保护：记录拒绝原因并列出引用它的身份名
    private async Task RejectDeleteAsync(VaultSecretListItemViewModel item, IReadOnlyList<SecretReference> refs)
    {
        _logger.LogInformation(
            "材料库删除被拒绝（存在引用）条目Id={SecretId} 类型={Kind} 引用数={Count}",
            item.Id,
            item.Secret.Kind,
            refs.Count);
        await _interaction.NotifyAsync(
            Strings.Get("VaultSecretLibrary.DeleteBlockedTitle"),
            string.Join("、", refs.Select(reference => reference.IdentityName).Distinct()));
    }
}
