using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using System.Collections.Generic;
using System.Linq;

namespace Kei.Term.App.ViewModels;

public partial class SessionEditViewModel
{
    public IReadOnlyList<InheritableOption<TerminalUploadProtocol>> TerminalUploadOptions { get; private set; } = [];

    [ObservableProperty]
    private InheritableOption<TerminalUploadProtocol>? _selectedTerminalUpload;

    private void InitializeTerminalUploadPreference(SessionOverrides overrides, AppSettings global)
    {
        TerminalUploadOptions = TerminalFileDropOptions.InheritableUploadProtocols(global.FileTransfer.TerminalUploadProtocol);
        SelectedTerminalUpload = TerminalUploadOptions.FirstOrDefault(option => option.Value == overrides.TerminalUploadProtocol) ?? TerminalUploadOptions[0];
    }
}
