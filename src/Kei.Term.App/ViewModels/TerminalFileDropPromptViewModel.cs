using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Models;

namespace Kei.Term.App.ViewModels;

public sealed record TerminalDropScopeOption(TerminalFileDropRememberScope Scope, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public partial class TerminalFileDropPromptViewModel : ViewModelBase
{
    public string Summary { get; }
    public string UploadDescription { get; }

    [ObservableProperty]
    private bool _isUpload = true;
    [ObservableProperty]
    private bool _isPastePath;
    [ObservableProperty]
    private bool _isPasteFileName;

    public IReadOnlyList<TerminalDropScopeOption> RememberScopes { get; } =
    [
        new(TerminalFileDropRememberScope.AskAgain, Strings.Get("Terminal.Drop.Remember.AskAgain")),
        new(TerminalFileDropRememberScope.Connection, Strings.Get("Terminal.Drop.Remember.Connection")),
        new(TerminalFileDropRememberScope.Global, Strings.Get("Terminal.Drop.Remember.Global"))
    ];

    [ObservableProperty]
    private TerminalDropScopeOption _selectedRememberScope;

    public TerminalFileDropPromptViewModel(IReadOnlyList<string> paths, TerminalUploadProtocol protocol, string directory)
    {
        Summary = Strings.Format("Terminal.Drop.Summary", paths.Count, string.Join("、", paths.Take(3).Select(Path.GetFileName)));
        UploadDescription = Strings.Format("Terminal.Drop.UploadDescription", TerminalFileDropOptions.Describe(protocol), directory);
        _selectedRememberScope = RememberScopes[0];
    }

    public TerminalFileDropChoice BuildChoice()
        => new(IsPastePath ? TerminalFileDropAction.PastePath
            : IsPasteFileName ? TerminalFileDropAction.PasteFileName : TerminalFileDropAction.Upload,
            SelectedRememberScope.Scope);
}
