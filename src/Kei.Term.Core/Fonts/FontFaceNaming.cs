using System.Globalization;
using System.Text.RegularExpressions;

namespace Kei.Term.Core.Fonts;

public enum NerdFontVariant { None, Mono, Standard, Propo }

public sealed record KonsoleFont(string Family, double PointSize, int Weight, string? StyleName);

public static class FontFaceNaming
{
    private static readonly string[] Names = ["Thin", "ExtraLight", "Light", "Regular", "Medium", "SemiBold", "Bold", "ExtraBold", "Black"];
    private static readonly int[] QtWeights = [0, 12, 25, 50, 57, 63, 75, 81, 87];

    public static NerdFontVariant Classify(string family)
    {
        if (Regex.IsMatch(family, @"\b(Nerd Font Mono|NFM)\b", RegexOptions.IgnoreCase)) return NerdFontVariant.Mono;
        if (Regex.IsMatch(family, @"\b(Nerd Font Propo|NFP)\b", RegexOptions.IgnoreCase)) return NerdFontVariant.Propo;
        return Regex.IsMatch(family, @"\b(Nerd Font|NF)\b", RegexOptions.IgnoreCase) ? NerdFontVariant.Standard : NerdFontVariant.None;
    }

    public static int RoundWeight(int weight) => Math.Clamp((int)Math.Round(weight / 100.0, MidpointRounding.AwayFromZero) * 100, 100, 900);
    public static string WeightName(int weight) => Names[RoundWeight(weight) / 100 - 1];
    public static int WeightFromName(string name) => Array.FindIndex(Names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) is >= 0 and var index ? (index + 1) * 100 : 400;

    public static int FromQtWeight(int weight)
    {
        if (weight >= 100) return Math.Clamp(weight, 100, 900);
        int closest = Enumerable.Range(0, QtWeights.Length).MinBy(i => Math.Abs(QtWeights[i] - weight));
        return (closest + 1) * 100;
    }

    public static bool TryParseKonsoleProfile(string text, out KonsoleFont? font, out string error)
    {
        font = null;
        error = "缺少 [Appearance] 中的 Font 行";
        bool appearance = false;
        string? fontLine = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('[')) appearance = line.Equals("[Appearance]", StringComparison.OrdinalIgnoreCase);
            else if (appearance && line.StartsWith("Font=", StringComparison.OrdinalIgnoreCase)) fontLine = line[5..];
        }
        if (fontLine is null) return false;
        string[] fields = fontLine.Split(',');
        error = "Font 字段损坏";
        if (fields.Length is not (10 or 11 or 16 or 17) || string.IsNullOrWhiteSpace(fields[0]) ||
            !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double size) || !double.IsFinite(size) || size <= 0 ||
            !int.TryParse(fields[4], out int weight) || weight < 0 || weight > 900) return false;
        for (int i = 2; i < (fields.Length is 11 or 17 ? fields.Length - 1 : fields.Length); i++)
            if (!double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) return false;
        font = new KonsoleFont(fields[0].Trim(), size, FromQtWeight(weight), fields.Length is 11 or 17 ? fields[^1] : null);
        error = string.Empty;
        return true;
    }
}
