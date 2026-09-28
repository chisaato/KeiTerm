using System;
using System.Collections.Generic;
using System.Globalization;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.Core.Services;

// KDE Konsole (.colorscheme) 格式解析器
public static class KonsoleColorSchemeParser
{
    private static readonly string[] DefaultAnsiFallbacks =
    [
        "#000000", "#CD3131", "#0DBC79", "#E5E510", "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5",
        "#666666", "#F14C4C", "#23D18B", "#F5F543", "#3B8EEA", "#D670D6", "#29B8DB", "#FFFFFF"
    ];

    public static TerminalProfile Parse(string content, string? defaultName = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        // 简易 INI 状态机扫描
        Dictionary<string, Dictionary<string, string>> sections = new(StringComparer.OrdinalIgnoreCase);
        string currentSection = string.Empty;

        string[] lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                if (!sections.ContainsKey(currentSection))
                {
                    sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            int eqIdx = line.IndexOf('=');
            if (eqIdx > 0 && !string.IsNullOrEmpty(currentSection))
            {
                string key = line[..eqIdx].Trim();
                string val = line[(eqIdx + 1)..].Trim();
                if (sections.TryGetValue(currentSection, out var dict))
                {
                    dict[key] = val;
                }
            }
        }

        TerminalProfile profile = new()
        {
            IsBuiltIn = false
        };

        // 1. 提取名称
        if (sections.TryGetValue("General", out var gen) &&
            gen.TryGetValue("Description", out var desc) &&
            !string.IsNullOrWhiteSpace(desc))
        {
            profile.Name = desc;
        }
        else
        {
            profile.Name = !string.IsNullOrWhiteSpace(defaultName) ? defaultName : "Konsole Imported";
        }

        // 2. 提取核心基础色
        string? bgRgb = GetColorValue(sections, "Background");
        string? fgRgb = GetColorValue(sections, "Foreground");

        profile.Background = TryFormatRgbToHex(bgRgb, "#1E1E1E");
        profile.Foreground = TryFormatRgbToHex(fgRgb, "#FFFFFF");

        // 3. 提取 ANSI 16 色 (0~7 Normal, 8~15 Intense/Bright)
        profile.AnsiColors = new string[16];
        for (int i = 0; i < 8; i++)
        {
            string? normalVal = GetColorValue(sections, $"Color{i}");
            profile.AnsiColors[i] = TryFormatRgbToHex(normalVal, DefaultAnsiFallbacks[i]);

            string? brightVal = GetColorValue(sections, $"Color{i}Intense");
            profile.AnsiColors[i + 8] = TryFormatRgbToHex(brightVal, DefaultAnsiFallbacks[i + 8]);
        }

        // 光标色与选区高亮色智能推导
        string? cursorRgb = GetColorValue(sections, "CursorColor");
        profile.CursorColor = !string.IsNullOrWhiteSpace(cursorRgb)
            ? TryFormatRgbToHex(cursorRgb, profile.Foreground)
            : profile.Foreground;

        string? selectionRgb = GetColorValue(sections, "SelectionColor");
        if (!string.IsNullOrWhiteSpace(selectionRgb))
        {
            profile.SelectionBackground = TryFormatRgbToHex(selectionRgb, "#264F78");
        }
        else
        {
            // 统一 8 位为 #AARRGGBB：alpha 在前，取蓝色槽位 RGB
            // 不得写成 #RRGGBB50（Avalonia Color.Parse 会把前两位当 alpha，导致颜色被误读）
            profile.SelectionBackground = "#50" + profile.AnsiColors[4].TrimStart('#');
        }

        return profile;
    }

    private static string? GetColorValue(Dictionary<string, Dictionary<string, string>> sections, string sectionName)
    {
        if (sections.TryGetValue(sectionName, out var dict) && dict.TryGetValue("Color", out var colorStr))
        {
            return colorStr;
        }
        return null;
    }

    private static string TryFormatRgbToHex(string? rgbStr, string fallback)
    {
        if (string.IsNullOrWhiteSpace(rgbStr))
        {
            return fallback;
        }

        string[] parts = rgbStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3 &&
            byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b))
        {
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        return fallback;
    }
}
