namespace Kei.Term.App.Helpers;

internal static class WorkspaceTabTitle
{
    public static string ForDirectory(string connectionName, string? remotePath)
    {
        if (string.IsNullOrEmpty(remotePath)) return Strings.Format("FileManager.Tab.Title", connectionName);
        // 远端路径始终按 '/' 分隔，不使用依赖本机操作系统的 Path API。
        string path = remotePath.TrimEnd('/');
        string directory = path.Length == 0 ? "/" : path[(path.LastIndexOf('/') + 1)..];
        return $"{connectionName} · {directory}";
    }
}
