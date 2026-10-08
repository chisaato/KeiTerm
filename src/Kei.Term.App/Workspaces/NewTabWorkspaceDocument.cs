namespace Kei.Term.App.Workspaces;

using Kei.Term.App.ViewModels;

public sealed class NewTabWorkspaceDocument(NewTabViewModel tab) : WorkspaceDocument(tab, tab.Title)
{
    public NewTabViewModel Tab => (NewTabViewModel)Item;
}
