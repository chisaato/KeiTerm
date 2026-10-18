using System.Runtime.CompilerServices;

namespace Kei.Term.Tests;

internal static class TestRepository
{
    public static string RepositoryRoot { get; } = FindRoot();

    private static string FindRoot([CallerFilePath] string sourceFile = "")
        => FindAncestor(Path.GetDirectoryName(sourceFile))
            ?? FindAncestor(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("找不到包含 Kei.Term.slnx 的仓库目录");

    private static string? FindAncestor(string? path)
    {
        DirectoryInfo? directory = string.IsNullOrEmpty(path) ? null : new DirectoryInfo(path);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Kei.Term.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }
}
