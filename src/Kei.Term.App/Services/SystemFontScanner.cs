using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace Kei.Term.App.Services;

/// <summary>
/// 字体族选择项
/// </summary>
/// <param name="Name">字体名称</param>
/// <param name="IsMonospaceRecommended">是否为推荐置顶的等宽字体</param>
public record FontFamilyOption(string Name, bool IsMonospaceRecommended)
{
    // 下拉框或文本展示友好名称
    public string DisplayName => IsMonospaceRecommended ? $"{Name} (Mono)" : Name;

    // 类型安全的 FontFamily 实例，按原始名称构造，避免友好后缀 (Mono) 污染字体解析
    public FontFamily FontFamily { get; } = new(Name);

    public override string ToString() => Name;
}

/// <summary>
/// 系统字体动态扫描器：枚举本机已安装字体族，置顶推荐等宽字体，支持兜底保障。
/// </summary>
public static class SystemFontScanner
{
    // 常见等宽字体的关键词（命中任意一个即视为推荐等宽并置顶）
    private static readonly string[] MonospaceKeywords =
    {
        "mono",
        "code",
        "console",
        "consolas",
        "courier",
        "terminal",
        "cascadia",
        "fira",
        "jetbrains"
    };

    // 系统字体枚举失败或为空时的安全兜底候选项
    private static readonly string[] FallbackFonts =
    {
        "Cascadia Mono",
        "Consolas",
        "Courier New",
        "JetBrains Mono",
        "Fira Code",
        "monospace"
    };

    /// <summary>
    /// 获取本机已安装字体族列表，等宽字体优先置顶，其余字体按字母升序排列。
    /// </summary>
    public static IReadOnlyList<FontFamilyOption> GetInstalledFonts()
    {
        var rawNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var systemFonts = FontManager.Current?.SystemFonts;
            if (systemFonts != null)
            {
                foreach (var font in systemFonts)
                {
                    if (!string.IsNullOrWhiteSpace(font.Name))
                    {
                        rawNames.Add(font.Name.Trim());
                    }
                }
            }
        }
        catch
        {
            // 某些无头/特殊环境下 FontManager 可能抛出异常，忽略并走兜底
        }

        // 若扫描结果为空，使用兜底列表
        if (rawNames.Count == 0)
        {
            foreach (var fallback in FallbackFonts)
            {
                rawNames.Add(fallback);
            }
        }

        var options = rawNames
            .Select(name => new FontFamilyOption(name, IsRecommendedMonospace(name)))
            .OrderByDescending(f => f.IsMonospaceRecommended) // 推荐等宽置顶
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase) // 字母正序
            .ToList();

        return options;
    }

    /// <summary>
    /// 判定指定字体名是否匹配常见等宽字体特征
    /// </summary>
    public static bool IsRecommendedMonospace(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
        {
            return false;
        }

        foreach (var kw in MonospaceKeywords)
        {
            if (fontName.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
