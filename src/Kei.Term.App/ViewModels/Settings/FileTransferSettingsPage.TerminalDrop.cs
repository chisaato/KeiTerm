using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.Core.Models;
using System.Collections.Generic;
using System.Linq;

namespace Kei.Term.App.ViewModels.Settings;

public partial class FileTransferSettingsPage
{
    public IReadOnlyList<TerminalUploadOption> TerminalUploadProtocols => TerminalFileDropOptions.UploadProtocols;
    public IReadOnlyList<TerminalDropActionOption> TerminalDropActions => TerminalFileDropOptions.DropActions;

    [ObservableProperty]
    private TerminalUploadOption _selectedTerminalUploadProtocol = TerminalFileDropOptions.UploadProtocols.Single(option => option.Protocol == TerminalUploadProtocol.Scp);

    [ObservableProperty]
    private TerminalDropActionOption _selectedTerminalDropAction = TerminalFileDropOptions.DropActions[0];

    public void SetTerminalDropPreferences(TerminalUploadProtocol protocol, TerminalFileDropAction? action)
    {
        SelectedTerminalUploadProtocol = TerminalUploadProtocols.FirstOrDefault(option => option.Protocol == protocol)
            ?? TerminalUploadProtocols.Single(option => option.Protocol == TerminalUploadProtocol.Scp);
        SelectedTerminalDropAction = TerminalDropActions.FirstOrDefault(option => option.Action == action) ?? TerminalDropActions[0];
    }
}
