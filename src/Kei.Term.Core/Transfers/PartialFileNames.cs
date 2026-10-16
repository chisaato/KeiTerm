namespace Kei.Term.Core.Transfers;

public static class PartialFileNames
{
    public const string Suffix = ".keipart";

    public static string ToPartial(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            throw new ArgumentException("文件名不能为空。", nameof(fileName));
        }

        return fileName + Suffix;
    }

    // 大小写敏感。用户自己的 .KEIPART 文件不是我们的部分文件。
    public static bool IsPartial(string? fileName)
        => TryGetOriginal(fileName, out _);

    public static bool TryGetOriginal(string? fileName, out string original)
    {
        original = string.Empty;
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        if (fileName.Length == Suffix.Length)
        {
            return false;
        }

        original = fileName[..^Suffix.Length];
        return true;
    }
}
