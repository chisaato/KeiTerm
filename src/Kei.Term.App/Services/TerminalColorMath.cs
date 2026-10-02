using System;
using System.Globalization;

namespace Kei.Term.App.Services;

/// <summary>
/// 终端配色与调色器色彩空间换算工具（RGB / HSV / Hex）。
/// </summary>
public static class TerminalColorMath
{
    /// <summary>
    /// 解析 16 进制颜色字符串（支持 #RGB, #RRGGBB, #AARRGGBB，不区分大小写）。
    /// </summary>
    public static bool TryParseHex(string? hex, out byte r, out byte g, out byte b, out byte a)
    {
        r = 0;
        g = 0;
        b = 0;
        a = 255;

        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        string s = hex.Trim();
        if (s.StartsWith('#'))
        {
            s = s[1..];
        }

        if (s.Length == 3) // RGB -> RRGGBB
        {
            if (byte.TryParse(string.Concat(s[0], s[0]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                byte.TryParse(string.Concat(s[1], s[1]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                byte.TryParse(string.Concat(s[2], s[2]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                a = 255;
                return true;
            }
            return false;
        }

        if (s.Length == 6) // RRGGBB
        {
            if (byte.TryParse(s[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                byte.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                a = 255;
                return true;
            }
            return false;
        }

        if (s.Length == 8) // AARRGGBB
        {
            if (byte.TryParse(s[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a) &&
                byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                byte.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                byte.TryParse(s[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                return true;
            }
            return false;
        }

        return false;
    }

    /// <summary>
    /// 将 RGBA 通道格式化为十六进制字符串。
    /// 当 Alpha 为 255 时格式化为 #RRGGBB；否则格式化为 #AARRGGBB。
    /// </summary>
    public static string ToHex(byte r, byte g, byte b, byte a = 255)
    {
        if (a == 255)
        {
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>
    /// 将 RGB (0..255) 转换为 HSV：
    /// H 范围 0..359，S 范围 0..255，V 范围 0..255。
    /// </summary>
    public static (int H, int S, int V) RgbToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0;
        double gf = g / 255.0;
        double bf = b / 255.0;

        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;

        double h = 0.0;
        if (delta > 1e-6)
        {
            if (Math.Abs(max - rf) < 1e-6)
            {
                h = 60.0 * (((gf - bf) / delta) % 6.0);
            }
            else if (Math.Abs(max - gf) < 1e-6)
            {
                h = 60.0 * (((bf - rf) / delta) + 2.0);
            }
            else
            {
                h = 60.0 * (((rf - gf) / delta) + 4.0);
            }

            if (h < 0.0)
            {
                h += 360.0;
            }
        }

        double s = max <= 1e-6 ? 0.0 : delta / max;
        double v = max;

        // 浮点换算中的舍入调整：
        // 在将 sf/vf 缩放回 [0..255] 时使用四舍五入。
        int hInt = (int)Math.Round(h);
        if (hInt >= 360)
        {
            hInt = 0;
        }

        int sInt = (int)Math.Clamp(Math.Round(s * 255.0), 0, 255);
        int vInt = (int)Math.Clamp(Math.Round(v * 255.0), 0, 255);

        return (hInt, sInt, vInt);
    }

    /// <summary>
    /// 将 HSV (H: 0..359, S: 0..255, V: 0..255) 转换为 RGB (0..255)。
    /// </summary>
    public static (byte R, byte G, byte B) HsvToRgb(int h, int s, int v)
    {
        h = ((h % 360) + 360) % 360;
        double sf = Math.Clamp(s / 255.0, 0.0, 1.0);
        double vf = Math.Clamp(v / 255.0, 0.0, 1.0);

        double c = vf * sf;
        double hPrime = h / 60.0;
        double x = c * (1.0 - Math.Abs((hPrime % 2.0) - 1.0));
        double m = vf - c;

        double rf, gf, bf;
        if (hPrime < 1.0)
        {
            rf = c; gf = x; bf = 0.0;
        }
        else if (hPrime < 2.0)
        {
            rf = x; gf = c; bf = 0.0;
        }
        else if (hPrime < 3.0)
        {
            rf = 0.0; gf = c; bf = x;
        }
        else if (hPrime < 4.0)
        {
            rf = 0.0; gf = x; bf = c;
        }
        else if (hPrime < 5.0)
        {
            rf = x; gf = 0.0; bf = c;
        }
        else
        {
            rf = c; gf = 0.0; bf = x;
        }

        byte r = (byte)Math.Clamp(Math.Round((rf + m) * 255.0), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round((gf + m) * 255.0), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round((bf + m) * 255.0), 0, 255);

        return (r, g, b);
    }
}
