namespace Kei.Term.Core.Sync;

// 同步的唯一排除清单。文件按精确名/前缀列出；设置路径同时用于导出与导入防线。
public static class SyncLocalOnly
{
    public static IReadOnlyList<string> Files { get; } = Array.AsReadOnly(new[]
    {
        "window-state.json", "recent-directories.json", "quick-unlock", "sync-location", "sync-device-id", "sync-status"
    });

    public static IReadOnlyList<string> SettingPaths { get; } = Array.AsReadOnly(new[]
    {
        "WindowPosition", "WindowSize", "WindowState", "WindowBounds", "WindowWidth", "WindowHeight", "WindowX", "WindowY",
        "RecentDirectories", "RecentDirectory", "LastLocalDirectory", "LastRemoteDirectory", "QuickUnlock", "QuickUnlockRegistration",
        "Sync", "SyncLocation", "SyncDeviceId", "SyncStatus", "SyncPassphrase", "sync-location", "sync-device-id", "sync-status", "DeviceId"
    });

    public const string TreeExpansionColumn = "is_expanded";

    public static IReadOnlyList<string> DatabaseTables { get; } = Array.AsReadOnly(new[]
    {
        "vault_metadata", "local_vault_secrets", "sync_file_journal"
    });

    public static bool IsSettingExcluded(string path) => SettingPaths.Any(p =>
        path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));
}
