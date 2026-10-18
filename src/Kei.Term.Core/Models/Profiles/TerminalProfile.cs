namespace Kei.Term.Core.Models.Profiles;

public sealed class TerminalProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }

    // 核心基础色（仅作为缺省时的 Fallback 兜底，前景色默认纯白）
    public string Foreground { get; set; } = "#FFFFFF";
    public string Background { get; set; } = "#1E1E1E";
    public string CursorColor { get; set; } = "#FFFFFF";
    public string SelectionBackground { get; set; } = "#264F78";

    // ANSI 16 色矩阵 (0-7 Normal, 8-15 Bright)
    public string[] AnsiColors { get; set; } = new string[16];

    // 配色深拷贝，数组独立隔离。旧 JSON 的字体字段由反序列化器忽略。
    public TerminalProfile DeepCopy() => new()
    {
        Id = Id,
        Name = Name,
        IsBuiltIn = IsBuiltIn,
        Foreground = Foreground,
        Background = Background,
        CursorColor = CursorColor,
        SelectionBackground = SelectionBackground,
        AnsiColors = (string[])AnsiColors.Clone()
    };
}
