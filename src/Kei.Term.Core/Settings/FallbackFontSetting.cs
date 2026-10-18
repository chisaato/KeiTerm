namespace Kei.Term.Core.Settings;

// null 字重表示跟随主字体；不可变值对象避免设置草稿与已提交列表共享可变条目。
public sealed record FallbackFontSetting
{
    public string Family { get; init; } = string.Empty;
    public int? Weight { get; init; }
}
