using System;
using System.Globalization;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.Helpers;

public static class FileSizeFormatter
{
    private static readonly string[] IecUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
    private static readonly string[] SiUnits = ["B", "kB", "MB", "GB", "TB", "PB", "EB"];

    public static string Format(long bytes, FileSizeDisplayMode mode, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 0)
        {
            return "—";
        }

        if (mode == FileSizeDisplayMode.Bytes)
        {
            return bytes.ToString(culture) + " B";
        }

        int radix = mode == FileSizeDisplayMode.Si ? 1000 : 1024;
        string[] units = mode == FileSizeDisplayMode.Si ? SiUnits : IecUnits;
        decimal value = bytes;
        int unit = 0;
        while (value >= radix && unit < units.Length - 1)
        {
            value /= radix;
            unit++;
        }

        // 舍入跨过单位边界时进位，避免显示“1024 KiB”或“1000 kB”。
        if (decimal.Round(value, 2) >= radix && unit < units.Length - 1)
        {
            value /= radix;
            unit++;
        }

        return value.ToString("0.##", culture) + " " + units[unit];
    }
}
