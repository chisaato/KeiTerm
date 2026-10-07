using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.Core.Models;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.ViewModels.Settings;

// 设置 › 代理。表格只读，编辑走对话框。删除被引用的代理允许，连接时再失败。
public partial class ProxySettingsPage : ViewModelBase
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

    public IInteractionService Interaction { get; set; } = NullInteractionService.Instance;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnSelectedRowChanged(ProxyRow? value)
    {
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }
    private bool HasSelection() => SelectedRow != null;

    public IProxySecretStore? Secrets { get; set; }

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
    private Task Add() => RunAsync(async () =>
    {
        ProxyEditCommit? created = await Interaction.EditProxyAsync(null, false);
        if (created == null) return;
        created.Profile.SortOrder = Rows.Count == 0 ? 0 : Rows.Max(r => r.SortOrder) + 1;
        await SaveCommitAsync(created);
        await ReloadAsync();
        SelectedRow = Rows.FirstOrDefault(r => r.Id == created.Profile.Id);
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task Edit() => RunAsync(async () =>
    {
        if (SelectedRow == null) return;
        ProxyProfile? current = await _repository.GetByIdAsync(SelectedRow.Id);
        if (current == null) { await ReloadAsync(); return; }
        bool hasPassword = Secrets != null && await Secrets.HasPasswordAsync(current.Id);
        ProxyEditCommit? edited = await Interaction.EditProxyAsync(current, hasPassword);
        if (edited == null) return;
        await SaveCommitAsync(edited);
        await ReloadAsync();
    });

    private async Task SaveCommitAsync(ProxyEditCommit commit)
    {
        await _repository.SaveAsync(commit.Profile);
        if (Secrets != null) await commit.ApplyPassword(Secrets);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task Delete() => RunAsync(async () =>
    {
        if (SelectedRow == null) return;
        await _repository.DeleteAsync(SelectedRow.Id);
        await ReloadAsync();
    });

    private async Task RunAsync(Func<Task> operation)
    {
        ErrorMessage = string.Empty;
        try { await operation(); }
        // 错误区不回显异常内容，避免连接信息或口令出现在界面日志中。
        catch (Exception) { ErrorMessage = "代理操作失败，请检查本地配置数据库或凭据存储后重试。"; }
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
