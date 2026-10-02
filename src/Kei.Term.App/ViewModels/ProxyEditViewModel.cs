using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.App.ViewModels;

// 代理编辑对话框。HTTP 允许保存，说明文字标明尚未接入拨号。没有用户名口令字段。
public partial class ProxyEditViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly int _sortOrder;
    private readonly DateTime _createdAt;
    private Guid? _sessionId;

    public ProxyEditViewModel(ProxyProfile? existing, int nextSortOrder = 0)
    {
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

    public bool IsEndpoint => SelectedType != ProxyConfigKind.Session;
    public bool IsSession => SelectedType == ProxyConfigKind.Session;

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
            : new Socks5ProxyConfig(host, Port);
    }
}
