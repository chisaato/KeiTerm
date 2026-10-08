using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.ViewModels;

public partial class SessionEditViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = Strings.Get("SessionEdit.Title.New");

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private int _port = 22;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _terminalType = string.Empty;

    [ObservableProperty]
    private string _startupScript = string.Empty;

    [ObservableProperty]
    private ObservableCollection<IdentityOption> _identities = [];

    [ObservableProperty]
    private IdentityOption? _selectedIdentity;

    [ObservableProperty]
    private ObservableCollection<FirewallOption> _firewallOptions = [];

    [ObservableProperty]
    private FirewallOption? _selectedFirewall;

    // 选择会话 / 新建代理不是持久化值。由窗口注入对话框；测试直接替换这两个委托。
    public Func<SessionPickerRequest, Task<SessionNode?>>? PickSessionAsync { get; set; }
    public Func<Task<ProxyProfile?>>? RequestCreateProxyAsync { get; set; }

    // 文件传输协议与模式
    public ObservableCollection<PortForward> PortForwards { get; } = [];

    [ObservableProperty]
    private PortForward? _selectedPortForward;

    public Func<PortForward?, Task<PortForward?>>? EditPortForwardAsync { get; set; }

    public IReadOnlyList<FileTransferProtocol> AvailableProtocols { get; } = [FileTransferProtocol.Sftp, FileTransferProtocol.Scp];

    [ObservableProperty]
    private FileTransferProtocol _selectedProtocol = FileTransferProtocol.Sftp;

    public IReadOnlyList<SftpChannelMode> AvailableSftpModes { get; } = [SftpChannelMode.Auto, SftpChannelMode.Subsystem, SftpChannelMode.Dedicated];

    [ObservableProperty]
    private SftpChannelMode _selectedSftpMode = SftpChannelMode.Auto;

    // 会话行为覆盖：首项为「继承全局（当前：…）」
    public IReadOnlyList<InheritableOption<bool>> TitleFollowOptions { get; }

    [ObservableProperty]
    private InheritableOption<bool>? _selectedTitleFollow;

    public IReadOnlyList<InheritableOption<CwdFollowMode>> CwdFollowOptions { get; }

    [ObservableProperty]
    private InheritableOption<CwdFollowMode>? _selectedCwdFollow;

    // 选中的连接超时选项
    [ObservableProperty]
    private InheritableOption<int>? _selectedConnectTimeout;

    public IReadOnlyList<InheritableOption<int>> ConnectTimeoutOptions { get; }

    [ObservableProperty]
    private ObservableCollection<SettingsCategoryItem> _categories = [];

    [ObservableProperty]
    private SettingsCategoryItem? _selectedCategory;

    public Guid NodeId { get; }
    public Guid? ParentId { get; set; }
    public bool IsConfirmed { get; private set; }

    public event Action? RequestClose;

    internal IReadOnlyList<SessionNode> Sessions => _sessions;

    private readonly List<SessionNode> _sessions;
    private readonly List<ProxyProfile> _proxies;
    private readonly IReadOnlyList<TreeNodeBase> _sessionTree;
    private FirewallOption? _committed;
    private bool _suppressFirewall;

    public SessionEditViewModel(
        SessionNode? existing,
        Guid? parentId,
        IReadOnlyList<Identity> availableIdentities,
        AppSettings? globalSettings = null,
        IReadOnlyList<SessionNode>? jumpCandidates = null,
        IReadOnlyList<ProxyProfile>? proxies = null,
        IReadOnlyList<TreeNodeBase>? sessionTree = null,
        IReadOnlyList<PortForward>? portForwards = null)
    {
        AppSettings global = globalSettings ?? new AppSettings();
        TitleFollowOptions = SessionBehaviorOptions.InheritableTitleFollow(global.TabTitleFollowsRemote);
        CwdFollowOptions = SessionBehaviorOptions.InheritableCwdFollow(global.CwdFollowMode);
        ConnectTimeoutOptions = SessionBehaviorOptions.InheritableConnectTimeout(global.ConnectTimeoutSeconds);
        SessionOverrides overrides = existing?.Overrides ?? new SessionOverrides();
        SelectedTitleFollow = TitleFollowOptions.First(o => o.Value == overrides.FollowRemoteTitle);
        SelectedCwdFollow = CwdFollowOptions.First(o => o.Value == overrides.CwdFollow);
        SelectedConnectTimeout = ConnectTimeoutOptions.FirstOrDefault(o => o.Value == overrides.ConnectTimeoutSeconds) ?? ConnectTimeoutOptions[0];

        Identities.Add(new IdentityOption(null, Strings.Get("SessionEdit.IdentityNone")));
        foreach (var identity in availableIdentities)
        {
            var opt = new IdentityOption(identity.Id, identity.Name);
            Identities.Add(opt);
            if (existing?.IdentityId == identity.Id)
            {
                SelectedIdentity = opt;
            }
        }

        if (SelectedIdentity == null)
        {
            SelectedIdentity = Identities[0];
        }

        if (existing != null)
        {
            Title = Strings.Get("SessionEdit.Title.Edit");
            NodeId = existing.Id;
            ParentId = existing.ParentId;
            Name = existing.Name;
            Host = existing.Host;
            Port = existing.Port ?? 22;
            Username = existing.Username ?? string.Empty;
            Description = existing.Description ?? string.Empty;
            TerminalType = existing.TerminalType ?? string.Empty;
            StartupScript = existing.StartupScript ?? string.Empty;
            SelectedProtocol = existing.FileTransferProtocol;
            SelectedSftpMode = existing.SftpMode;
        }
        else
        {
            NodeId = Guid.NewGuid();
            ParentId = parentId;
            Port = 22;
        }

        InitCategories();
        _sessions = jumpCandidates?.ToList() ?? [];
        _proxies = proxies?.ToList() ?? [];
        _sessionTree = sessionTree ?? _sessions;
        BuildFirewallOptions(existing);
        foreach (PortForward forward in portForwards ?? [])
        {
            PortForwards.Add(CloneForward(forward));
        }
    }

    public IReadOnlyList<PortForward> CollectForwards()
        => PortForwards.Select(CloneForward).ToList();

    [RelayCommand]
    private async Task AddPortForward()
    {
        if (EditPortForwardAsync == null)
        {
            return;
        }

        PortForward? created = await EditPortForwardAsync(null);
        if (created == null)
        {
            return;
        }

        created.SessionId = NodeId;
        if (Conflicts(created, null))
        {
            return;
        }

        PortForwards.Add(created);
    }

    [RelayCommand]
    private async Task EditPortForward(PortForward? current)
    {
        current ??= SelectedPortForward;
        if (EditPortForwardAsync == null || current == null)
        {
            return;
        }

        PortForward? edited = await EditPortForwardAsync(current);
        if (edited == null)
        {
            return;
        }

        if (Conflicts(edited, current.Id))
        {
            return;
        }

        int index = PortForwards.IndexOf(current);
        if (index >= 0)
        {
            PortForwards[index] = edited;
        }
    }

    [RelayCommand]
    private void DeletePortForward(PortForward? current)
    {
        current ??= SelectedPortForward;
        if (current != null)
        {
            PortForwards.Remove(current);
        }
    }

    private bool Conflicts(PortForward candidate, Guid? ignoreId)
    {
        return PortForwards.Any(existing =>
            existing.Id != ignoreId
            && existing.Id != candidate.Id
            && string.Equals(existing.BindAddress, candidate.BindAddress, StringComparison.OrdinalIgnoreCase)
            && existing.ListenPort == candidate.ListenPort
            && existing.Mode == candidate.Mode);
    }

    private static PortForward CloneForward(PortForward source) => new()
    {
        Id = source.Id,
        SessionId = source.SessionId,
        Name = source.Name,
        Mode = source.Mode,
        BindAddress = source.BindAddress,
        ListenPort = source.ListenPort,
        DestinationHost = source.Mode == PortForwardMode.Dynamic ? null : source.DestinationHost,
        DestinationPort = source.Mode == PortForwardMode.Dynamic ? null : source.DestinationPort
    };

    private void InitCategories()
    {
        Categories =
        [
            new SettingsCategoryItem(Strings.Get("SessionEdit.Category.Connection"), null, "Connection", SettingsIcons.Ssh),
            new SettingsCategoryItem(Strings.Get("SessionEdit.Category.Terminal"), null, "Terminal", SettingsIcons.Terminal),
            new SettingsCategoryItem(Strings.Get("SessionEdit.Category.FileTransfer"), null, "FileTransfer", SettingsIcons.FileTransfer),
            new SettingsCategoryItem(Strings.Get("SessionEdit.Category.Behavior"), null, "Behavior", SettingsIcons.General),
            new SettingsCategoryItem(Strings.Get("SessionEdit.Category.Ports"), null, "Ports", SettingsIcons.Ssh)
        ];
        SelectedCategory = Categories[0];
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = string.IsNullOrWhiteSpace(Host) ? Strings.Get("SessionEdit.DefaultName") : Host;
        }

        IsConfirmed = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        RequestClose?.Invoke();
    }

    public SessionNode ApplyToModel(SessionNode? target = null)
    {
        var model = target ?? new SessionNode { Id = NodeId, ParentId = ParentId };
        model.Name = Name;
        model.Host = Host.Trim();
        model.Port = Port;
        model.Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        model.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        model.TerminalType = string.IsNullOrWhiteSpace(TerminalType) ? string.Empty : TerminalType.Trim();
        model.StartupScript = string.IsNullOrWhiteSpace(StartupScript) ? null : StartupScript.Trim();
        model.IdentityId = SelectedIdentity?.Id;
        ApplyFirewall(model);
        model.FileTransferProtocol = SelectedProtocol;
        model.SftpMode = SelectedSftpMode;
        // 只改本窗口管理的覆盖项，其余覆盖项（如批量修改设置的新项）原样保留
        model.Overrides.FollowRemoteTitle = SelectedTitleFollow?.Value;
        model.Overrides.CwdFollow = SelectedCwdFollow?.Value;
        model.Overrides.ConnectTimeoutSeconds = SelectedConnectTimeout?.Value;
        return model;
    }

    private void BuildFirewallOptions(SessionNode? existing)
    {
        FirewallOptions.Clear();
        FirewallOptions.Add(new FirewallOption(FirewallChoiceKind.None, null, Strings.Get("SessionEdit.FirewallNone")));
        foreach (ProxyProfile proxy in _proxies.OrderBy(p => p.SortOrder).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            FirewallOptions.Add(new FirewallOption(FirewallChoiceKind.Proxy, proxy.Id, proxy.Name));
        }

        FirewallOptions.Add(new FirewallOption(FirewallChoiceKind.ChooseSession, null, Strings.Get("SessionEdit.ChooseSession")));
        FirewallOptions.Add(new FirewallOption(FirewallChoiceKind.CreateProxy, null, Strings.Get("SessionEdit.CreateProxy")));

        FirewallOption selected = FirewallOptions[0];
        if (existing?.ProxyProfileId is Guid proxyId)
        {
            selected = FirewallOptions.FirstOrDefault(o => o.Kind == FirewallChoiceKind.Proxy && o.Id == proxyId)
                ?? AddExtra(new FirewallOption(FirewallChoiceKind.DeletedProxy, proxyId, Strings.Get("SessionEdit.Deleted")));
        }
        else if (existing?.JumpHostSessionId is Guid sessionId)
        {
            SessionNode? stillThere = _sessions.FirstOrDefault(s => s.Id == sessionId);
            selected = stillThere == null
                ? AddExtra(new FirewallOption(FirewallChoiceKind.DeletedSession, sessionId, Strings.Get("SessionEdit.Deleted")))
                : AddExtra(new FirewallOption(FirewallChoiceKind.ChosenSession, sessionId, stillThere.Name));
        }

        SelectFirewall(selected);
    }

    private FirewallOption AddExtra(FirewallOption option)
    {
        FirewallOptions.Add(option);
        return option;
    }

    partial void OnSelectedFirewallChanged(FirewallOption? value)
    {
        if (_suppressFirewall || value == null)
        {
            return;
        }

        if (value.Kind == FirewallChoiceKind.ChooseSession)
        {
            _ = ChooseSessionAsync();
            return;
        }

        if (value.Kind == FirewallChoiceKind.CreateProxy)
        {
            _ = CreateProxyAsync();
            return;
        }

        _committed = value;
    }

    public async Task ChooseSessionAsync()
    {
        FirewallOption? previous = _committed ?? FirewallOptions.FirstOrDefault(o => o.Kind == FirewallChoiceKind.None);
        SessionNode? picked = PickSessionAsync == null
            ? null
            : await PickSessionAsync(new SessionPickerRequest(NodeId, _sessions, _sessionTree));
        if (picked == null)
        {
            SelectFirewall(previous);
            return;
        }

        FirewallOption? previousChoice = FirewallOptions.FirstOrDefault(o => o.Kind is FirewallChoiceKind.ChosenSession or FirewallChoiceKind.DeletedSession);
        if (previousChoice != null)
        {
            FirewallOptions.Remove(previousChoice);
        }
        var option = new FirewallOption(FirewallChoiceKind.ChosenSession, picked.Id, picked.Name);
        FirewallOptions.Add(option);
        SelectFirewall(option);
    }

    public async Task CreateProxyAsync()
    {
        FirewallOption? previous = _committed ?? FirewallOptions.FirstOrDefault(o => o.Kind == FirewallChoiceKind.None);
        ProxyProfile? created = RequestCreateProxyAsync == null ? null : await RequestCreateProxyAsync();
        if (created == null)
        {
            SelectFirewall(previous);
            return;
        }

        _proxies.RemoveAll(p => p.Id == created.Id);
        _proxies.Add(created);
        FirewallOption? existing = FirewallOptions.FirstOrDefault(o => o.Kind == FirewallChoiceKind.Proxy && o.Id == created.Id);
        if (existing != null)
        {
            FirewallOptions.Remove(existing);
        }

        var option = new FirewallOption(FirewallChoiceKind.Proxy, created.Id, created.Name);
        int insertAt = FirewallOptions.ToList().FindIndex(o => o.Kind is FirewallChoiceKind.ChooseSession or FirewallChoiceKind.CreateProxy);
        if (insertAt < 0)
        {
            FirewallOptions.Add(option);
        }
        else
        {
            FirewallOptions.Insert(insertAt, option);
        }

        SelectFirewall(option);
    }

    private void SelectFirewall(FirewallOption? option)
    {
        _suppressFirewall = true;
        SelectedFirewall = option;
        _committed = option;
        _suppressFirewall = false;
    }

    private void ApplyFirewall(SessionNode model)
    {
        FirewallOption? choice = _committed ?? SelectedFirewall;
        switch (choice?.Kind)
        {
            case FirewallChoiceKind.Proxy:
            case FirewallChoiceKind.DeletedProxy:
                model.ProxyProfileId = choice.Id;
                model.JumpHostSessionId = null;
                break;
            case FirewallChoiceKind.ChosenSession:
            case FirewallChoiceKind.DeletedSession:
                model.JumpHostSessionId = choice.Id;
                model.ProxyProfileId = null;
                break;
            default:
                model.ProxyProfileId = null;
                model.JumpHostSessionId = null;
                break;
        }
    }
}

public enum FirewallChoiceKind
{
    None,
    Proxy,
    ChooseSession,
    CreateProxy,
    DeletedProxy,
    DeletedSession,
    ChosenSession
}

public sealed record FirewallOption(FirewallChoiceKind Kind, Guid? Id, string DisplayName);

public sealed record SessionPickerRequest(
    Guid EditingId,
    IReadOnlyList<SessionNode> Sessions,
    IReadOnlyList<TreeNodeBase> Roots);
