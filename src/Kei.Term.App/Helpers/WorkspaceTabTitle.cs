using Kei.Term.Core.Services;

namespace Kei.Term.App.Helpers;

internal static class WorkspaceTabTitle
{
    public static string ForDirectory(string connectionName, string? remotePath)
    {
        if (string.IsNullOrEmpty(remotePath)) return Strings.Format("FileManager.Tab.Title", connectionName);
        string? directory = TabTitleTemplate.DirectoryName(remotePath);
        return $"{connectionName} · {directory}";
    }
}
