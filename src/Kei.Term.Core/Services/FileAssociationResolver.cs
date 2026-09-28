namespace Kei.Term.Core.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Kei.Term.Core.Models;

public static class FileAssociationResolver
{
    // 根据文件名与关联规则列表匹配最优编辑器 ID，若无匹配返回 null
    public static Guid? ResolveEditorId(string fileName, IEnumerable<FileAssociationRule> rules)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string cleanName = Path.GetFileName(fileName);

        // 规则按 Priority 降序排列
        var sorted = rules.OrderByDescending(r => r.Priority);

        foreach (var rule in sorted)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern))
            {
                continue;
            }

            var patterns = rule.Pattern.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var pattern in patterns)
            {
                if (IsPatternMatch(cleanName, pattern))
                {
                    return rule.EditorId;
                }
            }
        }

        return null;
    }

    // 通配符匹配：支持通配符 * 与 ?，不区分大小写
    public static bool IsPatternMatch(string input, string pattern)
    {
        if (string.Equals(pattern, "*", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pattern, "*.*", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (pattern.StartsWith(".") && !pattern.Contains('*') && !pattern.Contains('?'))
        {
            return input.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);
        }

        string regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";

        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }
}
