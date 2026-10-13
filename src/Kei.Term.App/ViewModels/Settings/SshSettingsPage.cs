using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Security;
using Kei.Term.Core.Storage;

namespace Kei.Term.App.ViewModels.Settings;

// 「SSH」分类页：连接、默认身份、保活与 Agent
public partial class SshSettingsPage : ViewModelBase
{
    private readonly IIdentityRepository? _identityRepo;

    public GeneralSettingsPage General { get; }

    // 新会话默认 SSH 端口
    [ObservableProperty]
    private int _defaultPort = 22;

    // 全局默认用户名（会话未填时的回退）
    [ObservableProperty]
    private string _defaultUsername = string.Empty;

    // 全局默认身份下拉数据源（首项为「无」）
    [ObservableProperty]
    private ObservableCollection<IdentityOption> _identities = [];

    // 当前选中的全局默认身份（null = 无，连接时提示）
    [ObservableProperty]
    private IdentityOption? _selectedIdentity;

    // 选中的身份 Id（与设置项直接对应，加载列表后回映射到 SelectedIdentity）
    [ObservableProperty]
    private Guid? _selectedIdentityId;

    // 优先尝试系统 ssh-agent
    [ObservableProperty]
    private bool _preferSystemAgent = true;

    // 建立 SSH 连接的最长等待时间（秒）
    [ObservableProperty]
    private int _connectTimeoutSeconds = 60;

    // SSH 保活心跳间隔（秒），0 表示禁用
    [ObservableProperty]
    private int _keepAliveIntervalSeconds = 30;

    // 是否在远端会话中转发本地 SSH-Agent
    [ObservableProperty]
    private bool _enableAgentForwarding = false;

    // 自定义 Agent Socket/Pipe 路径，空表示自动检测
    [ObservableProperty]
    private string _customAgentSocketPath = string.Empty;

    // 主机密钥校验策略下拉
    public IReadOnlyList<HostKeyPolicyOption> HostKeyPolicies { get; } =
    [
        new(HostKeyPolicy.Ask, Strings.Get("Settings.Ssh.HostKeyPolicy.Ask")),
        new(HostKeyPolicy.AcceptNew, Strings.Get("Settings.Ssh.HostKeyPolicy.AcceptNew")),
        new(HostKeyPolicy.Strict, Strings.Get("Settings.Ssh.HostKeyPolicy.Strict"))
    ];

    [ObservableProperty]
    private HostKeyPolicyOption? _selectedHostKeyPolicy;

    // 与设置项直接对应；未匹配到选项时按默认 Ask 处理
    public HostKeyPolicy HostKeyPolicy
    {
        get => SelectedHostKeyPolicy?.Policy ?? HostKeyPolicy.Ask;
        set => SelectedHostKeyPolicy = HostKeyPolicies.FirstOrDefault(o => o.Policy == value) ?? HostKeyPolicies[0];
    }

    public SshSettingsPage() : this(null)
    {
    }

    public SshSettingsPage(IIdentityRepository? identityRepo, GeneralSettingsPage? general = null)
    {
        _identityRepo = identityRepo;
        General = general ?? new GeneralSettingsPage(string.Empty);
    }

    // 加载身份列表；首项「无」值 null
    public async Task LoadIdentitiesAsync()
    {
        if (_identityRepo == null)
        {
            return;
        }

        var list = await _identityRepo.GetAllAsync();
        var options = new ObservableCollection<IdentityOption>
        {
            new(null, Strings.Get("Settings.Ssh.IdentityNone"))
        };
        foreach (var identity in list)
        {
            options.Add(new IdentityOption(identity.Id, identity.Name));
        }
        Identities = options;

        // 按已选 Id 恢复选中项，找不到则回落到首项「无」
        SelectedIdentity = options.FirstOrDefault(o => o.Id == SelectedIdentityId) ?? options[0];
    }

    // 下拉选中项变化时同步 Id，保存时直接取用
    partial void OnSelectedIdentityChanged(IdentityOption? value)
    {
        SelectedIdentityId = value?.Id;
    }
}

// 全局默认身份下拉项（null = 不绑定）
public sealed record IdentityOption(Guid? Id, string DisplayName);

public sealed record HostKeyPolicyOption(HostKeyPolicy Policy, string DisplayName);
