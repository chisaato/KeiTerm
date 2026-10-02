using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;

namespace Kei.Term.App.ViewModels.Settings;

// 设置 › 代理。表格只读，编辑走对话框。删除被引用的代理允许，连接时再失败。
public partial class ProxySettingsPage : ObservableObject
{
    private readonly IProxyRepository _repository;
    private readonly Func<IReadOnlyList<SessionNode>> _sessions;

    public ProxySettingsPage(IProxyRepository repository, Func<IReadOnlyList<SessionNode>> sessions)
    {
        _repository = repository;
        _sessions = sessions;
    }

    public ObservableCollection<ProxyRow> Rows { get; } = [];

    [ObservableProperty]
    private ProxyRow? _selectedRow;

    public Func<ProxyProfile?, Task<ProxyProfile?>>? EditProxyAsync { get; set; }

    public async Task ReloadAsync()
    {
        IReadOnlyList<ProxyProfile> proxies = await _repository.GetAllAsync();
        Dictionary<Guid, SessionNode> sessions = _sessions().ToDictionary(s => s.Id);
        Guid? selected = SelectedRow?.Id;
        Rows.Clear();
        foreach (ProxyProfile proxy in proxies)
        {
            Rows.Add(Describe(proxy, sessions));
        }

        SelectedRow = Rows.FirstOrDefault(r => r.Id == selected) ?? Rows.FirstOrDefault();
    }

    [RelayCommand]
    private async Task Add()
    {
        if (EditProxyAsync == null)
        {
            return;
        }

        ProxyProfile? created = await EditProxyAsync(null);
        if (created == null)
        {
            return;
        }

        created.SortOrder = Rows.Count == 0 ? 0 : Rows.Max(r => r.SortOrder) + 1;
        await _repository.SaveAsync(created);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task Edit()
    {
        if (EditProxyAsync == null || SelectedRow == null)
        {
            return;
        }

        ProxyProfile? current = await _repository.GetByIdAsync(SelectedRow.Id);
        if (current == null)
        {
            await ReloadAsync();
            return;
        }

        ProxyProfile? edited = await EditProxyAsync(current);
        if (edited == null)
        {
            return;
        }

        await _repository.SaveAsync(edited);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (SelectedRow == null)
        {
            return;
        }

        await _repository.DeleteAsync(SelectedRow.Id);
        await ReloadAsync();
    }

    public static ProxyRow Describe(ProxyProfile proxy, IReadOnlyDictionary<Guid, SessionNode> sessions)
    {
        string type;
        string host;
        string port;
        switch (proxy.Config)
        {
            case Socks5ProxyConfig socks:
                type = Strings.Get("Proxy.Type.Socks5");
                host = socks.Host;
                port = socks.Port.ToString();
                break;
            case HttpProxyConfig http:
                type = Strings.Get("Proxy.Type.Http");
                host = http.Host;
                port = http.Port.ToString();
                break;
            case SessionProxyConfig session:
                type = Strings.Get("Proxy.Type.Session");
                port = string.Empty;
                host = sessions.TryGetValue(session.SessionId, out SessionNode? node)
                    ? FormatSession(node)
                    : Strings.Get("SessionEdit.Deleted");
                break;
            default:
                type = Strings.Get("Proxy.NotWired");
                host = string.Empty;
                port = string.Empty;
                break;
        }

        return new ProxyRow(proxy.Id, proxy.Name, proxy.SortOrder, type, host, port);
    }

    public static string FormatSession(SessionNode session)
    {
        int port = session.Port ?? 22;
        if (string.IsNullOrWhiteSpace(session.Username))
        {
            return $"{session.Name} ({session.Host}:{port})";
        }

        return $"{session.Name} ({session.Username.Trim()}@{session.Host}:{port})";
    }
}

public sealed record ProxyRow(Guid Id, string Name, int SortOrder, string TypeLabel, string Host, string Port);
