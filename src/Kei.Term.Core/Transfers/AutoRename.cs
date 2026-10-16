namespace Kei.Term.Core.Transfers;

public static class AutoRename
{
    // 从 (1) 开始找第一个不存在的名字。调用方用 exists 判断候选名，不判断原名。
    // 扩展名取最后一个点：a.tar.gz → a.tar (1).gz。无点，或以点开头且没有后续点（.bashrc），整个名字都是词干。
    public static string NextAvailable(string fileName, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (string.IsNullOrEmpty(fileName) || fileName is "." or ".."
            || fileName.Contains('/') || fileName.Contains('\\'))
        {
            throw new ArgumentException("只接受文件名，不接受空值或路径。", nameof(fileName));
        }

        (string stem, string extension) = SplitStemAndExtension(fileName);
        for (int n = 1; n < int.MaxValue; n++)
        {
            string candidate = $"{stem} ({n}){extension}";
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("没有可用的自动改名。");
    }

    public static (string Stem, string Extension) SplitStemAndExtension(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        int lastDot = fileName.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return (fileName, string.Empty);
        }

        return (fileName[..lastDot], fileName[lastDot..]);
    }
}
