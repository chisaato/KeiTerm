using System;
using System.Collections.Generic;
using Avalonia.Media;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Services;

namespace Kei.Term.App.Services;

// GUI 配置只存基础色；表面层次与交互状态统一从当前基础色派生。
internal static class GuiThemeTokens
{
    public static IReadOnlyDictionary<string, Color> FromProfile(GuiProfile profile)
    {
        GuiProfile fallback = BuiltInPresets.GetDefaultGuiProfile();
        Color window = Resolve(profile.WindowBackground, fallback.WindowBackground);
        Color panel = Resolve(profile.PanelBackground, fallback.PanelBackground);
        Color primary = Resolve(profile.PrimaryText, fallback.PrimaryText);
        Color secondary = Resolve(profile.SecondaryText, fallback.SecondaryText);
        Color accent = Resolve(profile.AccentColor, fallback.AccentColor);
        Color panelAlt = Resolve(profile.PanelAltBackground, fallback.PanelAltBackground);
        Color muted = ReadableMutedColor(Mix(secondary, panel, 0.25), primary, [window, panel, panelAlt]);
        return new Dictionary<string, Color>
        {
            ["Kei.Bg.Card"] = Mix(panel, window, 0.5),
            ["Kei.Bg.Input"] = window,
            ["Kei.Bg.Hover"] = Mix(panel, primary, 0.08),
            ["Kei.Bg.Pressed"] = Mix(panel, primary, 0.14),
            ["Kei.Bg.TabInactive"] = window,
            ["Kei.Text.Muted"] = muted,
            ["Kei.Status.Disconnected"] = muted,
            ["Kei.Border.Focus"] = accent,
            ["Kei.Accent.Pressed"] = Mix(accent, window, 0.18)
        };
    }

    private static Color Resolve(string value, string fallback)
        => Color.TryParse(value, out Color color) ? color : Color.Parse(fallback);

    private static Color ReadableMutedColor(Color muted, Color primary, Color[] surfaces)
    {
        // 占位符与禁用按钮都使用弱化文字；向主文字色靠拢直到各基础表面至少有 3:1 对比度。
        for (int step = 0; step <= 20; step++)
        {
            Color candidate = Mix(muted, primary, step / 20d);
            double foreground = Luminance(candidate);
            bool readable = true;
            foreach (Color surface in surfaces)
            {
                double background = Luminance(surface);
                if ((Math.Max(foreground, background) + 0.05) / (Math.Min(foreground, background) + 0.05) < 3)
                {
                    readable = false;
                    break;
                }
            }
            if (readable) return candidate;
        }
        return primary;
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static Color Mix(Color basis, Color overlay, double amount)
        => Color.FromArgb(
            Blend(basis.A, overlay.A, amount), Blend(basis.R, overlay.R, amount),
            Blend(basis.G, overlay.G, amount), Blend(basis.B, overlay.B, amount));

    private static byte Blend(byte basis, byte overlay, double amount)
        => (byte)Math.Round(basis + (overlay - basis) * amount);
}
