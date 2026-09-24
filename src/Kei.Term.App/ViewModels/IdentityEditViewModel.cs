using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kei.Term.App.ViewModels;

// 身份编辑器：名称/描述/用户名 + 有序方法列表（添加、删除、上下移、行内编辑）
public partial class IdentityEditViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = Strings.Get("IdentityEdit.Title.New");

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    // 预填用户名（Interactive / 单次弹窗兜底时使用）
    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private ObservableCollection<IdentityMethodRowViewModel> _methods = [];

    [ObservableProperty]
    private IdentityMethodRowViewModel? _selectedMethod;

    // 保存校验失败提示
    [ObservableProperty]
    private string? _errorMessage;

    public Guid IdentityId { get; }

    // 是否已是已存在身份：「应用」成功后新身份也转为 true，标题同步
    [ObservableProperty]
    private bool _isExisting;

    // 用户是否点击过「确定」（应用成功后关窗）
    public bool IsConfirmed { get; private set; }

    // 本次编辑器是否成功「应用」过（用于关窗后刷新列表，含点取消/标题栏关闭）
    public bool WasApplied { get; private set; }

    // 已保存的身份对象（从未应用过为 null）
    public Identity? AppliedIdentity => WasApplied ? _model : null;

    // 首次应用后的领域对象（保留 CreatedAt，后续应用在其上更新）
    private Identity? _model;

    private readonly ILogger _logger;

    public event Action? RequestClose;

    // 添加方法下拉数据源
    public IReadOnlyList<AuthMethodTypeOption> MethodTypes { get; } = new[]
    {
        new AuthMethodTypeOption("vault-password", Strings.Get("IdentityEdit.Method.VaultPassword")),
        new AuthMethodTypeOption("vault-key", Strings.Get("IdentityEdit.Method.VaultKey")),
        new AuthMethodTypeOption("file-key", Strings.Get("IdentityEdit.Method.FileKey")),
        new AuthMethodTypeOption("agent", Strings.Get("IdentityEdit.Method.Agent")),
        new AuthMethodTypeOption("interactive", Strings.Get("IdentityEdit.Method.Interactive")),
    };

    // 口令三态下拉数据源
    public IReadOnlyList<PassphraseModeOption> PassphraseModes { get; } = new[]
    {
        new PassphraseModeOption(0, Strings.Get("IdentityEdit.PassphraseMode.AlwaysAsk")),
        new PassphraseModeOption(1, Strings.Get("IdentityEdit.PassphraseMode.SessionOnly")),
        new PassphraseModeOption(2, Strings.Get("IdentityEdit.PassphraseMode.Persistent")),
    };

    // 已选中的待添加类型（ComboBox 绑定）
    [ObservableProperty]
    private AuthMethodTypeOption? _selectedMethodType;

    // 窗口注入的私钥文件选择器，转发给每行（后续新增行也自动继承）
    private Func<Task<string?>>? _filePicker;
    public Func<Task<string?>>? FilePicker
    {
        get => _filePicker;
        set
        {
            _filePicker = value;
            foreach (var row in Methods)
            {
                row.FilePicker = value;
            }
        }
    }

    // Vault 私钥导入时的口令输入（一次性，无三态）；由管理器窗口注入
    public Func<string, Task<PassphrasePromptResult?>>? VaultKeyPassphrasePrompt { get; set; }

    // Vault 私钥已存材料信息读取（字节数 + 指纹）；由管理器窗口注入
    public Func<Guid, Guid, Task<VaultKeyInfo?>>? VaultKeyInfoLoader { get; set; }

    // 身份落库（「应用」时调用）；由管理器窗口注入
    public Func<Identity, Task>? SaveIdentityAsync { get; set; }

    // Vault 私钥材料写入/删除；返回 false 表示保管库未解锁（用户取消）
    public Func<Guid, IReadOnlyList<VaultKeyImport>, Task<bool>>? PersistVaultKeysAsync { get; set; }

    public IdentityEditViewModel(Identity? existing = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _model = existing;
        IsExisting = existing != null;

        if (existing != null)
        {
            Title = Strings.Get("IdentityEdit.Title.Edit");
            IdentityId = existing.Id;
            Name = existing.Name;
            Description = existing.Description ?? string.Empty;
            Username = existing.Username ?? string.Empty;

            // 深拷贝方法，避免编辑过程中污染列表页持有的对象
            foreach (var method in existing.Methods.OrderBy(m => m.SortOrder))
            {
                AddRow(CloneMethod(method));
            }
        }
        else
        {
            IdentityId = Guid.NewGuid();
        }

        SelectedMethodType = MethodTypes[0];
        SelectedMethod = Methods.FirstOrDefault();

        _logger.LogInformation(
            "身份编辑器打开 模式={Mode} 身份Id={IdentityId} 已有方法数={Count}",
            IsExisting ? "编辑" : "新建",
            IdentityId,
            Methods.Count);
    }

    [RelayCommand]
    private void AddMethod()
    {
        if (SelectedMethodType == null)
        {
            return;
        }

        var method = CreateMethod(SelectedMethodType.Key);
        var row = AddRow(method);
        SelectedMethod = row;
    }

    [RelayCommand]
    private void RemoveMethod(IdentityMethodRowViewModel? row)
    {
        if (row == null)
        {
            return;
        }

        Methods.Remove(row);
        SelectedMethod = Methods.FirstOrDefault();
    }

    [RelayCommand]
    private void MoveUp(IdentityMethodRowViewModel? row)
    {
        if (row == null)
        {
            return;
        }

        var index = Methods.IndexOf(row);
        if (index > 0)
        {
            Methods.Move(index, index - 1);
            SelectedMethod = row;
        }
    }

    [RelayCommand]
    private void MoveDown(IdentityMethodRowViewModel? row)
    {
        if (row == null)
        {
            return;
        }

        var index = Methods.IndexOf(row);
        if (index >= 0 && index < Methods.Count - 1)
        {
            Methods.Move(index, index + 1);
            SelectedMethod = row;
        }
    }

    // 「应用」：完整保存但不关窗，可继续编辑
    [RelayCommand]
    private async Task Apply()
    {
        await ApplyCoreAsync();
    }

    // 「确定」：应用成功后关窗；失败保持打开并内联提示
    [RelayCommand]
    private async Task Confirm()
    {
        if (await ApplyCoreAsync())
        {
            IsConfirmed = true;
            RequestClose?.Invoke();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        _logger.LogInformation("身份编辑器取消 身份Id={IdentityId} 已应用过={WasApplied}", IdentityId, WasApplied);
        RequestClose?.Invoke();
    }

    // 完整保存：校验 → 身份落库 → Vault 材料写入 → 固化行内回显并清暂存标记
    private async Task<bool> ApplyCoreAsync()
    {
        if (Methods.Count == 0)
        {
            ErrorMessage = Strings.Get("IdentityEdit.Error.MethodRequired");
            _logger.LogWarning("身份应用失败 身份Id={IdentityId} 原因=无认证方法", IdentityId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = string.IsNullOrWhiteSpace(Username)
                ? Strings.Get("IdentityEdit.DefaultName")
                : string.Format(Strings.Get("IdentityEdit.DefaultNameForUserFormat"), Username.Trim());
        }

        if (SaveIdentityAsync == null)
        {
            ErrorMessage = Strings.Get("IdentityEdit.Error.SaveNotWired");
            _logger.LogError("身份应用失败 身份Id={IdentityId} 原因=保存未接线", IdentityId);
            return false;
        }

        try
        {
            // 新身份首次应用转已存在；复用 _model 保留 CreatedAt
            var model = ApplyToModel(_model);
            await SaveIdentityAsync(model);
            _model = model;

            // 身份已落库：即便随后 Vault 材料写入失败，也应视为可刷新列表
            WasApplied = true;
            IsExisting = true;
            Title = Strings.Get("IdentityEdit.Title.Edit");

            var imports = CollectVaultKeyImports();
            if (imports.Count > 0 && PersistVaultKeysAsync != null)
            {
                if (!await PersistVaultKeysAsync(model.Id, imports))
                {
                    ErrorMessage = Strings.Get("IdentityEdit.Error.VaultLocked");
                    _logger.LogWarning("身份应用部分失败 身份Id={IdentityId} 原因=保管库未解锁，私钥材料未保存", model.Id);
                    return false;
                }
            }

            foreach (var row in Methods)
            {
                row.MarkApplied();
            }

            ErrorMessage = null;
            _logger.LogInformation("身份应用成功 身份Id={IdentityId} 方法数={Count}", model.Id, model.Methods.Count);
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = string.Format(Strings.Get("IdentityEdit.Error.SaveFailedFormat"), ex.Message);
            _logger.LogError(ex, "身份应用失败 身份Id={IdentityId}", IdentityId);
            return false;
        }
    }

    // 组装 Identity：方法按当前行序回写 SortOrder 与行内字段
    public Identity ApplyToModel(Identity? target = null)
    {
        var model = target ?? new Identity { Id = IdentityId, CreatedAt = DateTime.UtcNow };
        model.Name = Name.Trim();
        model.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        model.Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();

        var methods = new List<AuthMethodEntry>();
        for (var i = 0; i < Methods.Count; i++)
        {
            var row = Methods[i];
            row.Apply();
            row.Model.SortOrder = i;
            row.Model.Enabled = true;
            methods.Add(row.Model);
        }

        model.Methods = methods;
        model.UpdatedAt = DateTime.UtcNow;
        return model;
    }

    private IdentityMethodRowViewModel AddRow(AuthMethodEntry method)
    {
        var row = new IdentityMethodRowViewModel(method)
        {
            FilePicker = _filePicker,
            VaultKeyPassphrasePrompt = VaultKeyPassphrasePrompt,
            VaultKeyInfoLoader = VaultKeyInfoLoader,
        };
        Methods.Add(row);
        return row;
    }

    // 窗口注入完成后调用：把委托下发到各行并回显已存 Vault 私钥状态
    public async Task InitializeVaultKeyStatusAsync()
    {
        // 仅已存在的身份才去 Vault 回显材料（新身份尚无 Id，避免无谓解锁）
        if (!IsExisting || IdentityId == Guid.Empty)
        {
            return;
        }

        foreach (var row in Methods)
        {
            row.VaultKeyPassphrasePrompt = VaultKeyPassphrasePrompt;
            row.VaultKeyInfoLoader = VaultKeyInfoLoader;
            await row.InitializeVaultKeyStatusAsync(IdentityId);
        }
    }

    // 收集本编辑器暂存的 Vault 私钥变更（导入/移除），供身份落库后写入
    public IReadOnlyList<VaultKeyImport> CollectVaultKeyImports()
    {
        var imports = new List<VaultKeyImport>();
        foreach (var row in Methods)
        {
            if (row.Model is not VaultPrivateKeyMethod)
            {
                continue;
            }

            if (row.IsRemovalRequested)
            {
                imports.Add(new VaultKeyImport(row.Model.Id, null, null, Remove: true));
            }
            else if (row.StagedPrivateKeyContent != null)
            {
                imports.Add(new VaultKeyImport(row.Model.Id, row.StagedPrivateKeyContent, row.StagedPassphrase, Remove: false));
            }
        }

        return imports;
    }

    private static AuthMethodEntry CreateMethod(string key) => key switch
    {
        "vault-password" => new VaultPasswordMethod(),
        "vault-key" => new VaultPrivateKeyMethod(),
        "file-key" => new FilePrivateKeyMethod(),
        "agent" => new AgentMethod(),
        "interactive" => new InteractiveMethod(),
        _ => new VaultPasswordMethod(),
    };

    private static AuthMethodEntry CloneMethod(AuthMethodEntry source) => source switch
    {
        VaultPasswordMethod => new VaultPasswordMethod { Id = source.Id, SortOrder = source.SortOrder, Enabled = source.Enabled },
        VaultPrivateKeyMethod => new VaultPrivateKeyMethod { Id = source.Id, SortOrder = source.SortOrder, Enabled = source.Enabled },
        FilePrivateKeyMethod file => new FilePrivateKeyMethod
        {
            Id = source.Id,
            SortOrder = source.SortOrder,
            Enabled = source.Enabled,
            KeyFilePath = file.KeyFilePath,
            PassphraseMode = file.PassphraseMode,
        },
        AgentMethod agent => new AgentMethod { Id = source.Id, SortOrder = source.SortOrder, Enabled = source.Enabled, AgentFingerprint = agent.AgentFingerprint },
        InteractiveMethod => new InteractiveMethod { Id = source.Id, SortOrder = source.SortOrder, Enabled = source.Enabled },
        _ => new VaultPasswordMethod { Id = source.Id, SortOrder = source.SortOrder, Enabled = source.Enabled },
    };
}
