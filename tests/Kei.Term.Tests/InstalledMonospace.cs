using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Threading;
using Kei.Term.App.Services;
using RoyalTerminal.Avalonia.Controls;

namespace Kei.Term.Tests;

// 终端网格测试不能写死某一台机器上的族名。未安装的名字会让 Skia 把单元格度量成 1px，
// 视口被排成数百行，回滚断言既可能失败也可能假绿。
public static class InstalledMonospace
{
    // 与原先 TerminalHostTests 探针链一致：缺哪个就跳到 FontManager 能精确解析的下一个。
    private static readonly string[] ProbeChain =
    [
        "Cascadia Mono",
        "Cascadia Code",
        "Consolas",
        "Courier New",
        "Menlo",
        "SF Mono",
        "Monaco",
        "DejaVu Sans Mono",
        "Liberation Mono",
        "Nimbus Mono PS",
        "Noto Sans Mono",
        "Ubuntu Mono",
        "Hack",
        "Source Code Pro",
        "FreeMono",
        "Courier 10 Pitch",
        "Adwaita Mono",
        "JetBrains Mono",
        "JetBrainsMono Nerd Font",
        "JetBrainsMono Nerd Font Mono",
    ];

    private static readonly object Gate = new();
    private static bool _resolved;
    private static string? _family;

    public static string? TrySelect()
    {
        lock (Gate)
        {
            if (_resolved)
            {
                return _family;
            }
        }

        string? selected = SelectCore();
        lock (Gate)
        {
            _family = selected;
            _resolved = true;
            return _family;
        }
    }

    public static string Require()
    {
        string? family = TrySelect();
        Assert.True(
            family is not null,
            "本机没有 FontManager 能精确解析的等宽字体，无法验证终端网格。不要改用未安装的族名把测试做成假绿。");
        return family;
    }

    public static void AssertUsableCellHeight(TerminalControl terminal, string? family)
    {
        float height = terminal.Renderer?.CellHeight ?? 0f;
        Assert.True(
            height > 4f,
            $"字体“{family}”的 CellHeight={height}。缺字体时 Skia 回落到 1px，终端会被排成数百行。");
    }

    private static string? SelectCore()
    {
        string? fromChain = FirstExact(ProbeChain);
        if (fromChain is not null)
        {
            return fromChain;
        }

        // 链上全缺时，再用设置页同一套等宽关键词扫本机已安装族，并再次要求精确解析。
        List<string> recommended = new();
        foreach (FontFamilyOption option in SystemFontScanner.GetInstalledFonts())
        {
            if (option.IsMonospaceRecommended)
            {
                recommended.Add(option.Name);
            }
        }

        return FirstExact(recommended);
    }

    private static string? FirstExact(IReadOnlyList<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string family = candidate.Trim();
            if (IsExactInstalledFamily(family))
            {
                return family;
            }
        }

        return null;
    }

    // TryGetGlyphTypeface 在找不到族时可能返回别的脸。必须同时出现在 SystemFonts，且字形族名与请求一致。
    private static bool IsExactInstalledFamily(string familyName)
    {
        IFontCollection? fonts = FontManager.Current?.SystemFonts;
        if (fonts is null)
        {
            return false;
        }

        bool listed = false;
        foreach (FontFamily font in fonts)
        {
            if (string.Equals(font.Name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                listed = true;
                break;
            }
        }

        if (!listed)
        {
            return false;
        }

        if (!fonts.TryGetGlyphTypeface(
                familyName,
                FontStyle.Normal,
                FontWeight.Normal,
                FontStretch.Normal,
                out GlyphTypeface? glyphTypeface)
            || glyphTypeface is null)
        {
            return false;
        }

        if (string.Equals(glyphTypeface.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(glyphTypeface.TypographicFamilyName, familyName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string name in glyphTypeface.FamilyNames.Values)
        {
            if (string.Equals(name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

// 发现期若已有 Avalonia 会话，则按本机字体设置 Skip。平台特性表达不了“这台机器没装等宽字体”。
// 会话还没起来时不碰 Dispatcher，避免把 UI 线程抢到发现线程上。
public sealed class MonospaceFactAttribute : FactAttribute
{
    public MonospaceFactAttribute()
    {
        ApplySkip(this);
    }

    internal static void ApplySkip(FactAttribute attribute)
    {
        if (!HeadlessAvalonia.IsSessionStarted)
        {
            return;
        }

        string? family = null;
        bool probed = false;
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                family = InstalledMonospace.TrySelect();
                probed = true;
            }
            else
            {
                Task probe = HeadlessAvalonia.RunAsync(() => family = InstalledMonospace.TrySelect());
                probed = probe.Wait(TimeSpan.FromSeconds(8));
            }
        }
        catch
        {
            // 探测失败时不跳过，留给测试体内的明确失败，避免整组度量测试被静默跳过。
            return;
        }

        if (probed && family is null)
        {
            attribute.Skip = "本机没有 FontManager 能精确解析的等宽字体，跳过依赖真实单元格度量的测试";
        }
    }
}

public sealed class MonospaceTheoryAttribute : TheoryAttribute
{
    public MonospaceTheoryAttribute()
    {
        MonospaceFactAttribute.ApplySkip(this);
    }
}
