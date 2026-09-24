namespace Kei.Term.Core.Models.Profiles;

public enum TabPlacement
{
    Top,
    Bottom
}

public sealed class TabBarSettings
{
    public TabPlacement Placement { get; set; } = TabPlacement.Top;

    // 连接状态色（十六进制），供左侧竖条呈现
    public string ConnectingColor { get; set; } = "#FFA500";   // 橙/黄
    public string ConnectedColor { get; set; } = "#22C55E";    // 绿
    public string DisconnectedColor { get; set; } = "#3B82F6"; // 蓝/灰
    public string ErrorColor { get; set; } = "#EF4444";        // 红
}
