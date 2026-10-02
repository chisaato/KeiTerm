using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;

namespace Kei.Term.App.ViewModels;

// 单条端口转发。Dynamic 不收集目标主机和端口。
public partial class PortForwardEditViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly Guid _sessionId;

    public PortForwardEditViewModel(PortForward? existing, Guid sessionId)
    {
        _sessionId = sessionId;
        if (existing == null)
        {
            _id = Guid.NewGuid();
            Title = Strings.Get("PortForward.Title.New");
            return;
        }

        _id = existing.Id;
        Title = Strings.Get("PortForward.Title.Edit");
        Name = existing.Name ?? string.Empty;
        SelectedMode = existing.Mode;
        BindAddress = existing.BindAddress;
        ListenPort = existing.ListenPort;
        DestinationHost = existing.DestinationHost ?? string.Empty;
        DestinationPort = existing.DestinationPort ?? 22;
    }

    public string Title { get; }

    public IReadOnlyList<PortForwardMode> Modes { get; } =
        [PortForwardMode.Local, PortForwardMode.Remote, PortForwardMode.Dynamic];

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private PortForwardMode _selectedMode = PortForwardMode.Local;

    [ObservableProperty]
    private string _bindAddress = "127.0.0.1";

    [ObservableProperty]
    private int _listenPort = 1080;

    [ObservableProperty]
    private string _destinationHost = string.Empty;

    [ObservableProperty]
    private int _destinationPort = 22;

    public bool NeedsDestination => SelectedMode != PortForwardMode.Dynamic;

    public bool IsConfirmed { get; private set; }
    public event Action? RequestClose;

    partial void OnSelectedModeChanged(PortForwardMode value) => OnPropertyChanged(nameof(NeedsDestination));

    public string ModeLabel(PortForwardMode mode) => mode switch
    {
        PortForwardMode.Remote => "Remote",
        PortForwardMode.Dynamic => "Dynamic",
        _ => "Local"
    };

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

    public PortForward? Build()
    {
        string bind = string.IsNullOrWhiteSpace(BindAddress) ? "127.0.0.1" : BindAddress.Trim();
        if (ListenPort is < 1 or > 65535)
        {
            return null;
        }

        if (SelectedMode != PortForwardMode.Dynamic
            && (string.IsNullOrWhiteSpace(DestinationHost) || DestinationPort is < 1 or > 65535))
        {
            return null;
        }

        return new PortForward
        {
            Id = _id,
            SessionId = _sessionId,
            Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
            Mode = SelectedMode,
            BindAddress = bind,
            ListenPort = ListenPort,
            DestinationHost = SelectedMode == PortForwardMode.Dynamic ? null : DestinationHost.Trim(),
            DestinationPort = SelectedMode == PortForwardMode.Dynamic ? null : DestinationPort
        };
    }
}
