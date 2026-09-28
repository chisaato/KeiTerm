using System;
using System.Globalization;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal.Theming;
using SkiaSharp;

namespace Kei.Term.App.Services;

// 终端配色适配器：TerminalProfile -> RoyalTerminal TerminalTheme
// 颜色格式约定：6 位 #RRGGBB（不透明）；8 位 #AARRGGBB（alpha 在前）
// 本类不读取也不写入 TerminalProfile 的字体字段。
public static class TerminalThemeAdapter
{
    // 构造主题：Base16 顺序 = ANSI 0..15；不触碰控件
    public static TerminalTheme ToRoyalTheme(TerminalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        TerminalProfile fallback = BuiltInPresets.GetDefaultTerminalProfile();

        uint[] base16 = new uint[16];
        for (int i = 0; i < 16; i++)
        {
            base16[i] = ResolveArgb(GetAnsiColor(profile, i), GetAnsiColor(fallback, i));
        }

        return TerminalTheme.FromBase16(
            base16,
            ResolveArgb(profile.Foreground, fallback.Foreground),
            ResolveArgb(profile.Background, fallback.Background),
            cursorColor: ResolveArgb(profile.CursorColor, fallback.CursorColor),
            // 保持 Canonical 扩展色：不承诺 truecolor/256 色随 16 色改变
            TerminalPaletteGenerationMode.Canonical,
            TerminalOscColorReportFormat.Bit16,
            selectionForeground: null,
            // 注意：TerminalTheme 构造期会 EnsureOpaque，此处 alpha 会被改写为 FF
            selectionBackground: ResolveSelectionArgb(profile),
            boldColor: null,
            cursorTextColor: null);
    }

    // 应用到真实终端控件：只做主题注入与重绘请求，不启动任何会话
    public static void Apply(TerminalControl terminal, TerminalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(profile);

        terminal.ApplyTheme(ToRoyalTheme(profile));

        // 上游 ApplyThemeToRenderer 会把选区 alpha 强制为 0x80（构造期还会先 EnsureOpaque 到 0xFF），
        // 所以 ApplyTheme 之后必须用公开的 Renderer.SelectionColor 恢复目标 ARGB
        TryReapplySelectionOverride(terminal, profile);

        terminal.InvalidateTerminal();
    }

    // 恢复被上游强制改写的选区 alpha。
    // 返回 false 表示 Renderer 尚不存在（控件未挂载）——调用方需在 Loaded 后、
    // 以及每次重新 ApplyTheme / 改 Theme / 改 DefaultForeground|Background 之后再次调用。
    // 字体设置变化不需要重调：CreateRenderer 会复制 previous.SelectionColor。
    public static bool TryReapplySelectionOverride(TerminalControl terminal, TerminalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(profile);

        if (terminal.Renderer is not { } renderer)
        {
            return false;
        }

        renderer.SelectionColor = ToSkColor(ResolveSelectionArgb(profile));
        return true;
    }

    // ARGB(0xAARRGGBB) → SKColor；SKColor(uint) 内部即 ARGB 布局，alpha 原样保留
    public static SKColor ToSkColor(uint argb) => new(argb);

    private static uint ResolveSelectionArgb(TerminalProfile profile)
        => ResolveArgb(profile.SelectionBackground, BuiltInPresets.GetDefaultTerminalProfile().SelectionBackground);

    // 严格入口：只接受 #RRGGBB 与 #AARRGGBB（大小写不敏感），其他一律拒绝且不猜测字节顺序
    public static bool TryParseArgb(string? hex, out uint argb)
    {
        argb = 0u;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        string value = hex.Trim();
        if (value.Length == 0 || value[0] != '#')
        {
            return false;
        }

        ReadOnlySpan<char> digits = value.AsSpan(1);
        if (digits.Length != 6 && digits.Length != 8)
        {
            return false;
        }

        if (!IsHexDigits(digits))
        {
            return false;
        }

        uint parsed = uint.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        // 6 位按不透明补 alpha；8 位按 #AARRGGBB 直读
        argb = digits.Length == 6
            ? 0xFF000000u | parsed
            : parsed;
        return true;
    }

    // 转换入口：严格 6/8 位之外，最小兼容既有数据曾接受的 3 位 #RGB（展开为 #RRGGBB）
    public static uint ToArgb(string hex)
    {
        if (TryParseArgb(hex, out uint argb))
        {
            return argb;
        }

        if (TryParseLegacyShortRgb(hex, out uint expanded))
        {
            return expanded;
        }

        throw new ArgumentException(
            $"Unsupported hex color '{hex}': expected #RRGGBB or #AARRGGBB.",
            nameof(hex));
    }

    // 逐色解析：优先严格格式，其次兼容 3 位旧值，最后回退到内置默认，保证渲染路径不因脏数据崩溃
    private static uint ResolveArgb(string? value, string? fallbackValue)
    {
        if (TryParseArgb(value, out uint parsed))
        {
            return parsed;
        }

        if (TryParseLegacyShortRgb(value, out uint expanded))
        {
            return expanded;
        }

        if (TryParseArgb(fallbackValue, out uint safe))
        {
            return safe;
        }

        return ToArgb(fallbackValue ?? "#000000");
    }

    private static string? GetAnsiColor(TerminalProfile profile, int index)
    {
        string[]? colors = profile.AnsiColors;
        if (colors == null || index >= colors.Length)
        {
            return null;
        }

        return colors[index];
    }

    // 3 位 #RGB（既有 Color.TryParse / 编辑弹窗正则接受）→ 每通道复制一位展开
    private static bool TryParseLegacyShortRgb(string? hex, out uint argb)
    {
        argb = 0u;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        string value = hex.Trim();
        if (value.Length != 4 || value[0] != '#')
        {
            return false;
        }

        ReadOnlySpan<char> digits = value.AsSpan(1);
        if (!IsHexDigits(digits))
        {
            return false;
        }

        uint r = HexValue(digits[0]);
        uint g = HexValue(digits[1]);
        uint b = HexValue(digits[2]);
        argb = 0xFF000000u | (r << 20) | (r << 16) | (g << 12) | (g << 8) | (b << 4) | b;
        return true;
    }

    private static bool IsHexDigits(ReadOnlySpan<char> digits)
    {
        foreach (char c in digits)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return digits.Length > 0;
    }

    private static uint HexValue(char c) => c switch
    {
        >= '0' and <= '9' => (uint)(c - '0'),
        >= 'a' and <= 'f' => (uint)(c - 'a' + 10),
        _ => (uint)(c - 'A' + 10),
    };
}
