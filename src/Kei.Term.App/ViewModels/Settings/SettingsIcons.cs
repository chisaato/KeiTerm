namespace Kei.Term.App.ViewModels.Settings;

// 分类图标：16×16 画布上的极简单色描边几何数据，禁止彩色 emoji
public static class SettingsIcons
{
    // 常规：滑杆（三根竖轨 + 可拖动节点）
    public const string General =
        "M4,2.5 L4,13.5 M8,2.5 L8,13.5 M12,2.5 L12,13.5 " +
        "M2.2,6 A1.8,1.8 0 1 0 5.8,6 A1.8,1.8 0 1 0 2.2,6 Z " +
        "M6.2,10.5 A1.8,1.8 0 1 0 9.8,10.5 A1.8,1.8 0 1 0 6.2,10.5 Z " +
        "M10.2,4 A1.8,1.8 0 1 0 13.8,4 A1.8,1.8 0 1 0 10.2,4 Z";

    // 外观：显示器 + 支架
    public const string Appearance =
        "M2,3.5 L14,3.5 L14,11 L2,11 Z M8,11 L8,13.5 M5.5,13.5 L10.5,13.5";

    // 终端：窗口 + 提示符箭头 + 光标线
    public const string Terminal =
        "M1.5,3 L14.5,3 L14.5,13 L1.5,13 Z M4.5,6.5 L7,8.5 L4.5,10.5 M9,11 L12,11";

    // 会话树：层级分支
    public const string Tree =
        "M3,3 L3,13 M3,8 L7,8 M3,13 L7,13 M7,6.5 L13,6.5 L13,9.5 L7,9.5 Z M7,11.5 L13,11.5 L13,14.5 L7,14.5 Z";

    // 标签栏：横向标签页
    public const string TabBar =
        "M2,4 L14,4 L14,12 L2,12 Z M2,7 L14,7 M6,4 L6,7 M10,4 L10,7";

    // SSH：挂锁
    public const string Ssh =
        "M5.5,7 L5.5,5.2 A2.5,2.5 0 0 1 10.5,5.2 L10.5,7 " +
        "M3.8,7 L12.2,7 L12.2,12.3 L3.8,12.3 Z M8,9 L8,10.6";

    // 文件传输 / 编辑器：文件夹与双向同步/编辑小箭头
    public const string FileTransfer =
        "M2,4 L6.5,4 L8,5.5 L14,5.5 L14,12.5 L2,12.5 Z " +
        "M5,9 L11,9 M9.5,7.5 L11,9 L9.5,10.5";
}
