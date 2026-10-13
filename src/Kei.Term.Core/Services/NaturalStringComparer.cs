namespace Kei.Term.Core.Services;

// 自然排序字符串比较器（无 UI 依赖）：把字符串切成「数字段」与「非数字段」，
// 数字段按数值大小比较（忽略前导零、不受位宽影响），非数字段按当前区域性、忽略大小写比较。
// 例：pve-3 < pve-11 < pve-31、file2 < file10；中文按 CurrentCulture 语义排布。
public sealed class NaturalStringComparer : IComparer<string>
{
    // 无状态、线程安全，可直接复用单例
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0;
        int j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                // 两侧同为数字：整段按数值比较，避免 "11" 排在 "3" 之前
                int xStart = i;
                int yStart = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                int numeric = CompareNumericRuns(x.AsSpan(xStart, i - xStart), y.AsSpan(yStart, j - yStart));
                if (numeric != 0)
                {
                    return numeric;
                }
            }
            else
            {
                // 否则按非数字段整体比较，大小写与中文交给区域性规则处理
                int xStart = i;
                int yStart = j;
                while (i < x.Length && !char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && !char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                int text = string.Compare(
                    x.Substring(xStart, i - xStart),
                    y.Substring(yStart, j - yStart),
                    StringComparison.CurrentCultureIgnoreCase);
                if (text != 0)
                {
                    return text;
                }
            }
        }

        // 前缀相同：短者在前
        return (x.Length - i).CompareTo(y.Length - j);
    }

    // 去前导零后先比有效位数、再逐位比较，规避 long 溢出
    private static int CompareNumericRuns(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int aIndex = 0;
        while (aIndex < a.Length - 1 && a[aIndex] == '0')
        {
            aIndex++;
        }

        int bIndex = 0;
        while (bIndex < b.Length - 1 && b[bIndex] == '0')
        {
            bIndex++;
        }

        int aLength = a.Length - aIndex;
        int bLength = b.Length - bIndex;
        if (aLength != bLength)
        {
            return aLength.CompareTo(bLength);
        }

        for (int k = 0; k < aLength; k++)
        {
            int digit = a[aIndex + k].CompareTo(b[bIndex + k]);
            if (digit != 0)
            {
                return digit;
            }
        }

        return 0;
    }
}
