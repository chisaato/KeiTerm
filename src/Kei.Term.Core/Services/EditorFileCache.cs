namespace Kei.Term.Core.Services;

using System.IO.Hashing;
using System.Text.Json;

// 每个监视器独占目录；关闭时才发布可清理文件清单，异常退出保留缓存供恢复。
internal sealed class EditorFileCache(string baseDirectory)
{
    private const string Prefix = "KeiTerm-editor-";
    private const string Manifest = ".keiterm-cache.json";
    private readonly Dictionary<string, string> _cleanContent = new(StringComparer.Ordinal);
    public string DirectoryPath { get; } = Path.Combine(baseDirectory, Prefix + Guid.NewGuid().ToString("N"));

    public void RecordCleanContent(string path, byte[] hash)
    {
        string relative = Path.GetRelativePath(DirectoryPath, path);
        if (IsCachePath(relative)) _cleanContent[relative] = Convert.ToHexString(hash);
    }

    public void Close()
    {
        if (_cleanContent.Count == 0) return;
        string manifest = Path.Combine(DirectoryPath, Manifest);
        File.WriteAllText(manifest + ".tmp", JsonSerializer.Serialize(_cleanContent));
        File.Move(manifest + ".tmp", manifest);
    }

    public static int ClearClosed(string baseDirectory)
    {
        if (!Directory.Exists(baseDirectory)) return 0;
        int removed = 0;
        foreach (var scope in new DirectoryInfo(baseDirectory).EnumerateDirectories(Prefix + "*"))
        {
            if (!Guid.TryParseExact(scope.Name[Prefix.Length..], "N", out _) || IsLink(scope)) continue;
            var manifest = new FileInfo(Path.Combine(scope.FullName, Manifest));
            if (!manifest.Exists || IsLink(manifest)) continue;

            Dictionary<string, string>? cleanContent;
            try { cleanContent = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest.FullName)); }
            catch (JsonException) { continue; }
            if (cleanContent == null) continue;
            bool keepManifest = false;
            foreach (var (relative, hash) in cleanContent)
            {
                if (!IsCachePath(relative)) continue;
                var file = new FileInfo(Path.Combine(scope.FullName, relative));
                var folder = file.Directory!;
                var session = folder.Parent!;
                if (IsLink(session) || IsLink(folder) || IsLink(file)) continue;
                // 暂存文件保留原名。若中断后编辑器又写了新文件，两份都留给用户。
                var recovery = new DirectoryInfo(Path.Combine(session.FullName, "Recovered edits-" + folder.Name));
                string staged = Path.Combine(recovery.FullName, file.Name);
                if (IsLink(recovery) || IsLink(new FileInfo(staged))) continue;
                if (File.Exists(staged) && !File.Exists(file.FullName)) File.Move(staged, file.FullName);
                if (!File.Exists(staged) && File.Exists(file.FullName) && Matches(file.FullName, hash))
                {
                    recovery.Create();
                    File.Move(file.FullName, staged);
                    try
                    {
                        // 移到已知路径后再校验，避免删掉编辑器在初次散列后原子替换的新版本。
                        if (!IsLink(new FileInfo(staged)) && Matches(staged, hash))
                        {
                            File.Delete(staged);
                            removed++;
                        }
                    }
                    finally
                    {
                        if (File.Exists(staged) && !File.Exists(file.FullName)) File.Move(staged, file.FullName);
                    }
                }
                if (File.Exists(staged) || File.Exists(file.FullName)) keepManifest = true;
                RemoveEmpty(recovery);
                RemoveEmpty(folder);
                RemoveEmpty(session);
            }
            if (!keepManifest)
            {
                manifest.Delete();
                RemoveEmpty(scope);
            }
        }
        return removed;
    }

    private static bool Matches(string path, string expectedHash)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = new XxHash128();
        digest.Append(stream);
        return Convert.ToHexString(digest.GetCurrentHash()) == expectedHash;
    }

    private static bool IsCachePath(string path)
    {
        string[] parts = path.Split(Path.DirectorySeparatorChar);
        return parts.Length == 3 && Guid.TryParseExact(parts[0], "N", out _)
            && parts[1].Length == 32 && parts[1].All(Uri.IsHexDigit)
            && parts[2] is not ("" or "." or "..") && Path.GetFileName(parts[2]) == parts[2];
    }

    private static bool IsLink(FileSystemInfo info) => info.LinkTarget != null;
    private static void RemoveEmpty(DirectoryInfo directory)
    {
        if (directory.Exists && !directory.EnumerateFileSystemInfos().Any()) directory.Delete();
    }
}
