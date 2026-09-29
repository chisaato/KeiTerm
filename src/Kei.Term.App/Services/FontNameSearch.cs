using System;
using System.Collections.Generic;
using System.Linq;

namespace Kei.Term.App.Services;

/// <summary>
/// 纯静态字体名称搜索匹配器，支持多 token 包含匹配。
/// </summary>
public static class FontNameSearch
{
    // 输入搜索词与目标字符串，按空格切分多 token，全部命中返回 true（顺序无关、大小写不敏感、子串匹配）
    public static bool IsMatch(string? pattern, string? target)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return true;
        }

        foreach (var token in tokens)
        {
            if (!target.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // 过滤列表便捷方法
    public static IEnumerable<T> Filter<T>(IEnumerable<T>? source, string? pattern, Func<T, string?> nameSelector)
    {
        if (source == null) return Enumerable.Empty<T>();
        if (string.IsNullOrWhiteSpace(pattern)) return source;
        return source.Where(item => IsMatch(pattern, nameSelector(item)));
    }
}
