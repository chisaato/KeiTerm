using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Models;
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
    private string _terminalType = "xterm-256color";

    [ObservableProperty]
    private string _startupScript = string.Empty;

    [ObservableProperty]
    private ObservableCollection<IdentityOption> _identities = [];

    [ObservableProperty]
    private IdentityOption? _selectedIdentity;

    // 文件传输协议与模式
    public IReadOnlyList<FileTransferProtocol> AvailableProtocols { get; } = [FileTransferProtocol.Sftp, FileTransferProtocol.Scp];

    [ObservableProperty]
    private FileTransferProtocol _selectedProtocol = FileTransferProtocol.Sftp;

    public IReadOnlyList<SftpChannelMode> AvailableSftpModes { get; } = [SftpChannelMode.Auto, SftpChannelMode.Subsystem, SftpChannelMode.Dedicated];

    [ObservableProperty]
    private SftpChannelMode _selectedSftpMode = SftpChannelMode.Auto;

    public Guid NodeId { get; }
    public Guid? ParentId { get; set; }
    public bool IsConfirmed { get; private set; }

    public event Action? RequestClose;

    public SessionEditViewModel(SessionNode? existing, Guid? parentId, IReadOnlyList<Identity> availableIdentities)
    {
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
            TerminalType = existing.TerminalType ?? "xterm-256color";
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
        model.TerminalType = string.IsNullOrWhiteSpace(TerminalType) ? "xterm-256color" : TerminalType.Trim();
        model.StartupScript = string.IsNullOrWhiteSpace(StartupScript) ? null : StartupScript.Trim();
        model.IdentityId = SelectedIdentity?.Id;
        model.FileTransferProtocol = SelectedProtocol;
        model.SftpMode = SelectedSftpMode;
        return model;
    }
}
