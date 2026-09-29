using System.Globalization;
using System.Resources;

namespace Kei.Term.App.Helpers;

// resx 取词入口：中性语言即中文；缺失键回退键名本身，避免界面出现空串
public static class Strings
{
    private static readonly ResourceManager Rm = new("Kei.Term.App.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string key)
        => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    // 带占位符的词条：{0}、{1}…
    public static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentUICulture, Get(key), args);
}
