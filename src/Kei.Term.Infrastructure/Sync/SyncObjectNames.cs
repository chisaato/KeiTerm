namespace Kei.Term.Infrastructure.Sync;

internal static class SyncObjectNames
{
    public const string PackageFileName = "keiterm-sync.age";

    public static void ValidateHistoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') || name is "." or "..")
            throw new ArgumentException("无效历史同步包名称", nameof(name));
    }
}
