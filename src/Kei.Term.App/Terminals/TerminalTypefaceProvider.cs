using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Kei.Term.App.Models;
using Kei.Term.Core.Settings;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Rendering;
using RoyalTerminal.Terminal;
using SkiaSharp;

namespace Kei.Term.App.Terminals;

public static class TerminalTypefaceProvider
{
    // RoyalTerminal 的 collection 模式借用 typeface，仅释放自己的 SKFont/排版缓存。
    // 缓存持有原生字体直至进程结束，不能由标签页或调用方 Dispose。
    private static readonly ConcurrentDictionary<(string Family, int Weight, SKFontStyleSlant Slant), Lazy<SKTypeface?>> Cache = new();

    internal static SKTypeface? Resolve(string family, int weight, SKFontStyleSlant slant = SKFontStyleSlant.Upright) =>
        Cache.GetOrAdd((family, weight, slant), key => new Lazy<SKTypeface?>(() =>
        {
            using SKFontStyle style = new((SKFontStyleWeight)key.Weight, SKFontStyleWidth.Normal, key.Slant);
            return SKFontManager.Default.MatchFamily(key.Family, style);
        })).Value;

    public static SKTypeface? Match(string family, int weight, SKFontStyleSlant slant = SKFontStyleSlant.Upright)
    {
        SKTypeface? face = Resolve(family, weight, slant);
        // Skia 可能对别名返回同一个托管字体对象；拒绝替代族时也不能 Dispose 共享实例。
        return face is not null && string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase) ? face : null;
    }

    public static TerminalTypefaceCollection? Create(TerminalFontSnapshot snapshot)
    {
        if (Match(snapshot.PrimaryFontFamily, snapshot.FontWeight) is null) return null;
        List<TerminalTypefaceEntry> entries = [];
        foreach (TerminalTypefaceStyle style in Enum.GetValues<TerminalTypefaceStyle>())
        {
            bool bold = style is TerminalTypefaceStyle.Bold or TerminalTypefaceStyle.BoldItalic;
            SKFontStyleSlant slant = style is TerminalTypefaceStyle.Italic or TerminalTypefaceStyle.BoldItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
            foreach (FallbackFontSetting font in new[] { new FallbackFontSetting { Family = snapshot.PrimaryFontFamily, Weight = snapshot.FontWeight } }
                .Concat(snapshot.FallbackFonts).Where(f => !string.IsNullOrWhiteSpace(f.Family)))
            {
                int weight = Math.Clamp(font.Weight ?? snapshot.FontWeight, 100, 900);
                SKTypeface? face = Match(font.Family.Trim(), bold ? Math.Clamp(weight + 300, 700, 900) : weight, slant);
                if (face is not null) entries.Add(new TerminalTypefaceEntry(face, style));
            }
        }
        return new TerminalTypefaceCollection(entries.ToArray()).WithSyntheticStyles();
    }

    public static void Apply(TerminalControl terminal, TerminalFontSnapshot snapshot)
    {
        terminal.FontFamilyName = snapshot.PrimaryFontFamily;
        terminal.TerminalFontSize = snapshot.FontSize;
        terminal.FontFamilies = new TerminalFontFamilySettings { Regular = new[] { snapshot.PrimaryFontFamily }.Concat(snapshot.FallbackFonts.Select(font => font.Family)).ToImmutableArray() };
        terminal.TypefaceCollection = Create(snapshot);
        TerminalFontMetricAdapter.AdaptCellHeight(terminal, snapshot.PrimaryFontFamily, snapshot.FontSize);
    }
}
