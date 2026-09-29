using System.Collections.Generic;

namespace Kei.Term.App.Models;

// 纯值传递的字体快照：仅表达“当前生效/当前草稿”的字体参数，
// 不是第二套持久化配置，也不代表回退链已在真实终端生效。
public sealed record TerminalFontSnapshot
{
    // 与 TerminalTabViewModel.NormalizeFontFamilyName 的默认值保持一致
    private const string DefaultPrimaryFontFamily = "Noto Sans Mono";

    public TerminalFontSnapshot(
        string fontFamily,
        IReadOnlyList<string> fallbackFonts,
        double fontSize,
        bool cursorBlink)
    {
        FontFamily = fontFamily;
        // 复制回退列表：快照不得因外部随后修改列表而“假不可变”
        FallbackFonts = fallbackFonts is null ? [] : [.. fallbackFonts];
        FontSize = fontSize;
        CursorBlink = cursorBlink;

        // 真实终端只接受单一族名；这里统一算出唯一主字体名（规则与 TerminalTabViewModel 一致）
        PrimaryFontFamily = NormalizePrimaryFontFamily(fontFamily);
    }

    public string FontFamily { get; }

    // 单一主字体族名（逗号列表第一有效项）；供真实 tab 与 Demo 统一使用
    public string PrimaryFontFamily { get; }

    public IReadOnlyList<string> FallbackFonts { get; }
    public double FontSize { get; }
    public bool CursorBlink { get; }

    // 与 TerminalTabViewModel.NormalizeFontFamilyName 相同的规则：取逗号列表第一段，空则默认单族名。
    // 该规则不构成对回退字体链的支持声明。
    public static string NormalizePrimaryFontFamily(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultPrimaryFontFamily;
        }

        string first = value.Split(',')[0].Trim();
        return first.Length > 0 ? first : DefaultPrimaryFontFamily;
    }
}
