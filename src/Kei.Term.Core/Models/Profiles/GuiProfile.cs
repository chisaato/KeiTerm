namespace Kei.Term.Core.Models.Profiles;

public sealed class GuiProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }

    // 核心色系定义（仅作为缺省时的 Fallback 兜底，十六进制 RGB/RGBA）
    public string WindowBackground { get; set; } = "#181818";
    public string PanelBackground { get; set; } = "#1F1F1F";
    public string PanelAltBackground { get; set; } = "#252526";
    public string BorderBrush { get; set; } = "#2D2D2D";
    public string PrimaryText { get; set; } = "#CCCCCC";
    public string SecondaryText { get; set; } = "#858585";
    public string AccentColor { get; set; } = "#0E639C";
    public string AccentHover { get; set; } = "#1177BB";
}
