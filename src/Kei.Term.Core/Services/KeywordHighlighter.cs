namespace Kei.Term.Core.Services;

// 一行文本上的高亮区间。Start 为字符偏移，Length 为字符数。
public readonly record struct KeywordSpan(int Start, int Length);

// 终端关键字高亮：纯函数，不碰控件、不回写 PTY。
// 规则：不区分大小写的整词 error / fail；IPv4；http:// 或 https:// 到空白为止。
// 不重叠的都保留；重叠时更长的优先。
public static class KeywordHighlighter
{
    public static IReadOnlyList<KeywordSpan> Find(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return [];
        }

        List<KeywordSpan> found = [];
        FindWords(line, found);
        FindIpv4(line, found);
        FindUrls(line, found);
        return ResolveOverlaps(found);
    }

    private static void FindWords(string line, List<KeywordSpan> found)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (i > 0 && IsWordChar(line[i - 1]))
            {
                continue;
            }

            if (StartsWithWholeWord(line, i, "error"))
            {
                found.Add(new KeywordSpan(i, 5));
                i += 4;
            }
            else if (StartsWithWholeWord(line, i, "fail"))
            {
                found.Add(new KeywordSpan(i, 4));
                i += 3;
            }
        }
    }

    private static bool StartsWithWholeWord(string line, int index, string word)
    {
        if (index + word.Length > line.Length)
        {
            return false;
        }

        if (!line.AsSpan(index, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int after = index + word.Length;
        return after == line.Length || !IsWordChar(line[after]);
    }

    // 字母、数字、下划线都算词内字符，避免 erroring / error_code 被当成 error。
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static void FindIpv4(string line, List<KeywordSpan> found)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (!char.IsDigit(line[i]))
            {
                continue;
            }

            if (i > 0 && char.IsDigit(line[i - 1]))
            {
                continue;
            }

            if (!TryReadIpv4(line, i, out int length))
            {
                continue;
            }

            found.Add(new KeywordSpan(i, length));
            i += length - 1;
        }
    }

    private static bool TryReadIpv4(string line, int start, out int length)
    {
        length = 0;
        int index = start;
        for (int octet = 0; octet < 4; octet++)
        {
            if (octet > 0)
            {
                if (index >= line.Length || line[index] != '.')
                {
                    return false;
                }

                index++;
            }

            if (!TryReadOctet(line, ref index))
            {
                return false;
            }
        }

        // 后面还跟着 .数字 就不是四段 IPv4
        if (index < line.Length && line[index] == '.' && index + 1 < line.Length && char.IsDigit(line[index + 1]))
        {
            return false;
        }

        length = index - start;
        return length > 0;
    }

    private static bool TryReadOctet(string line, ref int index)
    {
        if (index >= line.Length || !char.IsDigit(line[index]))
        {
            return false;
        }

        int value = 0;
        int digits = 0;
        while (index < line.Length && char.IsDigit(line[index]) && digits < 3)
        {
            value = value * 10 + (line[index] - '0');
            index++;
            digits++;
        }

        if (digits == 0 || value > 255)
        {
            return false;
        }

        // 第四位数字说明这一段不是 0–255
        return index >= line.Length || !char.IsDigit(line[index]);
    }

    private static void FindUrls(string line, List<KeywordSpan> found)
    {
        int i = 0;
        while (i < line.Length)
        {
            int scheme = 0;
            if (line.AsSpan(i).StartsWith("https://", StringComparison.Ordinal))
            {
                scheme = 8;
            }
            else if (line.AsSpan(i).StartsWith("http://", StringComparison.Ordinal))
            {
                scheme = 7;
            }

            if (scheme == 0)
            {
                i++;
                continue;
            }

            int end = i + scheme;
            while (end < line.Length && !char.IsWhiteSpace(line[end]))
            {
                end++;
            }

            found.Add(new KeywordSpan(i, end - i));
            i = end;
        }
    }

    private static KeywordSpan[] ResolveOverlaps(List<KeywordSpan> found)
    {
        if (found.Count == 0)
        {
            return [];
        }

        // 更长的先占位；同长时起点靠前的先占，避免结果随扫描顺序抖动
        found.Sort(static (a, b) =>
        {
            int byLength = b.Length.CompareTo(a.Length);
            return byLength != 0 ? byLength : a.Start.CompareTo(b.Start);
        });

        List<KeywordSpan> kept = [];
        foreach (KeywordSpan span in found)
        {
            bool overlaps = false;
            foreach (KeywordSpan accepted in kept)
            {
                if (span.Start < accepted.Start + accepted.Length && accepted.Start < span.Start + span.Length)
                {
                    overlaps = true;
                    break;
                }
            }

            if (!overlaps)
            {
                kept.Add(span);
            }
        }

        kept.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return kept.ToArray();
    }
}
