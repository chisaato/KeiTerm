using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.ViewModels;

// 代理编辑对话框。HTTP 允许保存，说明文字标明尚未接入拨号。口令不回填，也不进 ProxyProfile。
public partial class ProxyEditViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly int _sortOrder;
    private readonly DateTime _createdAt;
    private Guid? _sessionId;

    public ProxyEditViewModel(ProxyProfile? existing, int nextSortOrder = 0, bool hasSavedPassword = false)
    {
        HasSavedPassword = hasSavedPassword;
        if (existing == null)
        {
            _id = Guid.NewGuid();
            _sortOrder = nextSortOrder;
            _createdAt = DateTime.UtcNow;
            Title = Strings.Get("Proxy.Title.New");
            return;
        }

        _id = existing.Id;
        _sortOrder = existing.SortOrder;
        _createdAt = existing.CreatedAt;
        Title = Strings.Get("Proxy.Title.Edit");
        Name = existing.Name;
        switch (existing.Config)
        {
            case Socks5ProxyConfig socks:
                SelectedType = ProxyConfigKind.Socks5;
                Host = socks.Host;
                Port = socks.Port;
                Username = socks.Username ?? string.Empty;
                break;
            case HttpProxyConfig http:
                SelectedType = ProxyConfigKind.Http;
                Host = http.Host;
                Port = http.Port;
                break;
            case SessionProxyConfig session:
                SelectedType = ProxyConfigKind.Session;
                _sessionId = session.SessionId;
                break;
        }
    }

    public string Title { get; }

    public bool HasSavedPassword { get; }

    public IReadOnlyList<ProxyConfigKind> Types { get; } =
        [ProxyConfigKind.Socks5, ProxyConfigKind.Http, ProxyConfigKind.Session];

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private ProxyConfigKind _selectedType = ProxyConfigKind.Socks5;

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = 1080;

    [ObservableProperty]
    private string _sessionDisplay = string.Empty;

    [ObservableProperty]
    private string _username = string.Empty;

    // 不从库里回填。留空表示不改已存口令。
    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _clearSavedPassword;

    public bool IsEndpoint => SelectedType != ProxyConfigKind.Session;
    public bool IsSession => SelectedType == ProxyConfigKind.Session;
    public bool IsSocks5 => SelectedType == ProxyConfigKind.Socks5;
    public bool ShowClearPassword => IsSocks5 && HasSavedPassword;
    public string PasswordPlaceholder => HasSavedPassword
        ? Strings.Get("Proxy.Password.Keep")
        : string.Empty;

    public string TypeNotice => SelectedType == ProxyConfigKind.Http
        ? Strings.Get("Proxy.NotWired")
        : string.Empty;

    public bool IsConfirmed { get; private set; }
    public event Action? RequestClose;

    public Func<Task<SessionNode?>>? PickSessionAsync { get; set; }

    partial void OnSelectedTypeChanged(ProxyConfigKind value)
    {
        OnPropertyChanged(nameof(TypeNotice));
        OnPropertyChanged(nameof(IsEndpoint));
        OnPropertyChanged(nameof(IsSession));
        OnPropertyChanged(nameof(IsSocks5));
        OnPropertyChanged(nameof(ShowClearPassword));
        OnPropertyChanged(nameof(PasswordPlaceholder));
    }

    // 口令框留空不调用存储，避免把已存口令清掉。勾选清除才删除。
    public Task ApplyPasswordAsync(IProxySecretStore store, CancellationToken ct = default)
    {
        if (SelectedType != ProxyConfigKind.Socks5)
        {
            return Task.CompletedTask;
        }

        if (ClearSavedPassword)
        {
            return store.SetPasswordAsync(_id, null, ct);
        }

        if (!string.IsNullOrWhiteSpace(Password))
        {
            return store.SetPasswordAsync(_id, Password, ct);
        }

        return Task.CompletedTask;
    }

    public string TypeLabel(ProxyConfigKind kind) => kind switch
    {
        ProxyConfigKind.Http => Strings.Get("Proxy.Type.Http"),
        ProxyConfigKind.Session => Strings.Get("Proxy.Type.Session"),
        _ => Strings.Get("Proxy.Type.Socks5")
    };

    [RelayCommand]
    private async Task PickSession()
    {
        if (PickSessionAsync == null)
        {
            return;
        }

        SessionNode? picked = await PickSessionAsync();
        if (picked == null)
        {
            return;
        }

        _sessionId = picked.Id;
        SessionDisplay = picked.Name;
    }

    [RelayCommand]
    private void Save()
    {
        if (Build() == null)
        {
            return;
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

    public ProxyProfile? Build()
    {
        string name = string.IsNullOrWhiteSpace(Name) ? Host.Trim() : Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        ProxyConfig? config = SelectedType switch
        {
            ProxyConfigKind.Session => _sessionId is Guid id ? new SessionProxyConfig(id) : null,
            _ => BuildEndpoint()
        };
        if (config == null)
        {
            return null;
        }

        return new ProxyProfile
        {
            Id = _id,
            Name = name,
            SortOrder = _sortOrder,
            Config = config,
            CreatedAt = _createdAt,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private ProxyConfig? BuildEndpoint()
    {
        string host = Host.Trim();
        if (!SessionProxyExit.IsEndpoint(host, Port))
        {
            return null;
        }

        return SelectedType == ProxyConfigKind.Http
            ? new HttpProxyConfig(host, Port)
            : new Socks5ProxyConfig(host, Port, SessionProxyExit.NormalizeUsername(Username));
    }
}

public sealed class ProxyEditCommit
{
    public required ProxyProfile Profile { get; init; }
    public Func<IProxySecretStore, Task> ApplyPassword { get; init; } = _ => Task.CompletedTask;
}
