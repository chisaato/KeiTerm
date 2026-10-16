using System.ComponentModel;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Workspaces;

public sealed class FileManagerWorkspaceDocument : WorkspaceDocument
{
    public FileManagerWorkspaceDocument(FileManagerTabViewModel tab) : base(tab, tab.Title)
    {
        Tab = tab;
        Tab.FileManager.IsFileTab = true;
        Tab.FileManager.IsShellVisible = false;
        Tab.PropertyChanged += OnTabPropertyChanged;
    }

    public FileManagerTabViewModel Tab { get; }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(FileManagerTabViewModel.Title)) Title = Tab.Title;
    }

    public override void Detach()
    {
        Tab.PropertyChanged -= OnTabPropertyChanged;
        Tab.Detach();
    }
}
