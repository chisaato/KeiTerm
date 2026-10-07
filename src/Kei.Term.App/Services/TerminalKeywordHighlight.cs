using System;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Rendering;

namespace Kei.Term.App.Services;

// 把 Core 关键字规则接到 TerminalControl.TextHighlightRules。
// 控件钩子是按行正则，后写覆盖先写；因此短规则在前、URL 在后，尽量贴近「更长优先」。
// 精确的重叠取舍以 KeywordHighlighter 为准，这里不回写 ANSI。
public static class TerminalKeywordHighlight
{
    public static void Apply(TerminalControl terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        terminal.TextHighlightRules =
        [
            Rule("error-fail", @"(?i)(?<![A-Za-z0-9_])(?:error|fail)(?![A-Za-z0-9_])", 0xFFF48771),
            Rule("ipv4", @"(?<!\d)(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?!\.\d)", 0xFF4EC9B0),
            Rule("url", @"https?://\S+", 0xFF6CB6FF)
        ];
    }

    private static TerminalTextHighlightRule Rule(string name, string pattern, uint foreground) => new()
    {
        Name = name,
        Pattern = pattern,
        IsEnabled = true,
        Foreground = foreground,
        DarkForeground = foreground
    };
}
